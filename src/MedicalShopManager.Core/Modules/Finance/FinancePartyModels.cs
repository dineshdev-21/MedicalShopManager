using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>A customer or supplier that can receive / make payments (search result of the receipt and ledger screens).</summary>
public sealed record FinanceParty(PartyType PartyType, long PartyId, string Name, string? Phone, string? Gstin, long NetDuePaisa)
{
    public string Display => string.IsNullOrWhiteSpace(Phone) ? Name : $"{Name}  ·  {Phone}";
    public override string ToString() => Display;
}

/// <summary>An open document of a party that a receipt / payment can settle (a credit sale, a purchase bill or the opening balance).</summary>
public sealed record DueDocument
{
    public AllocationTarget Target { get; init; }
    /// <summary>Sales.Id / Purchases.Id; 0 for the opening balance.</summary>
    public long DocumentId { get; init; }
    public string Number { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public DateTime? DueDate { get; init; }
    public long TotalPaisa { get; init; }
    public long PaidPaisa { get; init; }
    /// <summary>Still owed on this document (paisa, &gt; 0 for every listed document).</summary>
    public long BalancePaisa { get; init; }

    public int AgeDays(DateTime asOf) => Math.Max(0, (asOf.Date - Date.Date).Days);
    public string TargetLabel => Target switch
    {
        AllocationTarget.Sale => "Sale",
        AllocationTarget.Purchase => "Purchase",
        _ => "Opening balance"
    };
}

/// <summary>Part of a receipt / payment applied to one document.</summary>
public sealed record VoucherAllocation(AllocationTarget Target, long DocumentId, string Number, long AmountPaisa);

/// <summary>Cheque details captured with a cheque receipt / payment.</summary>
public sealed record ChequeDetails(string ChequeNumber, DateTime ChequeDate, string? BankName);

/// <summary>Everything needed to post a receipt (customer → shop) or a payment (shop → supplier).</summary>
public sealed record VoucherRequest
{
    public FinanceVoucherKind Kind { get; init; }
    public PartyType PartyType { get; init; }
    public long PartyId { get; init; }
    public DateTime Date { get; init; }
    public long AmountPaisa { get; init; }
    public FinancePaymentMode Mode { get; init; }
    /// <summary>Account the money goes into / comes from. Null only for a cheque (chosen when it is deposited / clears).</summary>
    public long? AccountId { get; init; }
    public ChequeDetails? Cheque { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    /// <summary>Explicit allocations (from the dues grid); null = settle the oldest dues first.</summary>
    public IReadOnlyList<VoucherAllocation>? Allocations { get; init; }
    public long? UserId { get; init; }
}

/// <summary>A posted receipt / payment voucher.</summary>
public sealed class FinanceVoucher
{
    public long Id { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public FinanceVoucherKind Kind { get; set; }
    public DateTime VoucherDate { get; set; }
    public PartyType PartyType { get; set; }
    public long PartyId { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public string? PartyPhone { get; set; }
    public FinancePaymentMode Mode { get; set; }
    public long? AccountId { get; set; }
    public string? AccountName { get; set; }
    public long AmountPaisa { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public long? ChequeId { get; set; }
    public string? ChequeNumber { get; set; }
    public DateTime? ChequeDate { get; set; }
    public string? ChequeBank { get; set; }
    public ChequeStatus? ChequeStatus { get; set; }
    public FinanceVoucherStatus Status { get; set; } = FinanceVoucherStatus.Active;
    public DateTime? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<VoucherAllocation> Allocations { get; } = new();

    public long AllocatedPaisa => Status == FinanceVoucherStatus.Active ? Allocations.Sum(a => a.AmountPaisa) : 0;
    /// <summary>Part of the amount not applied to any document (advance / on account).</summary>
    public long OnAccountPaisa => Status == FinanceVoucherStatus.Active ? AmountPaisa - Allocations.Sum(a => a.AmountPaisa) : 0;
    public bool IsActive => Status == FinanceVoucherStatus.Active;
    public string KindLabel => Kind == FinanceVoucherKind.Receipt ? "Payment In" : "Payment Out";
    public string ModeLabel => Mode.ToLabel();
    public string StatusLabel => Status.ToLabel();
    public string AllocationSummary => Allocations.Count == 0
        ? "On account"
        : string.Join(", ", Allocations.Select(a => a.Number));
}

public sealed record VoucherFilter
{
    public DateTime From { get; init; } = DateTime.Today.AddDays(-30);
    public DateTime To { get; init; } = DateTime.Today;
    public FinanceVoucherKind? Kind { get; init; }
    public PartyType? PartyType { get; init; }
    public long? PartyId { get; init; }
    public string? SearchText { get; init; }
    public bool IncludeReversed { get; init; } = true;
    public int Limit { get; init; } = 1000;
}

/// <summary>A cheque in the cheque register.</summary>
public sealed class FinanceCheque
{
    public long Id { get; set; }
    public ChequeDirection Direction { get; set; }
    public string ChequeNumber { get; set; } = string.Empty;
    public DateTime ChequeDate { get; set; }
    public string? BankName { get; set; }
    public long AmountPaisa { get; set; }
    public PartyType PartyType { get; set; }
    public long PartyId { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public long? VoucherId { get; set; }
    public string? VoucherNumber { get; set; }
    public ChequeStatus Status { get; set; } = ChequeStatus.Pending;
    /// <summary>Account the cheque is deposited into (received) or drawn on (issued).</summary>
    public long? AccountId { get; set; }
    public string? AccountName { get; set; }
    public DateTime? DepositDate { get; set; }
    public DateTime? ClearedDate { get; set; }
    public DateTime? BouncedDate { get; set; }
    public string? StatusNote { get; set; }
    public DateTime CreatedAt { get; set; }

    public string DirectionLabel => Direction == ChequeDirection.Received ? "Received" : "Issued";
    public string StatusLabel => Status.ToLabel();
    public bool IsOpen => Status is ChequeStatus.Pending or ChequeStatus.Deposited;
    /// <summary>Post-dated: the cheque date is still in the future.</summary>
    public bool IsPostDated(DateTime today) => ChequeDate.Date > today.Date && IsOpen;
}

public sealed record ChequeFilter
{
    public DateTime From { get; init; } = DateTime.Today.AddDays(-90);
    public DateTime To { get; init; } = DateTime.Today.AddDays(90);
    public ChequeDirection? Direction { get; init; }
    public ChequeStatus? Status { get; init; }
    /// <summary>Open cheques (pending / deposited) of any date; ignores the date range.</summary>
    public bool OpenOnly { get; init; }
    public string? SearchText { get; init; }
}

/// <summary>Request to move a cheque to a new status.</summary>
public sealed record ChequeStatusChange
{
    public long ChequeId { get; init; }
    public ChequeStatus NewStatus { get; init; }
    public DateTime Date { get; init; }
    /// <summary>Required for deposit / clear when the cheque has no account yet.</summary>
    public long? AccountId { get; init; }
    public string? Note { get; init; }
    public long? UserId { get; init; }
}

/// <summary>Outstanding split by age of the open documents (days since the document date).</summary>
public sealed record AgeingBuckets
{
    public long Days0To30Paisa { get; init; }
    public long Days31To60Paisa { get; init; }
    public long Days61To90Paisa { get; init; }
    public long Over90Paisa { get; init; }
    public long TotalPaisa => Days0To30Paisa + Days31To60Paisa + Days61To90Paisa + Over90Paisa;

    public static AgeingBuckets operator +(AgeingBuckets a, AgeingBuckets b) => new()
    {
        Days0To30Paisa = a.Days0To30Paisa + b.Days0To30Paisa,
        Days31To60Paisa = a.Days31To60Paisa + b.Days31To60Paisa,
        Days61To90Paisa = a.Days61To90Paisa + b.Days61To90Paisa,
        Over90Paisa = a.Over90Paisa + b.Over90Paisa
    };
}

/// <summary>A party with its dues: receivable from a customer or payable to a supplier, with ageing.</summary>
public sealed record PartyBalanceRow
{
    public PartyType PartyType { get; init; }
    public long PartyId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Gstin { get; init; }
    public AgeingBuckets Ageing { get; init; } = new();
    /// <summary>Open credit notes / debit notes and unallocated advances in the party's favour (paisa ≥ 0).</summary>
    public long CreditsPaisa { get; init; }
    public int OpenDocumentCount { get; init; }
    public DateTime? OldestDueDate { get; init; }

    /// <summary>Net due in the party's natural direction: customer owes the shop / shop owes the supplier.</summary>
    public long NetDuePaisa => Ageing.TotalPaisa - CreditsPaisa;
    /// <summary>Signed per ARCHITECTURE §10: positive = the shop will receive, negative = the shop will pay.</summary>
    public long SignedBalancePaisa => PartyType == PartyType.Supplier ? -NetDuePaisa : NetDuePaisa;
    public string Display => string.IsNullOrWhiteSpace(Phone) ? Name : $"{Name}  ·  {Phone}";
}

/// <summary>Totals of a receivables / payables list.</summary>
public sealed record PartyBalanceTotals(AgeingBuckets Ageing, long CreditsPaisa, int PartyCount)
{
    public long NetDuePaisa => Ageing.TotalPaisa - CreditsPaisa;
}

/// <summary>One line of a customer / supplier ledger. Debit raises what the party owes the shop; credit lowers it.</summary>
public sealed record PartyLedgerLine
{
    public DateTime Date { get; init; }
    public PartyEntrySource Source { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public long DebitPaisa { get; init; }
    public long CreditPaisa { get; init; }
    /// <summary>Running signed balance (positive = receivable, negative = payable).</summary>
    public long BalancePaisa { get; init; }
    public long SortKey { get; init; }

    public string SourceLabel => Source.ToLabel();
    public string BalanceLabel => FinanceRules.DrCr(BalancePaisa);
}

public sealed record PartyLedger
{
    public PartyType PartyType { get; init; }
    public long PartyId { get; init; }
    public string PartyName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Gstin { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public long OpeningBalancePaisa { get; init; }
    public IReadOnlyList<PartyLedgerLine> Lines { get; init; } = Array.Empty<PartyLedgerLine>();
    public long TotalDebitPaisa => Lines.Sum(l => l.DebitPaisa);
    public long TotalCreditPaisa => Lines.Sum(l => l.CreditPaisa);
    public long ClosingBalancePaisa => OpeningBalancePaisa + TotalDebitPaisa - TotalCreditPaisa;
}
