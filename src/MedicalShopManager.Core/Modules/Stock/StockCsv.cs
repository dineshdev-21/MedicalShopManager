using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>
/// Pure parsing for the opening-stock / item-master CSV import: RFC 4180 records (comma, semicolon or tab),
/// header → field auto-mapping, and per-row parsing + validation into <see cref="StockImportRow"/>.
/// Rupee amounts in the file are converted to paisa once (half away from zero); no other money maths happens here.
/// </summary>
public static class StockCsv
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Header synonyms (lower-case, letters and digits only) for auto-mapping, as exported by common pharmacy software.</summary>
    private static readonly Dictionary<StockImportField, string[]> Synonyms = new()
    {
        [StockImportField.Name] = new[] { "name", "itemname", "productname", "medicinename", "item", "product", "medicine", "drugname" },
        [StockImportField.Manufacturer] = new[] { "manufacturer", "company", "mfr", "mfg", "companyname", "brandcompany", "marketer" },
        [StockImportField.Composition] = new[] { "composition", "salt", "generic", "genericname", "content", "molecule" },
        [StockImportField.Category] = new[] { "category", "group", "itemgroup", "type" },
        [StockImportField.PackLabel] = new[] { "pack", "packing", "packlabel", "packsize", "primarypack" },
        [StockImportField.UnitsPerPack] = new[] { "unitsperpack", "conversion", "unitperpack", "qtyperpack", "packqty" },
        [StockImportField.HsnCode] = new[] { "hsn", "hsncode", "hsnsac" },
        [StockImportField.GstPercent] = new[] { "gst", "gstpercent", "gstrate", "tax", "taxrate", "gstper" },
        [StockImportField.Schedule] = new[] { "schedule", "drugschedule", "sch", "rxtype" },
        [StockImportField.Barcode] = new[] { "barcode", "ean", "upc" },
        [StockImportField.RackLocation] = new[] { "rack", "location", "racklocation", "shelf", "bin" },
        [StockImportField.MinStock] = new[] { "minstock", "min", "reorderlevel", "minqty" },
        [StockImportField.MaxStock] = new[] { "maxstock", "max", "maxqty" },
        [StockImportField.BatchNumber] = new[] { "batch", "batchno", "batchnumber", "batchnum" },
        [StockImportField.Expiry] = new[] { "expiry", "exp", "expirydate", "expdate" },
        [StockImportField.Mrp] = new[] { "mrp", "mrpperpack", "mrprate" },
        [StockImportField.PurchaseRate] = new[] { "purchaserate", "purrate", "cost", "costprice", "purchaseprice", "rate", "prate" },
        [StockImportField.Ptr] = new[] { "ptr", "pricetoretailer" },
        [StockImportField.SaleRate] = new[] { "salerate", "saleprice", "sellingprice", "srate" },
        [StockImportField.QuantityPacks] = new[] { "qty", "quantity", "stock", "packs", "qtypacks", "closingstock", "stockqty" },
        [StockImportField.LooseUnits] = new[] { "loose", "looseunits", "looseqty", "units" }
    };

    /// <summary>Case-insensitive identity of an item in an import (name + manufacturer).</summary>
    public static string ItemKey(string name, string? manufacturer) =>
        $"{name.Trim().ToUpperInvariant()}|{(manufacturer ?? string.Empty).Trim().ToUpperInvariant()}";

    // ───────────────────────── Records ─────────────────────────

    /// <summary>The delimiter used by the header line: tab, semicolon or comma (whichever occurs most outside quotes).</summary>
    public static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n')[0];
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    /// <summary>Parses CSV text into records. Quoted fields may contain delimiters, quotes ("") and line breaks. Blank lines are skipped.</summary>
    public static IReadOnlyList<string[]> Parse(string text, char? delimiter = null)
    {
        var records = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return records;
        if (text[0] == '﻿') text = text[1..];
        var sep = delimiter ?? DetectDelimiter(text);

        var fields = new List<string>();
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
            else if (c == sep) { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* handled with \n */ }
            else if (c == '\n') { EndRecord(); }
            else field.Append(c);
        }
        EndRecord();
        return records;

        void EndRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Any(f => !string.IsNullOrWhiteSpace(f)))
                records.Add(fields.Select(f => f.Trim()).ToArray());
            fields.Clear();
        }
    }

    // ───────────────────────── Mapping ─────────────────────────

    private static string Normalize(string header) => new(header.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>Best field for one header, or Ignore.</summary>
    public static StockImportField GuessField(string header)
    {
        var key = Normalize(header);
        if (key.Length == 0) return StockImportField.Ignore;
        foreach (var (field, names) in Synonyms)
            if (names.Contains(key)) return field;
        return StockImportField.Ignore;
    }

    /// <summary>Suggested mapping for a header row; each field is used at most once (first column wins).</summary>
    public static IReadOnlyList<StockImportColumn> SuggestMapping(IReadOnlyList<string> headers)
    {
        var used = new HashSet<StockImportField>();
        var columns = new List<StockImportColumn>();
        for (var i = 0; i < headers.Count; i++)
        {
            var field = GuessField(headers[i]);
            if (field != StockImportField.Ignore && !used.Add(field)) field = StockImportField.Ignore;
            columns.Add(new StockImportColumn(i, headers[i], field));
        }
        return columns;
    }

    /// <summary>File-level problems with a mapping (missing name column, a field mapped twice, batch columns incomplete).</summary>
    public static IReadOnlyList<string> ValidateMapping(IReadOnlyList<StockImportColumn> columns)
    {
        var errors = new List<string>();
        var mapped = columns.Where(c => c.Field != StockImportField.Ignore).ToList();
        if (!mapped.Any(c => c.Field == StockImportField.Name)) errors.Add("Map a column to Name (item name is required).");
        foreach (var dup in mapped.GroupBy(c => c.Field).Where(g => g.Count() > 1))
            errors.Add($"{dup.Key} is mapped to more than one column ({string.Join(", ", dup.Select(c => c.Header))}).");
        var hasBatch = mapped.Any(c => c.Field == StockImportField.BatchNumber);
        if (hasBatch)
        {
            if (!mapped.Any(c => c.Field == StockImportField.Expiry)) errors.Add("Opening stock needs an Expiry column.");
            if (!mapped.Any(c => c.Field == StockImportField.Mrp)) errors.Add("Opening stock needs an MRP column.");
            if (!mapped.Any(c => c.Field == StockImportField.QuantityPacks)) errors.Add("Opening stock needs a Quantity (packs) column.");
        }
        return errors;
    }

    // ───────────────────────── Values ─────────────────────────

    /// <summary>Rupees ("₹1,234.50", "35.5") to paisa; blank is 0.</summary>
    public static bool TryParseMoney(string? text, out long paisa)
    {
        paisa = 0;
        var cleaned = (text ?? string.Empty).Replace("₹", "").Replace("Rs.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Rs", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Trim();
        if (cleaned.Length == 0) return true;
        if (!decimal.TryParse(cleaned, NumberStyles.Number, Invariant, out var rupees) || rupees < 0) return false;
        paisa = PricingCalculator.RoundPaisa(rupees * 100m);
        return true;
    }

    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        var cleaned = (text ?? string.Empty).Replace("%", "").Trim();
        return cleaned.Length == 0 || decimal.TryParse(cleaned, NumberStyles.Number, Invariant, out value);
    }

    public static bool TryParseWhole(string? text, out long value)
    {
        value = 0;
        var cleaned = (text ?? string.Empty).Replace(",", "").Trim();
        if (cleaned.Length == 0) return true;
        if (long.TryParse(cleaned, NumberStyles.Integer, Invariant, out value)) return value >= 0;
        // "10.0" from spreadsheets
        if (decimal.TryParse(cleaned, NumberStyles.Number, Invariant, out var d) && d >= 0 && d == Math.Floor(d))
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    /// <summary>Expiry as MM/YY, MM/YYYY, MM-YY, MMM-YY, YYYY-MM-DD or DD/MM/YYYY → (month, 4-digit year).</summary>
    public static bool TryParseExpiry(string? text, out int month, out int year)
    {
        month = 0;
        year = 0;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return false;

        string[] full = { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "d/M/yyyy", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(value, full, Invariant, DateTimeStyles.None, out var date))
        {
            month = date.Month;
            year = date.Year;
            return true;
        }

        string[] monthYear = { "MM/yy", "M/yy", "MM/yyyy", "M/yyyy", "MM-yy", "M-yy", "MM-yyyy", "MMM-yy", "MMM-yyyy", "MMM yy", "MMM yyyy", "MM.yy", "MMyy" };
        if (DateTime.TryParseExact(value, monthYear, Invariant, DateTimeStyles.None, out date))
        {
            month = date.Month;
            year = date.Year;
            return year is >= 2000 and <= 2099;
        }
        return false;
    }

    public static bool TryParseSchedule(string? text, out DrugSchedule schedule)
    {
        schedule = DrugSchedule.None;
        var key = Normalize(text ?? string.Empty);
        switch (key)
        {
            case "": case "otc": case "none": case "na": case "nonrx": case "general": return true;
            case "h": case "schh": case "scheduleh": schedule = DrugSchedule.H; return true;
            case "h1": case "schh1": case "scheduleh1": schedule = DrugSchedule.H1; return true;
            case "x": case "schx": case "schedulex": schedule = DrugSchedule.X; return true;
            case "nrx": case "narcotic": case "ndps": schedule = DrugSchedule.NRx; return true;
            case "rx": case "g": case "schg": case "scheduleg": case "prescription": schedule = DrugSchedule.Rx; return true;
            default: return (text ?? string.Empty).Trim() == "-";
        }
    }

    /// <summary>Units per pack from a pack label: "15's" → 15, "10 x 10" → 10, "strip of 10" → 10; volumes and weights ("100ml", "30gm") → 1.</summary>
    public static int UnitsFromPackLabel(string? packLabel)
    {
        if (string.IsNullOrWhiteSpace(packLabel)) return 1;
        var label = packLabel.Trim().ToLowerInvariant();
        if (Regex.IsMatch(label, @"\d\s*(ml|l|ltr|gm|g|kg|mg)\b")) return 1;
        var numbers = Regex.Matches(label, @"\d+");
        return numbers.Count > 0 && int.TryParse(numbers[^1].Value, NumberStyles.Integer, Invariant, out var n) && n >= 1 ? n : 1;
    }

    // ───────────────────────── Rows ─────────────────────────

    /// <summary>Parses and validates the data records (header excluded) with the given mapping.</summary>
    public static IReadOnlyList<StockImportRow> BuildRows(IReadOnlyList<string[]> dataRecords, IReadOnlyList<StockImportColumn> columns, decimal defaultGstPercent = 12m)
    {
        var rows = new List<StockImportRow>();
        for (var r = 0; r < dataRecords.Count; r++)
            rows.Add(BuildRow(dataRecords[r], columns, r + 2, defaultGstPercent));
        return rows;
    }

    public static StockImportRow BuildRow(string[] record, IReadOnlyList<StockImportColumn> columns, int lineNumber, decimal defaultGstPercent = 12m)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string? Get(StockImportField field)
        {
            var column = columns.FirstOrDefault(c => c.Field == field);
            if (column == null || column.Index >= record.Length) return null;
            var value = record[column.Index].Trim();
            return value.Length == 0 ? null : value;
        }
        bool Has(StockImportField field) => columns.Any(c => c.Field == field);

        long Money(StockImportField field, string label)
        {
            if (TryParseMoney(Get(field), out var paisa)) return paisa;
            errors.Add($"{label} '{Get(field)}' is not a valid amount.");
            return 0;
        }
        long Whole(StockImportField field, string label)
        {
            if (TryParseWhole(Get(field), out var n)) return n;
            errors.Add($"{label} '{Get(field)}' must be a whole number.");
            return 0;
        }

        var name = Get(StockImportField.Name) ?? string.Empty;
        if (name.Length == 0) errors.Add("Item name is empty.");
        if (name.Length > 150) errors.Add("Item name is longer than 150 characters.");

        var packLabel = Get(StockImportField.PackLabel);
        var packSizeGiven = Get(StockImportField.UnitsPerPack) != null || packLabel != null;
        var upp = 1;
        if (Get(StockImportField.UnitsPerPack) is { } uppText)
        {
            if (!int.TryParse(uppText, NumberStyles.Integer, Invariant, out upp) || upp < 1)
            {
                errors.Add($"Units per pack '{uppText}' must be a whole number of at least 1.");
                upp = 1;
            }
        }
        else upp = UnitsFromPackLabel(packLabel);

        decimal gst = defaultGstPercent;
        if (Get(StockImportField.GstPercent) is { } gstText)
        {
            if (!TryParseDecimal(gstText, out gst) || gst is < 0 or > StockRules.MaxGstPercent)
            {
                errors.Add($"GST '{gstText}' must be between 0 and {StockRules.MaxGstPercent:0}.");
                gst = defaultGstPercent;
            }
        }
        else warnings.Add($"GST not given; {defaultGstPercent:0.##}% assumed.");

        var hsn = Get(StockImportField.HsnCode);
        if (StockRules.ValidateHsn(hsn) is { } hsnError) errors.Add(hsnError);

        if (!TryParseSchedule(Get(StockImportField.Schedule), out var schedule))
            errors.Add($"Schedule '{Get(StockImportField.Schedule)}' is not one of OTC, H, H1, X, NRx, Rx.");

        var minStock = Whole(StockImportField.MinStock, "Min stock");
        var maxStock = Whole(StockImportField.MaxStock, "Max stock");
        if (maxStock > 0 && maxStock < minStock) errors.Add("Max stock must be at least the min stock.");

        var batch = Has(StockImportField.BatchNumber) ? StockRules.NormalizeBatchNumber(Get(StockImportField.BatchNumber)) : string.Empty;
        int? expMonth = null, expYear = null;
        long mrp = 0, purchase = 0, ptr = 0, sale = 0, packs = 0, loose = 0;
        mrp = Money(StockImportField.Mrp, "MRP");
        purchase = Money(StockImportField.PurchaseRate, "Purchase rate");
        ptr = Money(StockImportField.Ptr, "PTR");
        sale = Money(StockImportField.SaleRate, "Sale rate");
        if (batch.Length > 0)
        {
            if (batch.Length > StockRules.MaxBatchNumberLength)
                errors.Add($"Batch number is longer than {StockRules.MaxBatchNumberLength} characters.");
            if (TryParseExpiry(Get(StockImportField.Expiry), out var m, out var y)) { expMonth = m; expYear = y; }
            else errors.Add($"Expiry '{Get(StockImportField.Expiry)}' is not a valid date (use MM/YY).");
            packs = Whole(StockImportField.QuantityPacks, "Quantity");
            loose = Whole(StockImportField.LooseUnits, "Loose units");
            if (packs == 0 && loose == 0) errors.Add("Opening quantity is zero.");
            // Without a pack size in the row, loose units are checked once the pack size is known (ApplyPackSize).
            if (packSizeGiven && LooseError(loose, upp) is { } looseError) errors.Add(looseError);
            if (mrp <= 0) errors.Add("MRP is required for opening stock.");
            if (sale > mrp && mrp > 0) errors.Add("Sale rate is more than the MRP.");
            if (purchase > mrp && mrp > 0) errors.Add("Purchase rate is more than the MRP.");
            if (ptr > mrp && mrp > 0) errors.Add("PTR is more than the MRP.");
            if (expMonth is { } em && expYear is { } ey && new DateTime(ey, em, DateTime.DaysInMonth(ey, em)) < new DateTime(2000, 1, 1))
                errors.Add("Expiry is too old.");
        }
        else if (Get(StockImportField.QuantityPacks) != null || Get(StockImportField.Expiry) != null)
        {
            warnings.Add("No batch number: only the item master is imported, quantity ignored.");
        }

        var row = new StockImportRow
        {
            LineNumber = lineNumber,
            Name = name,
            Manufacturer = Get(StockImportField.Manufacturer),
            Composition = Get(StockImportField.Composition),
            Category = Get(StockImportField.Category),
            PackLabel = packLabel,
            UnitsPerPack = upp,
            PackSizeGiven = packSizeGiven,
            HsnCode = hsn,
            GstPercent = gst,
            Schedule = schedule,
            Barcode = Get(StockImportField.Barcode),
            RackLocation = Get(StockImportField.RackLocation),
            MinStock = (int)Math.Min(minStock, int.MaxValue),
            MaxStock = (int)Math.Min(maxStock, int.MaxValue),
            BatchNumber = batch.Length > 0 ? batch : null,
            ExpiryMonth = expMonth,
            ExpiryYear = expYear,
            MrpPaisa = mrp,
            PurchaseRatePaisa = purchase,
            PtrPaisa = ptr,
            SaleRatePaisa = sale,
            Packs = packs,
            LooseUnits = loose
        };
        row.Errors.AddRange(errors);
        row.Warnings.AddRange(warnings);
        return row;
    }

    private static string? LooseError(long loose, int unitsPerPack) =>
        unitsPerPack > 1 && loose >= unitsPerPack ? $"Loose units must be less than the pack size ({unitsPerPack})."
        : unitsPerPack == 1 && loose > 0 ? "Pack size is 1: give the quantity in packs."
        : null;

    /// <summary>
    /// For a row that gives no pack size: takes <paramref name="unitsPerPack"/> (the existing item's, else 1) and checks its
    /// loose units against it. Rows with their own pack size are left unchanged.
    /// </summary>
    public static void ApplyPackSize(StockImportRow row, int unitsPerPack)
    {
        if (row.PackSizeGiven) return;
        row.UnitsPerPack = Math.Max(unitsPerPack, 1);
        if (row.HasBatch && LooseError(row.LooseUnits, row.UnitsPerPack) is { } error) row.Errors.Add(error);
    }

    /// <summary>Marks repeated (item, batch) pairs within the file: the second and later occurrences are errors.</summary>
    public static void FlagDuplicateBatches(IEnumerable<StockImportRow> rows)
    {
        var seen = new HashSet<string>();
        foreach (var row in rows.Where(r => r.HasBatch))
        {
            if (!seen.Add(ItemKey(row.Name, row.Manufacturer) + "|" + row.BatchNumber))
                row.Errors.Add($"Batch {row.BatchNumber} of {row.Name} appears more than once in the file.");
        }
    }
}
