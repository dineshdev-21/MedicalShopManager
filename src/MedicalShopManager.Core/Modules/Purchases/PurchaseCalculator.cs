using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Computed amounts of one purchase line.</summary>
public sealed record PurchaseLineResult(
    long PurchaseRateExGstPaisa,
    long ValueBeforeDiscountPaisa,
    long DiscountAmountPaisa,
    long TaxablePaisa,
    long TaxPaisa,
    long CgstPaisa,
    long SgstPaisa,
    long IgstPaisa,
    long TotalPaisa,
    long NetRatePaisa,
    long NetPurchaseRatePaisa,
    decimal MarginPercent);

/// <summary>Bill totals (spec 04 "Totals / footer").</summary>
public sealed record PurchaseTotals(
    long SubtotalPaisa,
    long DiscountPaisa,
    long TaxablePaisa,
    long CgstPaisa,
    long SgstPaisa,
    long IgstPaisa,
    long TaxPaisa,
    long OtherChargesPaisa,
    long RoundOffPaisa,
    long GrandTotalPaisa,
    long TotalUnits);

public sealed record PurchaseBillCalculation(IReadOnlyList<PurchaseLineResult> Lines, PurchaseTotals Totals);

/// <summary>
/// Purchase-side rules on top of <see cref="PricingCalculator"/> (which does every money operation): line and bill
/// totals, GST-inclusive rate entry, "bill has GST" = No, round-off, payment status, due date, scheme free quantity,
/// default sale rate and margin. Pure; no I/O and no clock (callers pass "today").
/// </summary>
public static class PurchaseCalculator
{
    /// <summary>Rate per pack ex-GST: the entered rate, or the entered GST-inclusive rate with GST backed out.</summary>
    public static long ExGstRate(long enteredRatePaisa, decimal gstPercent, bool ratesIncludeGst) =>
        ratesIncludeGst ? PricingCalculator.ExtractTaxable(enteredRatePaisa, gstPercent) : enteredRatePaisa;

    /// <summary>One line. GST is charged only when the bill has a GST column; the line's GST % still goes on the batch.</summary>
    public static PurchaseLineResult CalculateLine(PurchaseBillLine line, PurchaseBill bill)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(bill);

        var rateEx = ExGstRate(line.EnteredRatePaisa, line.GstRatePercent, bill.RatesIncludeGst);
        var amounts = PricingCalculator.CalculatePurchaseLine(new PurchaseLineInput
        {
            RatePerPackPaisa = rateEx,
            Units = line.Quantity,
            FreeUnits = line.FreeQuantity,
            UnitsPerPack = line.UnitsPerPack,
            DiscountPercent = line.DiscountPercent,
            SchemePercent = line.SchemePercent,
            BillDiscountPercent = bill.BillDiscountPercent,
            GstPercent = bill.WithGst ? line.GstRatePercent : 0m,
            IsInterState = bill.IsInterState
        });

        var valueBefore = PricingCalculator.AmountForUnits(rateEx, line.Quantity, line.UnitsPerPack);
        var margin = line.SaleRatePaisa > 0
            ? PricingCalculator.MarginPercent(line.SaleRatePaisa, line.GstRatePercent, amounts.NetPurchaseRatePerPackPaisa)
            : 0m;

        return new PurchaseLineResult(rateEx, valueBefore, valueBefore - amounts.TaxablePaisa, amounts.TaxablePaisa,
            amounts.TaxPaisa, amounts.CgstPaisa, amounts.SgstPaisa, amounts.IgstPaisa, amounts.TotalPaisa,
            amounts.NetRatePerPackPaisa, amounts.NetPurchaseRatePerPackPaisa, margin);
    }

    /// <summary>
    /// Every line plus the bill totals: invoice amount = Σ taxable + Σ GST + other charges, rounded with
    /// <paramref name="roundOff"/> (spec 04: <c>purchase_invoice_amount = round(amount, 0)</c>).
    /// </summary>
    public static PurchaseBillCalculation Calculate(PurchaseBill bill, RoundOffMode roundOff = RoundOffMode.NearestRupee)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.OtherChargesPaisa < 0) throw new ArgumentOutOfRangeException(nameof(bill), "Other charges cannot be negative.");

        var lines = bill.Lines.Select(l => CalculateLine(l, bill)).ToList();
        var taxable = lines.Sum(l => l.TaxablePaisa);
        var tax = lines.Sum(l => l.TaxPaisa);
        var rounded = PricingCalculator.RoundOff(taxable + tax + bill.OtherChargesPaisa, roundOff);

        var totals = new PurchaseTotals(
            SubtotalPaisa: lines.Sum(l => l.ValueBeforeDiscountPaisa),
            DiscountPaisa: lines.Sum(l => l.DiscountAmountPaisa),
            TaxablePaisa: taxable,
            CgstPaisa: lines.Sum(l => l.CgstPaisa),
            SgstPaisa: lines.Sum(l => l.SgstPaisa),
            IgstPaisa: lines.Sum(l => l.IgstPaisa),
            TaxPaisa: tax,
            OtherChargesPaisa: bill.OtherChargesPaisa,
            RoundOffPaisa: rounded.RoundOffPaisa,
            GrandTotalPaisa: rounded.RoundedPaisa,
            TotalUnits: bill.Lines.Sum(l => (long)l.Quantity + l.FreeQuantity));
        return new PurchaseBillCalculation(lines, totals);
    }

    /// <summary>Calculates and writes every computed field onto the bill and its lines (the one place they are set).</summary>
    public static PurchaseTotals Recalculate(PurchaseBill bill, RoundOffMode roundOff = RoundOffMode.NearestRupee)
    {
        var calc = Calculate(bill, roundOff);
        for (var i = 0; i < bill.Lines.Count; i++)
        {
            var line = bill.Lines[i];
            var r = calc.Lines[i];
            line.LineNo = i + 1;
            line.PurchaseRatePaisa = r.PurchaseRateExGstPaisa;
            if (line.PtrPaisa <= 0) line.PtrPaisa = r.PurchaseRateExGstPaisa;
            line.ValueBeforeDiscountPaisa = r.ValueBeforeDiscountPaisa;
            line.DiscountAmountPaisa = r.DiscountAmountPaisa;
            line.TaxablePaisa = r.TaxablePaisa;
            line.GstAmountPaisa = r.TaxPaisa;
            line.CgstPaisa = r.CgstPaisa;
            line.SgstPaisa = r.SgstPaisa;
            line.IgstPaisa = r.IgstPaisa;
            line.LineTotalPaisa = r.TotalPaisa;
            line.NetRatePaisa = r.NetRatePaisa;
            line.NetPurchaseRatePaisa = r.NetPurchaseRatePaisa;
            line.MarginPercent = r.MarginPercent;
        }

        var t = calc.Totals;
        bill.SubtotalPaisa = t.SubtotalPaisa;
        bill.DiscountAmountPaisa = t.DiscountPaisa;
        bill.TaxablePaisa = t.TaxablePaisa;
        bill.CgstPaisa = t.CgstPaisa;
        bill.SgstPaisa = t.SgstPaisa;
        bill.IgstPaisa = t.IgstPaisa;
        bill.TaxAmountPaisa = t.TaxPaisa;
        bill.RoundOffPaisa = t.RoundOffPaisa;
        bill.GrandTotalPaisa = t.GrandTotalPaisa;
        bill.BalancePaisa = t.GrandTotalPaisa - bill.AmountPaidPaisa;
        bill.PaymentStatus = PaymentStatusFor(t.GrandTotalPaisa, bill.AmountPaidPaisa);
        return t;
    }

    /// <summary>Paid when nothing is owed, Partial when something but not all was paid, else Unpaid (credit).</summary>
    public static PaymentStatus PaymentStatusFor(long grandTotalPaisa, long amountPaidPaisa)
    {
        if (amountPaidPaisa >= grandTotalPaisa) return PaymentStatus.Paid;
        return amountPaidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid;
    }

    /// <summary>Payment due date = invoice date + credit days (null when the distributor gives no credit period).</summary>
    public static DateTime? DueDate(DateTime invoiceDate, int creditDays) =>
        creditDays > 0 ? invoiceDate.Date.AddDays(creditDays) : null;

    /// <summary>Default selling rate per pack: MRP less the shop's default sale discount % (0 = sell at MRP).</summary>
    public static long DefaultSaleRate(long mrpPaisa, decimal saleDiscountPercent) =>
        mrpPaisa <= 0 ? 0 : PricingCalculator.RateAfterDiscount(mrpPaisa, PricingCalculator.ClampDiscount(saleDiscountPercent));

    /// <summary>
    /// Free packs earned by a "X + Y" scheme on <paramref name="packs"/> paid packs (spec 04
    /// <c>getQtyAndFreeQtyConsideringScheme</c>): Full = Y × ⌊Q/X⌋; Half applies from Q ≥ X/2 and counts a fraction
    /// ≥ 0.4 of a scheme as 0.5; Quarter applies from Q ≥ X/4 with 0.2–0.4 → 0.25, 0.4–0.6 → 0.5, &gt; 0.6 → 0.75.
    /// </summary>
    public static decimal FreePacksFromScheme(decimal packs, int schemeQuantity, int schemeFreeQuantity, SchemeRounding rounding = SchemeRounding.Full)
    {
        if (packs <= 0 || schemeQuantity <= 0 || schemeFreeQuantity <= 0) return 0m;
        var ratio = packs / schemeQuantity;
        var whole = Math.Floor(ratio);
        var fraction = ratio - whole;

        var multiplier = rounding switch
        {
            SchemeRounding.Half when packs * 2 < schemeQuantity => 0m,
            SchemeRounding.Half => whole + (fraction >= 0.4m ? 0.5m : 0m),
            SchemeRounding.Quarter when packs * 4 < schemeQuantity => 0m,
            SchemeRounding.Quarter => whole + fraction switch
            {
                > 0.6m => 0.75m,
                >= 0.4m => 0.5m,
                >= 0.2m => 0.25m,
                _ => 0m
            },
            _ => whole
        };
        return multiplier * schemeFreeQuantity;
    }

    /// <summary>Free packs → whole free units (a half pack of a 15's strip gives 7 units).</summary>
    public static int FreeUnitsFromPacks(decimal freePacks, int unitsPerPack)
    {
        if (unitsPerPack < 1) throw new ArgumentOutOfRangeException(nameof(unitsPerPack));
        return freePacks <= 0 ? 0 : (int)Math.Floor(freePacks * unitsPerPack);
    }

    /// <summary>
    /// Packs typed on the purchase grid (QTY / FREE columns) → units. Null when the packs do not make whole units
    /// (e.g. 0.3 of a 10's strip) or are negative, so the screen can ask for a different quantity.
    /// </summary>
    public static int? UnitsFromPacks(decimal packs, int unitsPerPack)
    {
        if (unitsPerPack < 1) throw new ArgumentOutOfRangeException(nameof(unitsPerPack));
        if (packs < 0) return null;
        // 4 dp absorbs the repeating decimals of packs derived from units (7 units of 15 = 0.4666… packs).
        var units = Math.Round(packs * unitsPerPack, 4, MidpointRounding.AwayFromZero);
        if (units != Math.Floor(units) || units > int.MaxValue) return null;
        return (int)units;
    }

    /// <summary>Units → packs for display on the grid (15 units of a 10's strip → 1.5).</summary>
    public static decimal PacksFromUnits(int units, int unitsPerPack) =>
        unitsPerPack < 1 ? throw new ArgumentOutOfRangeException(nameof(unitsPerPack)) : (decimal)units / unitsPerPack;

    /// <summary>
    /// Debit note lines and totals: amount = rate × units / pack ex-GST, GST added on top (purchase side), totals
    /// rounded with <paramref name="roundOff"/>. Writes the computed fields onto the document.
    /// </summary>
    public static void RecalculateReturn(PurchaseReturnDocument doc, RoundOffMode roundOff = RoundOffMode.NearestRupee)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var line in doc.Lines)
        {
            var amounts = PricingCalculator.CalculatePurchaseLine(new PurchaseLineInput
            {
                RatePerPackPaisa = line.RatePaisa,
                Units = line.Quantity,
                UnitsPerPack = line.UnitsPerPack,
                GstPercent = line.GstRatePercent,
                IsInterState = doc.IsInterState
            });
            line.TaxablePaisa = amounts.TaxablePaisa;
            line.TaxPaisa = amounts.TaxPaisa;
            line.CgstPaisa = amounts.CgstPaisa;
            line.SgstPaisa = amounts.SgstPaisa;
            line.IgstPaisa = amounts.IgstPaisa;
            line.LineTotalPaisa = amounts.TotalPaisa;
        }

        doc.TaxablePaisa = doc.Lines.Sum(l => l.TaxablePaisa);
        doc.CgstPaisa = doc.Lines.Sum(l => l.CgstPaisa);
        doc.SgstPaisa = doc.Lines.Sum(l => l.SgstPaisa);
        doc.IgstPaisa = doc.Lines.Sum(l => l.IgstPaisa);
        doc.TaxAmountPaisa = doc.Lines.Sum(l => l.TaxPaisa);
        var rounded = PricingCalculator.RoundOff(doc.TaxablePaisa + doc.TaxAmountPaisa, roundOff);
        doc.RoundOffPaisa = rounded.RoundOffPaisa;
        doc.NetAmountPaisa = rounded.RoundedPaisa;
    }

    /// <summary>Rupees typed by the user (e.g. 35.50) → paisa, rounded to whole paisa.</summary>
    public static long ToPaisa(decimal rupees) => PricingCalculator.RoundPaisa(rupees * 100m);
}
