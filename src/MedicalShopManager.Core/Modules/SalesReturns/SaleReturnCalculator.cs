using System.Globalization;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>
/// Pure rules of a sale return (credit note). All money goes through <see cref="PricingCalculator"/>.
/// <list type="bullet">
/// <item><b>Against a bill</b>, a returned line is credited at the price the customer actually paid for those units:
/// the line total (after line and bill discounts, GST included) pro rata to units sold, free units included
/// (<c>AmountForUnits(lineTotal, units, soldUnits)</c>: the bill line priced as one "pack" of everything sold). GST is
/// backed out as on the bill (inclusive). The return that empties a line takes exactly what is left of the line's
/// amount and taxable value, so several partial returns never credit more, or more GST, than the bill charged.</item>
/// <item><b>Without a bill</b>, a line is priced like a sale line at the entered rate per pack (≤ MRP) and discount,
/// intra-state, with the batch's GST.</item>
/// <item>The credit note is rounded like a bill (<see cref="PricingCalculator.SummarizeBill"/>) but never exceeds what
/// is left of the bill (grand total − custom charge − earlier returns).</item>
/// <item>Disposition: Damaged is kept; otherwise an expired batch (expiry ≤ return date) becomes Expired stock, else Restock.</item>
/// <item>Refund now is capped at what was paid on the bill less earlier refunds; credit needs a saved customer.</item>
/// </list>
/// </summary>
public static class SaleReturnCalculator
{
    public const int MaxNotesLength = 300;
    public const int MaxReasonLength = 120;
    public const int MaxReferenceLength = 60;

    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>"1,234.50" for messages.</summary>
    public static string Rupees(long paisa) => (paisa / 100m).ToString("N2", India);

    /// <summary>A batch is expired from its expiry date onwards (same rule as the stock and billing screens).</summary>
    public static bool IsExpired(DateTime expiry, DateTime onDate) => expiry != default && expiry.Date <= onDate.Date;

    /// <summary>Damaged stays damaged; anything else goes back to the batch, as expired stock when the batch has expired.</summary>
    public static SaleReturnDisposition ResolveDisposition(SaleReturnDisposition requested, DateTime expiry, DateTime returnDate) =>
        requested == SaleReturnDisposition.Damaged ? SaleReturnDisposition.Damaged
        : IsExpired(expiry, returnDate) ? SaleReturnDisposition.ExpiredStock
        : SaleReturnDisposition.Restock;

    /// <summary>Amounts credited for <paramref name="units"/> of a bill line (see class remarks).</summary>
    public static SaleLineAmounts AgainstBillLine(ReturnableSaleLine line, long units, bool isInterState)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (units <= 0 || units > line.ReturnableUnits)
            throw new ArgumentOutOfRangeException(nameof(units), $"Units must be between 1 and {line.ReturnableUnits}.");

        var unitsPerPack = Math.Max(line.UnitsPerPack, 1);
        long total, taxable;
        if (units == line.ReturnableUnits)
        {
            total = Math.Max(0, line.LineTotalPaisa - line.ReturnedTotalPaisa);
            taxable = Math.Clamp(line.TaxablePaisa - line.ReturnedTaxablePaisa, 0, total);
        }
        else
        {
            total = PricingCalculator.AmountForUnits(line.LineTotalPaisa, units, checked((int)line.SoldUnits));
            taxable = PricingCalculator.ExtractTaxable(total, line.GstPercent);
        }

        var tax = total - taxable;
        var split = PricingCalculator.SplitTax(tax, isInterState);
        var gross = PricingCalculator.AmountForUnits(line.RatePerPackPaisa, units, unitsPerPack);
        var mrpValue = line.MrpPerPackPaisa > 0 ? PricingCalculator.AmountForUnits(line.MrpPerPackPaisa, units, unitsPerPack) : 0;
        return new SaleLineAmounts
        {
            GrossPaisa = gross,
            DiscountedRatePerPackPaisa = PricingCalculator.RateAfterDiscount(line.RatePerPackPaisa, Math.Clamp(line.DiscountPercent, 0m, 100m)),
            LineDiscountPaisa = Math.Max(0, gross - total),
            TaxablePaisa = taxable,
            TaxPaisa = tax,
            CgstPaisa = split.CgstPaisa,
            SgstPaisa = split.SgstPaisa,
            IgstPaisa = split.IgstPaisa,
            TotalPaisa = total,
            MrpValuePaisa = mrpValue,
            SavingsPaisa = mrpValue > 0 ? mrpValue - total : 0
        };
    }

    /// <summary>Amounts of a line returned without a bill, priced like a sale line (GST-inclusive, intra-state).</summary>
    public static SaleLineAmounts WalkInLine(SaleReturnBatchInfo batch, long units, long ratePerPackPaisa, decimal discountPercent)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = ratePerPackPaisa,
            Units = units,
            UnitsPerPack = Math.Max(batch.UnitsPerPack, 1),
            DiscountPercent = discountPercent,
            GstPercent = batch.GstPercent,
            IsInterState = false,
            MrpPerPackPaisa = batch.MrpPaisa
        });
    }

    /// <summary>Most that can be paid back now on a return against <paramref name="sale"/>; unlimited without a bill.</summary>
    public static long MaxRefundNow(ReturnableSale? sale) =>
        sale is null ? long.MaxValue : Math.Max(0, sale.AmountPaidPaisa - sale.PreviouslyRefundedPaisa);

    /// <summary>Most a new credit note on <paramref name="sale"/> may be worth: what is left of the bill.</summary>
    public static long MaxCreditValue(ReturnableSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return Math.Max(0, sale.GrandTotalPaisa - Math.Max(0, sale.CustomChargePaisa) - sale.PreviouslyReturnedPaisa);
    }

    /// <summary>
    /// Validates the request and computes the credit note. The document is complete (lines, GST split, round-off,
    /// net) even when there are errors, so the screen can show running totals.
    /// </summary>
    /// <param name="sale">The original bill (required when <see cref="SaleReturnRequest.SaleId"/> is set).</param>
    /// <param name="batches">Batch details for lines returned without a bill, by batch id.</param>
    public static (SaleReturnDocument Document, IReadOnlyList<string> Errors) Build(SaleReturnRequest request, ReturnableSale? sale,
        IReadOnlyDictionary<long, SaleReturnBatchInfo> batches, RoundOffMode roundOff, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(batches);
        var errors = new List<string>();
        var doc = new SaleReturnDocument
        {
            ReturnDate = request.ReturnDate.Date,
            RefundMode = request.RefundMode,
            RefundMethod = request.RefundMethod,
            RefundReference = Blank(request.RefundReference),
            Reason = Blank(request.Reason),
            Notes = Blank(request.Notes),
            CreatedByUserId = request.UserId
        };

        if (request.SaleId is not null)
        {
            if (sale is null || sale.SaleId != request.SaleId)
                return (doc, new[] { "The original invoice was not found. Search for it again." });
            doc.SaleId = sale.SaleId;
            doc.SaleInvoiceNumber = sale.InvoiceNumber;
            doc.SaleInvoiceDate = sale.InvoiceDate;
            doc.CustomerId = sale.HasCustomer ? sale.CustomerId : null;
            doc.CustomerName = sale.CustomerName;
            doc.CustomerPhone = sale.CustomerPhone;
            doc.CustomerGstin = sale.CustomerGstin;
            doc.IsInterState = sale.IsInterState;
            if (sale.IsCancelled) errors.Add($"Invoice {sale.InvoiceNumber} was cancelled, so nothing can be returned against it.");
            if (request.ReturnDate.Date < sale.InvoiceDate.Date)
                errors.Add($"The return date cannot be before the invoice date ({sale.InvoiceDate:dd-MM-yyyy}).");
        }
        else
        {
            sale = null;
            doc.CustomerId = request.CustomerId is > 0 ? request.CustomerId : null;
            doc.CustomerName = Blank(request.CustomerName);
            doc.CustomerPhone = Blank(request.CustomerPhone);
        }

        if (request.ReturnDate.Date > today.Date) errors.Add("The return date cannot be in the future.");
        if (request.ReturnDate == default) errors.Add("Enter the return date.");

        var amounts = new List<SaleLineAmounts>();
        var entered = request.Lines.Where(l => l.Units != 0).ToList();
        foreach (var bad in entered.Where(l => l.Units < 0))
            errors.Add($"Return quantities cannot be negative (line for batch id {bad.BatchId}).");
        entered = entered.Where(l => l.Units > 0).ToList();
        if (entered.Count == 0) errors.Add("Enter a return quantity for at least one item.");

        if (sale is not null) AddBillLines(doc, sale, entered, amounts, errors);
        else AddWalkInLines(doc, entered, batches, amounts, errors);

        Totals(doc, amounts, roundOff, sale);
        ValidateSettlement(doc, request, sale, errors);
        return (doc, errors);
    }

    private static void AddBillLines(SaleReturnDocument doc, ReturnableSale sale, List<SaleReturnLineRequest> entered,
        List<SaleLineAmounts> amounts, List<string> errors)
    {
        var seen = new HashSet<long>();
        foreach (var request in entered)
        {
            var line = request.SaleItemId is { } id ? sale.Lines.FirstOrDefault(l => l.SaleItemId == id) : null;
            if (line is null)
            {
                errors.Add($"A returned item is not on invoice {sale.InvoiceNumber}.");
                continue;
            }
            if (!seen.Add(line.SaleItemId))
            {
                errors.Add($"{line.MedicineName} (Batch {line.BatchNumber}) is listed twice; enter the total quantity once.");
                continue;
            }
            if (request.Units > line.ReturnableUnits)
            {
                errors.Add(line.ReturnableUnits == 0
                    ? $"{line.MedicineName} (Batch {line.BatchNumber}) has already been returned in full."
                    : $"{line.MedicineName} (Batch {line.BatchNumber}): only {line.ReturnableDisplay} can be returned " +
                      $"(sold {line.SoldDisplay}, already returned {line.ReturnedDisplay}).");
                continue;
            }

            var a = AgainstBillLine(line, request.Units, sale.IsInterState);
            amounts.Add(a);
            doc.Lines.Add(ToLine(a, request.Units, ResolveDisposition(request.Disposition, line.ExpiryDate, doc.ReturnDate),
                line.SaleItemId, line.MedicineId, line.MedicineName, line.HsnCode, line.BatchId, line.BatchNumber, line.ExpiryDate,
                line.UnitsPerPack, line.RatePerPackPaisa, line.MrpPerPackPaisa, line.DiscountPercent, line.GstPercent));
        }
    }

    private static void AddWalkInLines(SaleReturnDocument doc, List<SaleReturnLineRequest> entered,
        IReadOnlyDictionary<long, SaleReturnBatchInfo> batches, List<SaleLineAmounts> amounts, List<string> errors)
    {
        var seen = new HashSet<long>();
        foreach (var request in entered)
        {
            if (!batches.TryGetValue(request.BatchId, out var batch) || batch.MedicineId != request.MedicineId)
            {
                errors.Add("A returned batch was not found. Pick the item and batch again.");
                continue;
            }
            var name = $"{batch.MedicineName} (Batch {batch.BatchNumber})";
            if (!seen.Add(batch.BatchId))
            {
                errors.Add($"{name} is listed twice; enter the total quantity once.");
                continue;
            }
            if (request.RatePerPackPaisa <= 0)
            {
                errors.Add($"{name}: enter the refund rate per pack.");
                continue;
            }
            if (batch.MrpPaisa > 0 && request.RatePerPackPaisa > batch.MrpPaisa)
            {
                errors.Add($"{name}: the refund rate cannot be more than the MRP (₹{Rupees(batch.MrpPaisa)}).");
                continue;
            }
            if (request.DiscountPercent is < 0m or > 100m)
            {
                errors.Add($"{name}: discount must be between 0 and 100 %.");
                continue;
            }

            var a = WalkInLine(batch, request.Units, request.RatePerPackPaisa, request.DiscountPercent);
            amounts.Add(a);
            doc.Lines.Add(ToLine(a, request.Units, ResolveDisposition(request.Disposition, batch.ExpiryDate, doc.ReturnDate),
                null, batch.MedicineId, batch.MedicineName, batch.HsnCode, batch.BatchId, batch.BatchNumber, batch.ExpiryDate,
                batch.UnitsPerPack, request.RatePerPackPaisa, batch.MrpPaisa, request.DiscountPercent, batch.GstPercent));
        }
    }

    private static SaleReturnDocumentLine ToLine(SaleLineAmounts a, long units, SaleReturnDisposition disposition, long? saleItemId,
        long medicineId, string medicineName, string? hsn, long batchId, string batchNumber, DateTime expiry, int unitsPerPack,
        long rate, long mrp, decimal discount, decimal gst) => new()
    {
        SaleItemId = saleItemId,
        MedicineId = medicineId,
        MedicineName = medicineName,
        HsnCode = hsn,
        BatchId = batchId,
        BatchNumber = batchNumber,
        ExpiryDate = expiry,
        UnitsPerPack = Math.Max(unitsPerPack, 1),
        Units = units,
        RatePerPackPaisa = rate,
        MrpPerPackPaisa = mrp,
        DiscountPercent = discount,
        GstPercent = gst,
        GrossPaisa = a.GrossPaisa,
        DiscountPaisa = a.LineDiscountPaisa + a.BillDiscountPaisa,
        TaxablePaisa = a.TaxablePaisa,
        CgstPaisa = a.CgstPaisa,
        SgstPaisa = a.SgstPaisa,
        IgstPaisa = a.IgstPaisa,
        TaxPaisa = a.TaxPaisa,
        LineTotalPaisa = a.TotalPaisa,
        Disposition = disposition
    };

    private static void Totals(SaleReturnDocument doc, List<SaleLineAmounts> amounts, RoundOffMode roundOff, ReturnableSale? sale)
    {
        var totals = PricingCalculator.SummarizeBill(amounts, roundOff);
        doc.GrossPaisa = totals.GrossPaisa;
        doc.DiscountPaisa = totals.DiscountPaisa;
        doc.TaxablePaisa = totals.TaxablePaisa;
        doc.CgstPaisa = totals.CgstPaisa;
        doc.SgstPaisa = totals.SgstPaisa;
        doc.IgstPaisa = totals.IgstPaisa;
        doc.TaxPaisa = totals.TaxPaisa;
        doc.RoundOffPaisa = totals.RoundOffPaisa;
        doc.NetAmountPaisa = totals.NetPayablePaisa;

        if (sale is null || amounts.Count == 0) return;
        var cap = MaxCreditValue(sale);
        if (doc.NetAmountPaisa > cap)
        {
            // Rounding several partial returns up must not credit more than the bill: absorb it in the round-off.
            doc.NetAmountPaisa = cap;
            doc.RoundOffPaisa = cap - totals.UnroundedTotalPaisa;
        }
    }

    private static void ValidateSettlement(SaleReturnDocument doc, SaleReturnRequest request, ReturnableSale? sale, List<string> errors)
    {
        if (request.RefundMode == SaleRefundMode.CreditToCustomer && doc.CustomerId is not > 0)
            errors.Add(sale is null
                ? "Credit can only be given to a saved customer. Choose the customer, or refund now."
                : $"Invoice {sale.InvoiceNumber} is a counter sale without a saved customer, so the return must be refunded now.");

        if (request.RefundMode == SaleRefundMode.Cash)
        {
            if (!SalesReturnsLabels.RefundMethods.Contains(request.RefundMethod))
                errors.Add("Choose how the refund is paid: cash, UPI, card or bank.");
            var max = MaxRefundNow(sale);
            if (sale is not null && doc.NetAmountPaisa > max)
                errors.Add($"Only ₹{Rupees(sale.AmountPaidPaisa)} was received on invoice {sale.InvoiceNumber}" +
                           (sale.PreviouslyRefundedPaisa > 0 ? $" (₹{Rupees(sale.PreviouslyRefundedPaisa)} already refunded)" : "") +
                           $", so at most ₹{Rupees(max)} can be refunded now. Choose 'Credit to customer' instead.");
        }

        if (doc.Reason?.Length > MaxReasonLength) errors.Add($"Keep the reason within {MaxReasonLength} characters.");
        if (doc.Notes?.Length > MaxNotesLength) errors.Add($"Keep the notes within {MaxNotesLength} characters.");
        if (doc.RefundReference?.Length > MaxReferenceLength) errors.Add($"Keep the refund reference within {MaxReferenceLength} characters.");
    }

    /// <summary>Rules for paying out open credit from the Credit Notes list. Empty when valid.</summary>
    public static IReadOnlyList<string> ValidateRefund(SaleReturnDocument note, CreditRefundRequest refund, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(refund);
        var errors = new List<string>();
        if (note.IsCancelled) errors.Add($"Credit note {note.CreditNoteNumber} is cancelled.");
        if (refund.AmountPaisa <= 0) errors.Add("Enter the amount to refund.");
        else if (refund.AmountPaisa > note.CreditBalancePaisa)
            errors.Add($"Only ₹{Rupees(note.CreditBalancePaisa)} of credit note {note.CreditNoteNumber} is open.");
        if (!SalesReturnsLabels.RefundMethods.Contains(refund.Method)) errors.Add("Choose how the refund is paid: cash, UPI, card or bank.");
        if (refund.RefundDate.Date > today.Date) errors.Add("The refund date cannot be in the future.");
        if (refund.RefundDate.Date < note.ReturnDate.Date) errors.Add("The refund date cannot be before the return date.");
        if (refund.Reference?.Trim().Length > MaxReferenceLength) errors.Add($"Keep the reference within {MaxReferenceLength} characters.");
        return errors;
    }

    /// <summary>Rules for cancelling a sale return. Empty when valid.</summary>
    public static IReadOnlyList<string> ValidateCancel(SaleReturnDocument note, string? reason)
    {
        ArgumentNullException.ThrowIfNull(note);
        var errors = new List<string>();
        if (note.IsCancelled) errors.Add($"Sale return {note.ReturnNumber} is already cancelled.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) errors.Add("Enter why the return is being cancelled.");
        else if (reason.Trim().Length > MaxReasonLength) errors.Add($"Keep the reason within {MaxReasonLength} characters.");
        return errors;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
