using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Pricing;

/// <summary>Input for one sale-side line. All money is paisa; rates are per pack.</summary>
public sealed record SaleLineInput
{
    /// <summary>Selling rate per pack before the line discount (GST-inclusive when <see cref="TaxMode"/> is Inclusive).</summary>
    public required long RatePerPackPaisa { get; init; }

    /// <summary>Total units sold (use <see cref="PackQuantity.ToUnits"/> for packs + loose).</summary>
    public required long Units { get; init; }

    public int UnitsPerPack { get; init; } = 1;

    /// <summary>Line discount %, applied to the per-pack rate (0–100).</summary>
    public decimal DiscountPercent { get; init; }

    public decimal GstPercent { get; init; }

    /// <summary>Retail sale rates are GST-inclusive (MRP basis); B2B/exclusive pricing adds GST on top.</summary>
    public TaxMode TaxMode { get; init; } = TaxMode.Inclusive;

    /// <summary>IGST instead of CGST + SGST (see <see cref="PricingCalculator.IsInterStateSupply"/>).</summary>
    public bool IsInterState { get; init; }

    /// <summary>This line's share of a bill-level discount in value (from <see cref="PricingCalculator.DistributeAmount"/>).</summary>
    public long BillDiscountSharePaisa { get; init; }

    /// <summary>MRP per pack, only used for the MRP value / customer-savings figures (0 = unknown).</summary>
    public long MrpPerPackPaisa { get; init; }

    /// <summary>Cost per pack excluding GST (the batch's net purchase rate) for profit; 0 = unknown, profit reported as 0.</summary>
    public long CostPerPackExGstPaisa { get; init; }
}

/// <summary>Result of <see cref="PricingCalculator.CalculateSaleLine"/>. Invariant: Taxable + Tax == Total.</summary>
public sealed record SaleLineAmounts
{
    /// <summary>rate × units / pack, before any discount.</summary>
    public long GrossPaisa { get; init; }
    /// <summary>Per-pack rate after the line discount (what Localwell shows in RATE after DISC %).</summary>
    public long DiscountedRatePerPackPaisa { get; init; }
    public long LineDiscountPaisa { get; init; }
    public long BillDiscountPaisa { get; init; }
    public long TaxablePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
    public long IgstPaisa { get; init; }
    /// <summary>Amount the customer pays for the line, GST included.</summary>
    public long TotalPaisa { get; init; }
    public long MrpValuePaisa { get; init; }
    /// <summary>MRP value − total (can be negative if sold above MRP, which billing must block).</summary>
    public long SavingsPaisa { get; init; }
    public long CostPaisa { get; init; }
    /// <summary>Taxable − cost (both ex-GST); 0 when cost is unknown.</summary>
    public long ProfitPaisa { get; init; }
}

/// <summary>Input for one purchase line. Purchase rates are per pack and GST-exclusive (spec 04).</summary>
public sealed record PurchaseLineInput
{
    /// <summary>Billed purchase rate per pack, GST-exclusive (Localwell "PURC RATE"/PTR column).</summary>
    public required long RatePerPackPaisa { get; init; }

    /// <summary>Paid quantity in units.</summary>
    public required long Units { get; init; }

    /// <summary>Free quantity in units (scheme / bonus); costs nothing but lowers the net purchase rate.</summary>
    public long FreeUnits { get; init; }

    public int UnitsPerPack { get; init; } = 1;
    public decimal DiscountPercent { get; init; }
    public decimal SchemePercent { get; init; }
    /// <summary>Bill-level discount %, applied proportionally to every line's taxable value.</summary>
    public decimal BillDiscountPercent { get; init; }
    public decimal GstPercent { get; init; }
    public bool IsInterState { get; init; }
}

/// <summary>Result of <see cref="PricingCalculator.CalculatePurchaseLine"/>. Invariant: Taxable + Tax == Total.</summary>
public sealed record PurchaseLineAmounts
{
    /// <summary>rate × units / pack × (1 − disc) × (1 − scheme), before the bill discount.</summary>
    public long GrossPaisa { get; init; }
    public long TaxablePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long TotalPaisa { get; init; }
    /// <summary>Rate per pack after item, scheme and bill discounts (Localwell "NET RATE"), ex-GST.</summary>
    public long NetRatePerPackPaisa { get; init; }
    /// <summary>Landed cost per pack ex-GST spread over paid + free units (Localwell "batch_net_purchase_rate").</summary>
    public long NetPurchaseRatePerPackPaisa { get; init; }
}

/// <summary>CGST/SGST or IGST split of a tax amount.</summary>
public readonly record struct TaxSplit(long CgstPaisa, long SgstPaisa, long IgstPaisa)
{
    public long TotalPaisa => CgstPaisa + SgstPaisa + IgstPaisa;
}

/// <summary>A rounded invoice total and the ROUND OFF line that reconciles it.</summary>
public readonly record struct RoundOffResult(long RoundedPaisa, long RoundOffPaisa);

/// <summary>Totals for a bill built from <see cref="SaleLineAmounts"/>.</summary>
public sealed record BillTotals
{
    public long GrossPaisa { get; init; }
    /// <summary>Line discounts + bill discount.</summary>
    public long DiscountPaisa { get; init; }
    public long TaxablePaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long TaxPaisa { get; init; }
    /// <summary>Non-taxable add-ons such as a delivery fee (Localwell "custom charge").</summary>
    public long OtherChargesPaisa { get; init; }
    /// <summary>Credit adjusted against this bill (sale-return credit), subtracted.</summary>
    public long AdjustmentPaisa { get; init; }
    public long UnroundedTotalPaisa { get; init; }
    public long RoundOffPaisa { get; init; }
    /// <summary>Amount payable: unrounded total + round off.</summary>
    public long NetPayablePaisa { get; init; }
    public long MrpValuePaisa { get; init; }
    public long SavingsPaisa { get; init; }
    public long ProfitPaisa { get; init; }
}
