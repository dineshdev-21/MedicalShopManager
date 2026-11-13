using System.Globalization;
using ClosedXML.Excel;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Supplier invoice import (spec 04 "Import CSV"): reads CSV or Excel, maps columns (saved per distributor), matches
/// rows to items by barcode then exact name, and builds a draft purchase the user reviews before saving.
/// </summary>
public sealed class PurchaseImportService : IPurchaseImportService
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly IPurchaseBillRepository _purchases;
    private readonly IDistributorRepository _distributors;
    private readonly IMedicineRepository _medicines;
    private readonly IPurchaseEntryService _entry;

    public PurchaseImportService(IPurchaseBillRepository purchases, IDistributorRepository distributors, IMedicineRepository medicines,
        IPurchaseEntryService entry)
    {
        _purchases = purchases;
        _distributors = distributors;
        _medicines = medicines;
        _entry = entry;
    }

    public async Task<PurchaseImportTable> ReadFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new PurchaseRuleException("The selected file was not found.");
        var info = new FileInfo(path);
        if (info.Length == 0) throw new PurchaseRuleException("The selected file is empty.");
        if (info.Length > MaxFileBytes) throw new PurchaseRuleException("The file is larger than 10 MB. Export only the invoice rows.");

        var extension = info.Extension.ToLowerInvariant();
        PurchaseImportTable table;
        try
        {
            table = extension switch
            {
                ".csv" or ".txt" or ".tsv" => PurchaseImportParser.ParseDelimited(await File.ReadAllTextAsync(path)),
                ".xlsx" or ".xlsm" => ReadWorkbook(path),
                _ => throw new PurchaseRuleException("Choose a .csv or .xlsx file exported from the distributor's bill.")
            };
        }
        catch (IOException ex)
        {
            throw new PurchaseRuleException($"The file could not be read ({ex.Message}). Close it in Excel and try again.");
        }

        if (table.Headers.Count == 0 || table.Rows.Count == 0)
            throw new PurchaseRuleException("No rows were found. The first row must hold the column names.");
        return table;
    }

    /// <summary>First worksheet: first non-empty row = headers. Dates become dd-MM-yyyy and numbers invariant text.</summary>
    internal static PurchaseImportTable ReadWorkbook(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used == null) return new PurchaseImportTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var rows = used.RowsUsed().Select(r => (IReadOnlyList<string>)r.Cells(1, used.ColumnCount()).Select(CellText).ToList()).ToList();
        var headers = rows[0].Select(h => h.Trim()).ToList();
        return new PurchaseImportTable(headers, rows.Skip(1).ToList());
    }

    private static string CellText(IXLCell cell) => cell.DataType switch
    {
        XLDataType.DateTime => cell.GetDateTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        XLDataType.Number => cell.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => cell.GetFormattedString()
    };

    public async Task<IReadOnlyList<PurchaseImportField>> GetMappingAsync(long supplierId, PurchaseImportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (supplierId > 0 && await _purchases.GetImportMappingAsync(supplierId, table.Headers) is { } saved) return saved;
        return PurchaseImportParser.SuggestMapping(table.Headers);
    }

    public IReadOnlyList<PurchaseImportRow> Preview(PurchaseImportTable table, IReadOnlyList<PurchaseImportField> mapping) =>
        PurchaseImportParser.MapRows(table, mapping, DateTime.Today);

    public async Task<PurchaseImportResult> BuildDraftAsync(long supplierId, string supplierInvoiceNumber, DateTime invoiceDate,
        PurchaseImportTable table, IReadOnlyList<PurchaseImportField> mapping, string source)
    {
        var distributor = await _distributors.GetByIdAsync(supplierId) ?? throw new PurchaseRuleException("Please add Distributor");
        var settings = await _entry.GetSettingsAsync();
        var rows = Preview(table, mapping);
        var skipped = new List<PurchaseImportRow>();

        var bill = new PurchaseBill
        {
            SupplierId = distributor.Id,
            SupplierName = distributor.Name,
            SupplierGstin = distributor.Gstin,
            SupplierInvoiceNumber = supplierInvoiceNumber?.Trim() ?? string.Empty,
            InvoiceDate = invoiceDate.Date,
            DueDate = PurchaseCalculator.DueDate(invoiceDate, distributor.CreditDays),
            IsInterState = await _entry.IsInterStateAsync(distributor),
            ImportSource = source
        };

        var imported = 0;
        foreach (var row in rows)
        {
            if (!row.IsValid) { skipped.Add(row); continue; }
            var medicine = await MatchAsync(row);
            if (medicine == null)
            {
                row.Errors.Add($"No item named '{row.ProductName}' in Items. Add it (Alt+I) or fix the name, then import again.");
                skipped.Add(row);
                continue;
            }

            var line = await _entry.CreateLineAsync(medicine);
            var pack = row.UnitsPerPack ?? line.UnitsPerPack;
            line.UnitsPerPack = pack;
            line.BatchNumber = row.BatchNumber.Trim();
            line.ExpiryDate = row.ExpiryDate!.Value;
            line.HsnCode = row.HsnCode ?? line.HsnCode;
            line.Quantity = (int)row.QuantityPacks * pack;
            line.FreeQuantity = PurchaseCalculator.FreeUnitsFromPacks(row.FreePacks, pack);
            line.MrpPaisa = row.MrpPaisa;
            line.EnteredRatePaisa = row.RatePaisa;
            line.PtrPaisa = row.PtrPaisa;
            line.SaleRatePaisa = row.SaleRatePaisa > 0 ? row.SaleRatePaisa : PurchaseCalculator.DefaultSaleRate(row.MrpPaisa, settings.DefaultSaleDiscountPercent);
            line.DiscountPercent = row.DiscountPercent;
            line.SchemePercent = row.SchemePercent;
            line.GstRatePercent = row.GstPercent ?? line.GstRatePercent;

            var same = bill.Lines.FirstOrDefault(l => l.MedicineId == line.MedicineId && l.UnitsPerPack == line.UnitsPerPack
                && string.Equals(l.BatchNumber, line.BatchNumber, StringComparison.OrdinalIgnoreCase)
                && l.ExpiryDate.ToString("yyyy-MM") == line.ExpiryDate.ToString("yyyy-MM") && l.EnteredRatePaisa == line.EnteredRatePaisa);
            if (same != null)
            {
                same.Quantity += line.Quantity;
                same.FreeQuantity += line.FreeQuantity;
            }
            else bill.Lines.Add(line);
            imported++;
        }

        await _entry.RecalculateAsync(bill);
        await _purchases.SaveImportMappingAsync(distributor.Id, table.Headers, mapping);
        return new PurchaseImportResult(bill, skipped, imported);
    }

    /// <summary>Barcode first, then an exact (case- and space-insensitive) name match, else a single search hit.</summary>
    private async Task<Medicine?> MatchAsync(PurchaseImportRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Barcode) && await _medicines.GetByBarcodeAsync(row.Barcode.Trim()) is { } byBarcode)
            return byBarcode;

        static string Key(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        var candidates = await _medicines.SearchAsync(row.ProductName.Trim());
        var exact = candidates.Where(m => Key(m.Name) == Key(row.ProductName)).ToList();
        if (exact.Count >= 1) return exact[0];
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
