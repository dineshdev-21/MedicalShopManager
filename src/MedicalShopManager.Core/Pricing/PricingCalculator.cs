using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Pricing;

/// <summary>
/// The single home of every money calculation (MASTER-SPEC §4, spec 02 "Business rules", spec 04 "Business rules").
/// Pure and stateless: no I/O, no clock. Every module computes line amounts, tax, discounts, round-off and margins
/// through here instead of writing its own arithmetic.
///
/// Conventions:
/// <list type="bullet">
/// <item>Money is <c>long</c> paisa. Intermediate maths is <c>decimal</c>; every stored amount is rounded once to
/// whole paisa with <see cref="MidpointRounding.AwayFromZero"/> (<see cref="RoundPaisa"/>).</item>
/// <item>Rates (MRP, PTR, purchase, sale) are per <b>pack</b>; quantities are <b>units</b>. Amounts are computed
/// as rate × units / unitsPerPack and rounded on the line total, never on a per-unit price (spec 02 §4.8).</item>
/// <item>Sale rates are GST-inclusive (MRP basis): taxable = round(total × 100 / (100 + GST)) and tax = total − taxable,
/// so taxable + tax always equals what the customer pays. Purchase rates are GST-exclusive: tax = round(taxable × GST / 100).</item>
/// <item>Tax split: CGST = round(tax / 2), SGST = tax − CGST (any odd paisa goes to CGST); inter-state → all IGST.</item>
/// <item>Margin basis: selling price excluding GST — (saleEx − costEx) / saleEx × 100 (spec 02 "MARGIN %", verified).</item>
/// </list>
/// </summary>
public static class PricingCalculator
{
    /// <summary>Rounds a decimal paisa value to whole paisa, half away from zero.</summary>
    public static long RoundPaisa(decimal paisa) => (long)Math.Round(paisa, 0, MidpointRounding.AwayFromZero);

    /// <summary>round(amount × percent / 100).</summary>
    public static long PercentOf(long amountPaisa, decimal percent) => RoundPaisa(amountPaisa * percent / 100m);

    // ───────────────────────── Rates and quantities ─────────────────────────

    /// <summary>Value of <paramref name="units"/> at a per-pack rate: round(rate × units / unitsPerPack).</summary>
    public static long AmountForUnits(long ratePerPackPaisa, long units, int unitsPerPack)
    {
        EnsureUnitsPerPack(unitsPerPack);
        return RoundPaisa((decimal)ratePerPackPaisa * units / unitsPerPack);
    }

    /// <summary>Unrounded per-unit rate for display (e.g. 18840 per 15's = 1256 paisa).</summary>
    public static decimal UnitRatePaisa(long ratePerPackPaisa, int unitsPerPack)
    {
        EnsureUnitsPerPack(unitsPerPack);
        return (decimal)ratePerPackPaisa / unitsPerPack;
    }

    /// <summary>Rate after a percentage discount, rounded per pack: round(rate × (100 − d) / 100).</summary>
    public static long RateAfterDiscount(long ratePerPackPaisa, decimal discountPercent)
    {
        EnsurePercent(discountPercent, nameof(discountPercent));
        return RoundPaisa(ratePerPackPaisa * (100m - discountPercent) / 100m);
    }

    /// <summary>Discount % implied by a rate relative to a reference (usually MRP), rounded to 2 dp. 0 when reference is 0.</summary>
    public static decimal DiscountPercentFromRate(long referenceRatePaisa, long ratePaisa)
    {
        if (referenceRatePaisa <= 0) return 0m;
        return Math.Round(100m * (referenceRatePaisa - ratePaisa) / referenceRatePaisa, 2, MidpointRounding.AwayFromZero);
    }

    // ───────────────────────── GST ─────────────────────────

    /// <summary>Taxable value contained in a GST-inclusive amount: round(amount × 100 / (100 + gst)).</summary>
    public static long ExtractTaxable(long inclusiveAmountPaisa, decimal gstPercent)
    {
        EnsureGst(gstPercent);
        return RoundPaisa(inclusiveAmountPaisa * 100m / (100m + gstPercent));
    }

    /// <summary>GST on a taxable value: round(taxable × gst / 100).</summary>
    public static long TaxOnTaxable(long taxablePaisa, decimal gstPercent)
    {
        EnsureGst(gstPercent);
        return PercentOf(taxablePaisa, gstPercent);
    }

    /// <summary>Splits a tax amount into CGST + SGST (intra-state) or IGST (inter-state).</summary>
    public static TaxSplit SplitTax(long taxPaisa, bool isInterState)
    {
        if (isInterState) return new TaxSplit(0, 0, taxPaisa);
        var cgst = RoundPaisa(taxPaisa / 2m);
        return new TaxSplit(cgst, taxPaisa - cgst, 0);
    }

    /// <summary>
    /// Inter-state supply when the party has a GSTIN whose state code (first two digits) differs from the business
    /// state code. Walk-in / unregistered customers are always intra-state (MASTER-SPEC §4.2).
    /// <paramref name="businessStateCode"/> may be the 1–2 digit state code or the shop's full GSTIN.
    /// </summary>
    public static bool IsInterStateSupply(string? businessStateCode, string? partyGstin)
    {
        if (string.IsNullOrWhiteSpace(businessStateCode) || string.IsNullOrWhiteSpace(partyGstin)) return false;
        var party = partyGstin.Trim();
        if (party.Length < 2) return false;
        var business = businessStateCode.Trim();
        business = business.Length >= 2 ? business[..2] : business.PadLeft(2, '0');
        return !string.Equals(party[..2], business, StringComparison.Ordinal);
    }

    // ───────────────────────── Discounts ─────────────────────────

    /// <summary>
    /// Clamps a discount % into [min, max] for UI entry; a null max means no item-level cap. Bounds are themselves
    /// kept inside 0–100, so the result is always a valid discount (posting code still rejects &gt; 100 via guards).
    /// </summary>
    public static decimal ClampDiscount(decimal requestedPercent, decimal minPercent = 0m, decimal? maxPercent = null)
    {
        var max = Math.Clamp(maxPercent ?? 100m, 0m, 100m);
        var min = Math.Clamp(minPercent, 0m, 100m);
        if (min > max) min = max;
        return Math.Clamp(requestedPercent, min, max);
    }

    /// <summary>
    /// Sale-side bill discount in percent (spec 02 "Bill discount"): the bill % is added to each line's own %
    /// and clamped to that line's [min, max], so the saved invoice carries only line discounts.
    /// </summary>
    public static decimal CombineBillDiscount(decimal lineDiscountPercent, decimal billDiscountPercent,
        decimal minPercent = 0m, decimal? maxPercent = null)
    {
        EnsurePercent(billDiscountPercent, nameof(billDiscountPercent));
        return ClampDiscount(lineDiscountPercent + billDiscountPercent, minPercent, maxPercent);
    }

    /// <summary>
    /// Distributes an amount (e.g. a bill discount in rupees) across lines pro rata to <paramref name="weights"/>
    /// (normally each line's total). Uses largest remainder so the shares sum exactly to <paramref name="amountPaisa"/>;
    /// ties go to the earlier line. Every share is ≤ its weight when amount ≤ Σ weights.
    /// </summary>
    public static long[] DistributeAmount(long amountPaisa, IReadOnlyList<long> weights)
    {
        if (amountPaisa < 0) throw new ArgumentOutOfRangeException(nameof(amountPaisa), "Amount cannot be negative.");
        if (weights.Any(w => w < 0)) throw new ArgumentOutOfRangeException(nameof(weights), "Weights cannot be negative.");

        var shares = new long[weights.Count];
        var totalWeight = weights.Sum();
        if (amountPaisa == 0) return shares;
        if (totalWeight == 0) throw new InvalidOperationException("Cannot distribute an amount over lines that are all zero.");
        if (amountPaisa > totalWeight) throw new InvalidOperationException("Amount to distribute exceeds the total of the lines.");

        var remainders = new decimal[weights.Count];
        long allocated = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var exact = (decimal)amountPaisa * weights[i] / totalWeight;
            shares[i] = (long)Math.Floor(exact);
            remainders[i] = exact - shares[i];
            allocated += shares[i];
        }

        var order = Enumerable.Range(0, weights.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i);
        foreach (var i in order.Take((int)(amountPaisa - allocated)))
            shares[i]++;

        return shares;
    }

    // ───────────────────────── Sale line and bill ─────────────────────────

    /// <summary>
    /// One sale line (spec 02, retailer): discounted rate = round(rate × (1 − d)); line amount = round(discounted rate
    /// × units / pack); then the bill-discount share is taken off; then GST is backed out (inclusive) or added (exclusive).
    /// </summary>
    public static SaleLineAmounts CalculateSaleLine(SaleLineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Units < 0) throw new ArgumentOutOfRangeException(nameof(input), "Units cannot be negative.");
        if (input.RatePerPackPaisa < 0) throw new ArgumentOutOfRangeException(nameof(input), "Rate cannot be negative.");
        EnsurePercent(input.DiscountPercent, nameof(input.DiscountPercent));
        EnsureGst(input.GstPercent);

        var gross = AmountForUnits(input.RatePerPackPaisa, input.Units, input.UnitsPerPack);
        var discountedRate = RateAfterDiscount(input.RatePerPackPaisa, input.DiscountPercent);
        var afterLineDiscount = AmountForUnits(discountedRate, input.Units, input.UnitsPerPack);

        if (input.BillDiscountSharePaisa < 0 || input.BillDiscountSharePaisa > afterLineDiscount)
            throw new ArgumentOutOfRangeException(nameof(input), "Bill discount share must be between 0 and the line amount.");
        var net = afterLineDiscount - input.BillDiscountSharePaisa;

        long taxable, tax, total;
        if (input.TaxMode == TaxMode.Inclusive)
        {
            total = net;
            taxable = ExtractTaxable(net, input.GstPercent);
            tax = total - taxable;
        }
        else
        {
            taxable = net;
            tax = TaxOnTaxable(taxable, input.GstPercent);
            total = taxable + tax;
        }

        var split = SplitTax(tax, input.IsInterState);
        var mrpValue = input.MrpPerPackPaisa > 0 ? AmountForUnits(input.MrpPerPackPaisa, input.Units, input.UnitsPerPack) : 0;
        var cost = input.CostPerPackExGstPaisa > 0 ? AmountForUnits(input.CostPerPackExGstPaisa, input.Units, input.UnitsPerPack) : 0;

        return new SaleLineAmounts
        {
            GrossPaisa = gross,
            DiscountedRatePerPackPaisa = discountedRate,
            LineDiscountPaisa = gross - afterLineDiscount,
            BillDiscountPaisa = input.BillDiscountSharePaisa,
            TaxablePaisa = taxable,
            TaxPaisa = tax,
            CgstPaisa = split.CgstPaisa,
            SgstPaisa = split.SgstPaisa,
            IgstPaisa = split.IgstPaisa,
            TotalPaisa = total,
            MrpValuePaisa = mrpValue,
            SavingsPaisa = mrpValue > 0 ? mrpValue - total : 0,
            CostPaisa = cost,
            ProfitPaisa = cost > 0 ? taxable - cost : 0
        };
    }

    /// <summary>
    /// Sums lines into bill totals and applies the round-off. Unrounded = Σ line totals + other charges − adjustment;
    /// net payable = rounded unrounded total (the difference is the ROUND OFF line).
    /// </summary>
    public static BillTotals SummarizeBill(IReadOnlyList<SaleLineAmounts> lines, RoundOffMode roundOff = RoundOffMode.NearestRupee,
        long otherChargesPaisa = 0, long adjustmentPaisa = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (otherChargesPaisa < 0) throw new ArgumentOutOfRangeException(nameof(otherChargesPaisa));
        if (adjustmentPaisa < 0) throw new ArgumentOutOfRangeException(nameof(adjustmentPaisa));

        var linesTotal = lines.Sum(l => l.TotalPaisa);
        var unrounded = linesTotal + otherChargesPaisa - adjustmentPaisa;
        if (unrounded < 0) throw new InvalidOperationException("Adjustment cannot exceed the bill amount.");
        var rounded = RoundOff(unrounded, roundOff);

        return new BillTotals
        {
            GrossPaisa = lines.Sum(l => l.GrossPaisa),
            DiscountPaisa = lines.Sum(l => l.LineDiscountPaisa + l.BillDiscountPaisa),
            TaxablePaisa = lines.Sum(l => l.TaxablePaisa),
            CgstPaisa = lines.Sum(l => l.CgstPaisa),
            SgstPaisa = lines.Sum(l => l.SgstPaisa),
            IgstPaisa = lines.Sum(l => l.IgstPaisa),
            TaxPaisa = lines.Sum(l => l.TaxPaisa),
            OtherChargesPaisa = otherChargesPaisa,
            AdjustmentPaisa = adjustmentPaisa,
            UnroundedTotalPaisa = unrounded,
            RoundOffPaisa = rounded.RoundOffPaisa,
            NetPayablePaisa = rounded.RoundedPaisa,
            MrpValuePaisa = lines.Sum(l => l.MrpValuePaisa),
            SavingsPaisa = lines.Sum(l => l.SavingsPaisa),
            ProfitPaisa = lines.Sum(l => l.ProfitPaisa)
        };
    }

    /// <summary>Rounds an amount to the nearest rupee (or 50 paise), half away from zero, and returns the round-off.</summary>
    public static RoundOffResult RoundOff(long amountPaisa, RoundOffMode mode)
    {
        var step = mode switch
        {
            RoundOffMode.None => 1L,
            RoundOffMode.NearestRupee => 100L,
            RoundOffMode.NearestFiftyPaise => 50L,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var rounded = RoundPaisa((decimal)amountPaisa / step) * step;
        return new RoundOffResult(rounded, rounded - amountPaisa);
    }

    // ───────────────────────── Purchase line ─────────────────────────

    /// <summary>
    /// One purchase line (spec 04, from the web client): gross = rate × units / pack × (1 − disc) × (1 − scheme);
    /// taxable = gross × (1 − bill disc); tax added on top; net purchase rate = taxable / (paid + free) per pack.
    /// </summary>
    public static PurchaseLineAmounts CalculatePurchaseLine(PurchaseLineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureUnitsPerPack(input.UnitsPerPack);
        if (input.Units < 0 || input.FreeUnits < 0) throw new ArgumentOutOfRangeException(nameof(input), "Quantities cannot be negative.");
        if (input.RatePerPackPaisa < 0) throw new ArgumentOutOfRangeException(nameof(input), "Rate cannot be negative.");
        EnsurePercent(input.DiscountPercent, nameof(input.DiscountPercent));
        EnsurePercent(input.SchemePercent, nameof(input.SchemePercent));
        EnsurePercent(input.BillDiscountPercent, nameof(input.BillDiscountPercent));
        EnsureGst(input.GstPercent);

        var keep = (1m - input.DiscountPercent / 100m) * (1m - input.SchemePercent / 100m);
        var gross = RoundPaisa((decimal)input.RatePerPackPaisa * input.Units / input.UnitsPerPack * keep);
        var taxable = RoundPaisa(gross * (1m - input.BillDiscountPercent / 100m));
        var tax = TaxOnTaxable(taxable, input.GstPercent);
        var split = SplitTax(tax, input.IsInterState);

        var allUnits = input.Units + input.FreeUnits;
        return new PurchaseLineAmounts
        {
            GrossPaisa = gross,
            TaxablePaisa = taxable,
            TaxPaisa = tax,
            CgstPaisa = split.CgstPaisa,
            SgstPaisa = split.SgstPaisa,
            IgstPaisa = split.IgstPaisa,
            TotalPaisa = taxable + tax,
            NetRatePerPackPaisa = RoundPaisa(input.RatePerPackPaisa * keep * (1m - input.BillDiscountPercent / 100m)),
            NetPurchaseRatePerPackPaisa = allUnits == 0 ? 0 : RoundPaisa((decimal)taxable * input.UnitsPerPack / allUnits)
        };
    }

    /// <summary>Converts a bill discount in value to a percentage of <paramref name="basePaisa"/> (4 dp), as the purchase screen does.</summary>
    public static decimal PercentFromAmount(long amountPaisa, long basePaisa)
    {
        if (basePaisa <= 0) return 0m;
        return Math.Round(100m * amountPaisa / basePaisa, 4, MidpointRounding.AwayFromZero);
    }

    // ───────────────────────── Margin ─────────────────────────

    /// <summary>
    /// Margin % on the selling price excluding GST (spec 02 "MARGIN %", verified on screen):
    /// saleEx = saleRate × 100 / (100 + gst); margin = (saleEx − cost) / saleEx × 100, rounded to 2 dp.
    /// <paramref name="costPerPackExGstPaisa"/> is the batch's net purchase rate. Returns 0 when saleEx is 0.
    /// </summary>
    public static decimal MarginPercent(long saleRatePerPackInclGstPaisa, decimal gstPercent, long costPerPackExGstPaisa)
    {
        EnsureGst(gstPercent);
        var saleEx = saleRatePerPackInclGstPaisa * 100m / (100m + gstPercent);
        if (saleEx <= 0) return 0m;
        return Math.Round((saleEx - costPerPackExGstPaisa) / saleEx * 100m, 2, MidpointRounding.AwayFromZero);
    }

    // ───────────────────────── Guards ─────────────────────────

    internal static void EnsureUnitsPerPack(int unitsPerPack)
    {
        if (unitsPerPack < 1)
            throw new ArgumentOutOfRangeException(nameof(unitsPerPack), "Units per pack must be at least 1.");
    }

    private static void EnsurePercent(decimal percent, string name)
    {
        if (percent < 0m || percent > 100m)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between 0 and 100.");
    }

    private static void EnsureGst(decimal gstPercent)
    {
        if (gstPercent < 0m || gstPercent > 100m)
            throw new ArgumentOutOfRangeException(nameof(gstPercent), "GST % must be between 0 and 100.");
    }
}
