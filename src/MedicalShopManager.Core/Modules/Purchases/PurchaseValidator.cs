using System.Text.RegularExpressions;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// Save validations for purchases, debit notes and distributors. Messages follow spec 04 "Save validations"
/// (e.g. "Please fill Batch MRP on Row: 2") so users see the wording they know. Pure; callers pass "today".
/// </summary>
public static partial class PurchaseValidator
{
    public static readonly IReadOnlyList<decimal> StandardGstRates = new[] { 0m, 0.25m, 3m, 5m, 12m, 18m, 28m };

    [GeneratedRegex(@"^\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex GstinRegex();

    [GeneratedRegex(@"^(\d{4}|\d{6}|\d{8})$")]
    private static partial Regex HsnRegex();

    [GeneratedRegex(@"^[1-9]\d{5}$")]
    private static partial Regex PincodeRegex();

    public static bool IsValidGstin(string? gstin) => !string.IsNullOrWhiteSpace(gstin) && GstinRegex().IsMatch(gstin.Trim().ToUpperInvariant());

    public static bool IsValidHsn(string? hsn) => !string.IsNullOrWhiteSpace(hsn) && HsnRegex().IsMatch(hsn.Trim());

    /// <summary>Indian phone: 10 digits after removing spaces, dashes, a leading +91 / 91 or 0.</summary>
    public static bool IsValidPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        return digits.Length == 10;
    }

    /// <summary>
    /// All problems with a purchase, in the order the spec checks them. Empty = valid. Batches must not be expired on
    /// <paramref name="expiryCutoff"/> (default <paramref name="today"/>; edits of old bills pass the invoice date).
    /// </summary>
    public static IReadOnlyList<string> ValidatePurchase(PurchaseBill bill, DateTime today, long initialPaymentPaisa = 0, DateTime? expiryCutoff = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var errors = new List<string>();

        if (bill.OtherChargesPaisa < 0) errors.Add("Delivery / other charges cannot be negative.");
        if (bill.OtherChargesPaisa > 0 && string.IsNullOrWhiteSpace(bill.OtherChargesLabel))
            errors.Add("Please add custom charge type");
        if (bill.Lines.Count == 0) errors.Add("Please add Products");
        if (string.IsNullOrWhiteSpace(bill.SupplierInvoiceNumber)) errors.Add("Please add Invoice No");
        else if (bill.SupplierInvoiceNumber.Trim().Length > 30) errors.Add("Invoice No cannot be longer than 30 characters.");
        if (bill.SupplierId <= 0) errors.Add("Please add Distributor");
        if (bill.InvoiceDate == default) errors.Add("Please add Invoice Date/Check Date Format (DD-MM-YYYY)");
        else if (bill.InvoiceDate.Date > today.Date) errors.Add("Invoice Date cannot be in the future.");
        if (bill.DueDate.HasValue && bill.InvoiceDate != default && bill.DueDate.Value.Date < bill.InvoiceDate.Date)
            errors.Add("Payment Due Date cannot be before the Invoice Date.");
        if (bill.BillDiscountPercent is < 0 or > 100) errors.Add("Bill discount must be between 0 and 100%.");
        if (initialPaymentPaisa < 0) errors.Add("Payment Amount cannot be negative");

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bill.Lines.Count; i++)
        {
            var row = i + 1;
            var line = bill.Lines[i];
            ValidateLine(line, row, (expiryCutoff ?? today).Date, errors);

            if (line.MedicineId > 0 && !string.IsNullOrWhiteSpace(line.BatchNumber))
            {
                var key = $"{line.MedicineId}|{line.BatchNumber.Trim()}|{line.ExpiryDate:yyyy-MM}";
                if (seen.TryGetValue(key, out var first))
                    errors.Add($"Row: {row} repeats the product, batch and expiry of Row: {first}. Add the quantities on one row.");
                else seen[key] = row;
            }
        }

        return errors;
    }

    private static void ValidateLine(PurchaseBillLine line, int row, DateTime expiryCutoff, List<string> errors)
    {
        if (line.MedicineId <= 0 || string.IsNullOrWhiteSpace(line.MedicineName))
            errors.Add($"Please fill Product Name on Row: {row}");
        if (string.IsNullOrWhiteSpace(line.HsnCode)) errors.Add($"Please fill HSN Code on Row: {row}");
        else if (!IsValidHsn(line.HsnCode)) errors.Add($"Please fill a valid HSN Code on Row: {row}");
        if (string.IsNullOrWhiteSpace(line.BatchNumber)) errors.Add($"Please fill Batch No on Row: {row}");
        else if (line.BatchNumber.Trim().Length > 30) errors.Add($"Batch No is too long on Row: {row}");
        if (line.UnitsPerPack < 1) errors.Add($"Please fill Units Per Pack on Row: {row}");
        if (line.Quantity < 0 || line.FreeQuantity < 0) errors.Add($"Quantity cannot be negative on Row: {row}");
        else if (line.Quantity + line.FreeQuantity == 0) errors.Add($"Please fill Quantity on Row: {row}");
        if (line.MrpPaisa <= 0) errors.Add($"Please fill Batch MRP on Row: {row}");
        if (line.EnteredRatePaisa < 0 || (line.EnteredRatePaisa == 0 && line.Quantity > 0)) errors.Add($"Please fill PTR on Row: {row}");
        if (line.SaleRatePaisa <= 0) errors.Add($"Please fill Batch Sale Rate on Row: {row}");
        else if (line.MrpPaisa > 0 && line.SaleRatePaisa > line.MrpPaisa) errors.Add($"Sale Rate cannot be more than MRP on Row: {row}");
        if (line.MrpPaisa > 0 && line.PurchaseRatePaisa > line.MrpPaisa) errors.Add($"Purchase Rate is more than MRP on Row: {row}");
        if (line.GstRatePercent is < 0 or > 28) errors.Add($"Please fill Batch GST Rate on Row: {row}");
        if (line.DiscountPercent is < 0 or > 100) errors.Add($"Discount must be between 0 and 100% on Row: {row}");
        if (line.SchemePercent is < 0 or > 100) errors.Add($"Scheme % must be between 0 and 100 on Row: {row}");
        if (line.ExpiryDate == default) errors.Add($"Please fill Expiry Date on Row: {row}");
        else if (line.ExpiryDate.Date < expiryCutoff) errors.Add($"Batch is Expired on Row: {row}");
    }

    /// <summary>Distributor master checks (name, GSTIN format and state, phone, pincode, credit days).</summary>
    public static IReadOnlyList<string> ValidateDistributor(Distributor d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Name)) errors.Add("Please add Distributor Name");
        else if (d.Name.Trim().Length > 120) errors.Add("Distributor Name cannot be longer than 120 characters.");
        if (!string.IsNullOrWhiteSpace(d.Gstin))
        {
            if (!IsValidGstin(d.Gstin)) errors.Add("Please enter a valid 15-character GSTIN (e.g. 29ABCDE1234F1Z5).");
            else if (!string.IsNullOrWhiteSpace(d.StateCode) && !d.Gstin.Trim().StartsWith(d.StateCode.Trim(), StringComparison.Ordinal))
                errors.Add("State does not match the first two digits of the GSTIN.");
        }
        if (!string.IsNullOrWhiteSpace(d.StateCode) && !(d.StateCode.Trim().Length == 2 && d.StateCode.Trim().All(char.IsDigit)))
            errors.Add("State code must be the 2-digit GST state code.");
        if (!string.IsNullOrWhiteSpace(d.Phone) && !IsValidPhone(d.Phone)) errors.Add("Please enter a valid 10-digit phone number.");
        if (!string.IsNullOrWhiteSpace(d.Pincode) && !PincodeRegex().IsMatch(d.Pincode.Trim())) errors.Add("Please enter a valid 6-digit pincode.");
        if (!string.IsNullOrWhiteSpace(d.Email) && !d.Email.Contains('@')) errors.Add("Please enter a valid email address.");
        if (d.CreditDays is < 0 or > 365) errors.Add("Credit period must be between 0 and 365 days.");
        return errors;
    }

    /// <summary>Debit note checks. <paramref name="maxUnitsByBatch"/> caps each line (stock on hand / purchase line).</summary>
    public static IReadOnlyList<string> ValidateReturn(PurchaseReturnDocument doc, DateTime today, IReadOnlyDictionary<long, int>? maxUnitsByBatch = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var errors = new List<string>();
        if (doc.SupplierId <= 0) errors.Add("Please add Distributor");
        if (doc.ReturnDate == default) errors.Add("Please add Return Date");
        else if (doc.ReturnDate.Date > today.Date) errors.Add("Return Date cannot be in the future.");
        if (doc.Lines.Count == 0) errors.Add("Please add Products to return");
        if (doc.RefundReceivedPaisa < 0) errors.Add("Refund amount cannot be negative.");

        var perBatch = new Dictionary<long, int>();
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var row = i + 1;
            var line = doc.Lines[i];
            if (line.BatchId <= 0 || line.MedicineId <= 0) errors.Add($"Please select a batch on Row: {row}");
            if (line.Quantity <= 0) errors.Add($"Please fill Return Quantity on Row: {row}");
            if (line.RatePaisa < 0) errors.Add($"Rate cannot be negative on Row: {row}");
            if (line.UnitsPerPack < 1) errors.Add($"Please fill Units Per Pack on Row: {row}");
            if (line.GstRatePercent is < 0 or > 28) errors.Add($"Please fill GST Rate on Row: {row}");
            perBatch[line.BatchId] = perBatch.GetValueOrDefault(line.BatchId) + Math.Max(0, line.Quantity);
        }

        if (maxUnitsByBatch != null)
        {
            foreach (var (batchId, units) in perBatch)
            {
                if (maxUnitsByBatch.TryGetValue(batchId, out var max) && units > max)
                {
                    var name = doc.Lines.First(l => l.BatchId == batchId);
                    errors.Add($"Only {max} units of {name.MedicineName} (Batch {name.BatchNumber}) can be returned.");
                }
            }
        }

        return errors;
    }
}
