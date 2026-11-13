using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>A supplier invoice file read into text cells: one header row plus data rows.</summary>
public sealed record PurchaseImportTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public string Cell(int row, int column) =>
        column >= 0 && column < Rows[row].Count ? Rows[row][column].Trim() : string.Empty;
}

/// <summary>One parsed invoice row before it is matched to an item (packs as on the bill, money in paisa).</summary>
public sealed class PurchaseImportRow
{
    public int RowNumber { get; init; }
    public string ProductName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string? HsnCode { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }
    public int? UnitsPerPack { get; set; }
    public decimal QuantityPacks { get; set; }
    public decimal FreePacks { get; set; }
    public long MrpPaisa { get; set; }
    public long RatePaisa { get; set; }
    public long PtrPaisa { get; set; }
    public long SaleRatePaisa { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal SchemePercent { get; set; }
    public decimal? GstPercent { get; set; }

    /// <summary>Problems that stop the row from being imported (shown on the mapping screen).</summary>
    public List<string> Errors { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads supplier invoice CSV text and maps its columns (spec 04 "Import CSV" with column mapping; the mapping is
/// saved per distributor, "This is a One-Time Process Only"). Pure: file access lives in Infrastructure.
/// </summary>
public static partial class PurchaseImportParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly (PurchaseImportField Field, string[] Names)[] Synonyms =
    {
        (PurchaseImportField.ProductName, new[] { "product", "productname", "item", "itemname", "itemdescription", "description", "medicine", "medicinename", "name", "particulars" }),
        (PurchaseImportField.Barcode, new[] { "barcode", "ean", "upc" }),
        (PurchaseImportField.HsnCode, new[] { "hsn", "hsncode", "hsnsac" }),
        (PurchaseImportField.BatchNumber, new[] { "batch", "batchno", "batchnumber", "bno" }),
        (PurchaseImportField.Expiry, new[] { "exp", "expiry", "expirydate", "expdate", "expdt" }),
        (PurchaseImportField.UnitsPerPack, new[] { "pack", "packing", "packsize", "unitsperpack", "pk" }),
        (PurchaseImportField.Quantity, new[] { "qty", "quantity", "billedqty", "packs" }),
        (PurchaseImportField.FreeQuantity, new[] { "free", "freeqty", "fqty", "bonus", "sch qty", "schqty" }),
        (PurchaseImportField.Mrp, new[] { "mrp", "mrpperpack" }),
        (PurchaseImportField.Rate, new[] { "rate", "purchaserate", "purcrate", "prate", "netrate", "cost" }),
        (PurchaseImportField.Ptr, new[] { "ptr", "pricetoretailer" }),
        (PurchaseImportField.SaleRate, new[] { "salerate", "sellingprice", "retprice", "retailprice", "srate" }),
        (PurchaseImportField.DiscountPercent, new[] { "disc", "discount", "disc%", "discountpercent", "cd", "cd%" }),
        (PurchaseImportField.SchemePercent, new[] { "sch%", "scheme%", "schemepercent", "schpercent" }),
        (PurchaseImportField.GstPercent, new[] { "gst", "gst%", "gstrate", "tax", "tax%", "taxrate", "igst%" })
    };

    /// <summary>Parses CSV / semicolon / tab separated text (quoted fields, doubled quotes, CRLF). Blank lines are skipped.</summary>
    public static PurchaseImportTable ParseDelimited(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();

        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }

            if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                record.Add(field.ToString()); field.Clear();
                records.Add(record); record = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }

        var nonEmpty = records.Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        if (nonEmpty.Count == 0) return new PurchaseImportTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        var headers = nonEmpty[0].Select(h => h.Trim()).ToList();
        return new PurchaseImportTable(headers, nonEmpty.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList());
    }

    private static string Normalize(string header) =>
        new string(header.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '%').ToArray());

    /// <summary>Best guess of each column's meaning from its header; every field is used at most once.</summary>
    public static IReadOnlyList<PurchaseImportField> SuggestMapping(IReadOnlyList<string> headers)
    {
        var result = Enumerable.Repeat(PurchaseImportField.Ignore, headers.Count).ToArray();
        var used = new HashSet<PurchaseImportField>();
        // Exact synonym matches first, then "contains" matches, so "Free Qty" is not taken as Qty.
        foreach (var exact in new[] { true, false })
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (result[i] != PurchaseImportField.Ignore) continue;
                var h = Normalize(headers[i]);
                if (h.Length == 0) continue;
                foreach (var (field, names) in Synonyms)
                {
                    if (used.Contains(field)) continue;
                    var hit = exact
                        ? names.Any(n => Normalize(n) == h)
                        : names.Any(n => Normalize(n).Length >= 3 && h.Contains(Normalize(n), StringComparison.Ordinal));
                    if (!hit) continue;
                    result[i] = field;
                    used.Add(field);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Converts every data row with the chosen mapping. Rows with errors are returned too (flagged).</summary>
    public static IReadOnlyList<PurchaseImportRow> MapRows(PurchaseImportTable table, IReadOnlyList<PurchaseImportField> mapping, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(mapping);
        var missing = new[] { PurchaseImportField.ProductName, PurchaseImportField.BatchNumber, PurchaseImportField.Expiry,
            PurchaseImportField.Quantity, PurchaseImportField.Mrp, PurchaseImportField.Rate }
            .Where(f => !mapping.Contains(f)).ToList();
        if (missing.Count > 0)
            throw new PurchaseRuleException("Map these columns before importing: " + string.Join(", ", missing.Select(f => f.ToLabel())));

        int Col(PurchaseImportField f) => mapping.ToList().IndexOf(f);
        var rows = new List<PurchaseImportRow>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            string Get(PurchaseImportField f) => table.Cell(r, Col(f));
            var name = Get(PurchaseImportField.ProductName);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(Get(PurchaseImportField.BatchNumber))) continue; // footer / blank

            var row = new PurchaseImportRow
            {
                RowNumber = r + 2, // header is row 1 in the file
                ProductName = name,
                Barcode = NullIfEmpty(Get(PurchaseImportField.Barcode)),
                HsnCode = NullIfEmpty(Get(PurchaseImportField.HsnCode)),
                BatchNumber = Get(PurchaseImportField.BatchNumber)
            };

            if (string.IsNullOrWhiteSpace(row.ProductName)) row.Errors.Add("Product name is empty.");
            if (string.IsNullOrWhiteSpace(row.BatchNumber)) row.Errors.Add("Batch no is empty.");

            var expiryText = Get(PurchaseImportField.Expiry);
            row.ExpiryDate = ParseExpiry(expiryText);
            if (row.ExpiryDate == null) row.Errors.Add($"Expiry '{expiryText}' is not a date (use MM/YY).");
            else if (row.ExpiryDate.Value.Date < today.Date) row.Errors.Add("Batch is expired.");

            if (Col(PurchaseImportField.UnitsPerPack) >= 0)
            {
                var packText = Get(PurchaseImportField.UnitsPerPack);
                row.UnitsPerPack = ParsePack(packText);
                if (packText.Length > 0 && row.UnitsPerPack == null) row.Errors.Add($"Pack '{packText}' is not a number.");
            }

            row.QuantityPacks = Number(Get(PurchaseImportField.Quantity), "Qty", row, required: true);
            row.FreePacks = Number(Get(PurchaseImportField.FreeQuantity), "Free", row);
            if (row.QuantityPacks + row.FreePacks <= 0) row.Errors.Add("Quantity is zero.");
            if (row.QuantityPacks != Math.Floor(row.QuantityPacks)) row.Errors.Add("Qty must be whole packs.");
            row.MrpPaisa = PurchaseCalculator.ToPaisa(Number(Get(PurchaseImportField.Mrp), "MRP", row, required: true));
            row.RatePaisa = PurchaseCalculator.ToPaisa(Number(Get(PurchaseImportField.Rate), "Rate", row, required: true));
            row.PtrPaisa = PurchaseCalculator.ToPaisa(Number(Get(PurchaseImportField.Ptr), "PTR", row));
            row.SaleRatePaisa = PurchaseCalculator.ToPaisa(Number(Get(PurchaseImportField.SaleRate), "Sale rate", row));
            row.DiscountPercent = Number(Get(PurchaseImportField.DiscountPercent), "Disc %", row);
            row.SchemePercent = Number(Get(PurchaseImportField.SchemePercent), "Scheme %", row);
            var gstText = Get(PurchaseImportField.GstPercent);
            if (gstText.Length > 0) row.GstPercent = Number(gstText, "GST %", row);

            if (row.MrpPaisa <= 0) row.Errors.Add("MRP is missing.");
            if (row.DiscountPercent is < 0 or > 100 || row.SchemePercent is < 0 or > 100) row.Errors.Add("Discount must be 0–100%.");
            if (row.GstPercent is < 0 or > 28) row.Errors.Add("GST % must be 0–28.");
            rows.Add(row);
        }
        return rows;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static decimal Number(string text, string label, PurchaseImportRow row, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required) row.Errors.Add($"{label} is empty.");
            return 0m;
        }
        var cleaned = text.Replace("₹", "").Replace("Rs.", "", StringComparison.OrdinalIgnoreCase).Replace("Rs", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%", "").Replace(",", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Number, Invariant, out var value)) return value;
        row.Errors.Add($"{label} '{text}' is not a number.");
        return 0m;
    }

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitsRegex();

    /// <summary>Pack text such as "10", "10's", "1x15", "15 TAB" → units per pack (the last number for "1x15").</summary>
    public static int? ParsePack(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = DigitsRegex().Matches(text);
        if (matches.Count == 0) return null;
        var value = int.Parse(matches[^1].Value, Invariant);
        return value >= 1 ? value : null;
    }

    private static readonly string[] DateFormats =
    {
        "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yy", "dd/MM/yy", "dd.MM.yyyy"
    };

    private static readonly string[] MonthFormats = { "MM/yy", "M/yy", "MM-yy", "M-yy", "MM/yyyy", "M/yyyy", "MM-yyyy", "MMM-yy", "MMM yy", "MMM-yyyy", "MMM yyyy", "MMMyy", "MM.yy", "MMyy" };

    /// <summary>
    /// Expiry as printed on bills: month forms (MM/YY, MMM-YY, MM/YYYY, MMYY) give the last day of that month;
    /// full dates (DD-MM-YYYY, YYYY-MM-DD) give that month's last day as well, matching batch expiry storage.
    /// </summary>
    public static DateTime? ParseExpiry(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateTime.TryParseExact(t, MonthFormats, Invariant, DateTimeStyles.None, out var month) ||
            DateTime.TryParseExact(t, DateFormats, Invariant, DateTimeStyles.None, out month))
        {
            return new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        }
        // Excel serial dates exported as numbers.
        if (double.TryParse(t, NumberStyles.Float, Invariant, out var serial) && serial is > 20000 and < 80000)
        {
            var d = DateTime.FromOADate(serial);
            return new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
        }
        return null;
    }
}
