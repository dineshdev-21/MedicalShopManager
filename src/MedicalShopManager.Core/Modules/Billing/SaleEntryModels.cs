using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>
/// One bill line as entered on the POS grid (spec 02 §H). Rates are per pack; quantity is packs + loose units
/// (+ free units, given at no charge). Init-only properties so the draft JSON round-trips.
/// </summary>
public sealed record BillLine
{
    public long MedicineId { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? Composition { get; init; }
    public DrugSchedule Schedule { get; init; }
    public string? HsnCode { get; init; }
    public string? RackLocation { get; init; }

    public long BatchId { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }

    /// <summary>The batch's pack-size snapshot (rate basis).</summary>
    public int UnitsPerPack { get; init; } = 1;
    public bool AllowLooseSale { get; init; } = true;

    /// <summary>MRP per pack (GST-inclusive). Localwell computes the discount relative to MRP.</summary>
    public long MrpPerPackPaisa { get; init; }
    /// <summary>Rate the discount applies to, per pack (normally MRP; see <see cref="BillingCalculator.DefaultDiscountPercent"/>).</summary>
    public long RatePerPackPaisa { get; init; }
    public decimal DiscountPercent { get; init; }
    /// <summary>Item-level discount cap (Medicine.MaxDiscountPercent); null = no cap.</summary>
    public decimal? MaxDiscountPercent { get; init; }
    public decimal GstPercent { get; init; }

    public long Packs { get; init; }
    public long Loose { get; init; }
    /// <summary>Free units handed over at no charge (they still leave stock).</summary>
    public long FreeUnits { get; init; }

    /// <summary>Batch cost per pack ex-GST (purchase rate) for margin / profit; 0 = unknown.</summary>
    public long CostPerPackExGstPaisa { get; init; }

    /// <summary>Units the batch can supply to this bill (stock, plus this sale's own units when editing).</summary>
    public long AvailableUnits { get; init; }

    /// <summary>Paid units = packs × pack + loose; throws on negative input.</summary>
    public long PaidUnits => new PackQuantity(Packs, Loose).ToUnits(Math.Max(UnitsPerPack, 1));

    /// <summary>Units leaving stock: paid + free.</summary>
    public long StockUnits => PaidUnits + FreeUnits;
}

/// <summary>One payment received against the bill (spec 02 §M steps 2–3).</summary>
public sealed record SaleTender
{
    public TenderMode Mode { get; init; } = TenderMode.Cash;
    public long AmountPaisa { get; init; }
    /// <summary>UPI/bank UTR, card approval code or cheque number.</summary>
    public string? Reference { get; init; }
    public DateTime? ChequeDate { get; init; }
}

/// <summary>
/// Everything the billing screen sends to save (new or edit) a sale, also the JSON content of an unsaved draft.
/// </summary>
public sealed record SalePostingRequest
{
    /// <summary>Set when editing an existing sale (F4 in view mode); null for a new sale.</summary>
    public long? EditingSaleId { get; init; }
    /// <summary>The auto-saved draft this bill came from; deleted when the sale posts.</summary>
    public long? DraftId { get; init; }

    public DateTime InvoiceDate { get; init; } = DateTime.Now;

    /// <summary>Walk-in "Cash/Counter Sale": customer optional, must be paid in full.</summary>
    public bool IsCounterSale { get; init; } = true;
    public long? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerGstin { get; init; }

    public string? PatientName { get; init; }
    public string? PatientAge { get; init; }
    public PersonGender PatientGender { get; init; }
    public string? PatientAddress { get; init; }

    /// <summary>Doctor master id (DoctorsFollowups module); null until that module exists.</summary>
    public long? DoctorId { get; init; }
    public string? DoctorName { get; init; }
    public string? DoctorRegNo { get; init; }
    public string? PrescriptionNumber { get; init; }
    /// <summary>Path of the attached prescription image/PDF (copied into the app data folder by the UI).</summary>
    public string? PrescriptionFilePath { get; init; }

    public IReadOnlyList<BillLine> Lines { get; init; } = Array.Empty<BillLine>();

    public BillDiscountKind BillDiscountKind { get; init; }
    public decimal BillDiscountPercent { get; init; }
    public long BillDiscountAmountPaisa { get; init; }

    public string? CustomChargeName { get; init; }
    public long CustomChargePaisa { get; init; }

    /// <summary>WITH GST? (spec 02 §K). False = bill of supply: no GST is computed on the lines.</summary>
    public bool WithGst { get; init; } = true;

    /// <summary>UNPAID/CREDIT in payment step 1; the balance becomes a receivable with <see cref="DueDate"/>.</summary>
    public bool IsCredit { get; init; }
    public DateTime? DueDate { get; init; }
    public IReadOnlyList<SaleTender> Tenders { get; init; } = Array.Empty<SaleTender>();
    /// <summary>Cash handed over by the customer (≥ cash tender); the difference is the change.</summary>
    public long CashTenderedPaisa { get; init; }

    public string? Notes { get; init; }
    public long? UserId { get; init; }

    public bool HasCustomer => !IsCounterSale && CustomerId is > 0;
}

/// <summary>Input of <see cref="BillingCalculator.ComputeBill"/>: the amount-relevant part of a bill.</summary>
public sealed record BillRequest
{
    public IReadOnlyList<BillLine> Lines { get; init; } = Array.Empty<BillLine>();
    public BillDiscountKind BillDiscountKind { get; init; }
    public decimal BillDiscountPercent { get; init; }
    public long BillDiscountAmountPaisa { get; init; }
    public long CustomChargePaisa { get; init; }
    public bool WithGst { get; init; } = true;
    public bool IsInterState { get; init; }
    public RoundOffMode RoundOff { get; init; } = RoundOffMode.NearestRupee;
    /// <summary>Bounds a bill discount in percent is clamped to (Sale Settings min / max discount).</summary>
    public decimal BillDiscountMinPercent { get; init; }
    public decimal BillDiscountMaxPercent { get; init; } = 100m;

    /// <summary>
    /// The amount-relevant part of a sale request under the given settings. A shop that does not charge GST
    /// (composition / unregistered: Bill of Supply) never computes tax, whatever the WITH GST toggle says.
    /// </summary>
    public static BillRequest From(SalePostingRequest request, bool isInterState, BillingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        return new BillRequest
        {
            Lines = request.Lines,
            BillDiscountKind = request.BillDiscountKind,
            BillDiscountPercent = request.BillDiscountPercent,
            BillDiscountAmountPaisa = request.BillDiscountAmountPaisa,
            CustomChargePaisa = request.CustomChargePaisa,
            WithGst = request.WithGst && settings.ShopChargesGst,
            IsInterState = isInterState,
            RoundOff = settings.RoundOff,
            BillDiscountMinPercent = settings.MinDiscountPercent,
            BillDiscountMaxPercent = settings.MaxDiscountPercent
        };
    }
}

/// <summary>A bill line with its computed amounts (what the grid shows and the sale stores).</summary>
public sealed record ComputedBillLine(
    BillLine Line,
    long PaidUnits,
    decimal EffectiveDiscountPercent,
    decimal TaxPercent,
    SaleLineAmounts Amounts,
    decimal MarginPercent);

/// <summary>Result of <see cref="BillingCalculator.ComputeBill"/>.</summary>
public sealed record BillComputation(IReadOnlyList<ComputedBillLine> Lines, BillTotals Totals)
{
    public int ItemCount => Lines.Count;
    public long TotalUnits => Lines.Sum(l => l.Line.StockUnits);
    /// <summary>Bill-level discount actually given (line discounts excluded).</summary>
    public long BillDiscountPaisa => Lines.Sum(l => l.Amounts.BillDiscountPaisa);
}

/// <summary>Outcome of the payment step: what was paid, what is due and the change to hand back.</summary>
public sealed record PaymentResolution
{
    public long AmountPaidPaisa { get; init; }
    public long BalanceDuePaisa { get; init; }
    public long ChangePaisa { get; init; }
    public long CashTenderedPaisa { get; init; }
    public PaymentStatus Status { get; init; }
    /// <summary>Header payment method: the single tender's method, Split for several, Credit for none.</summary>
    public PaymentMethod Method { get; init; }
    public IReadOnlyList<SaleTender> Tenders { get; init; } = Array.Empty<SaleTender>();
}

/// <summary>Everything the repository needs to write a sale: the request plus its computed amounts.</summary>
public sealed record SalePosting(SalePostingRequest Request, BillComputation Bill, PaymentResolution Payment, bool IsInterState,
    bool AllowNegativeStock = false);

/// <summary>Result of posting a sale.</summary>
public sealed record PostedSale(long SaleId, string InvoiceNumber, long NetPayablePaisa, long AmountPaidPaisa,
    long BalanceDuePaisa, long ChangePaisa);

/// <summary>Raised by billing services when input fails validation; <see cref="Errors"/> are user-facing.</summary>
public sealed class BillingValidationException : Exception
{
    public BillingValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Validation failed." : string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public BillingValidationException(string error) : this(new[] { error }) { }

    public IReadOnlyList<string> Errors { get; }
}
