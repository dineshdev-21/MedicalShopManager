using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Filters of the purchase list / register (date range, distributor, status, free text).</summary>
public sealed record PurchaseListFilter(
    DateTime From,
    DateTime To,
    long? SupplierId = null,
    PurchaseListStatusFilter Status = PurchaseListStatusFilter.All,
    string? Search = null);

/// <summary>One row of the purchase list (header only, plus the line count).</summary>
public sealed class PurchaseListRow
{
    public long Id { get; set; }
    public string? PurchaseNumber { get; set; }
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public long TaxablePaisa { get; set; }
    public long TaxAmountPaisa { get; set; }
    public long GrandTotalPaisa { get; set; }
    public long AmountPaidPaisa { get; set; }
    public long BalancePaisa { get; set; }
    public PurchaseStatus Status { get; set; }
    public PaymentStatus PaymentStatus { get; set; }
    public string? ImportSource { get; set; }

    public bool IsOverdue => Status == PurchaseStatus.Confirmed && BalancePaisa > 0 && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    public decimal Taxable => TaxablePaisa / 100m;
    public decimal TaxAmount => TaxAmountPaisa / 100m;
    public decimal GrandTotal => GrandTotalPaisa / 100m;
    public decimal AmountPaid => AmountPaidPaisa / 100m;
    public decimal Balance => BalancePaisa / 100m;

    /// <summary>Single status word for the list: Draft, Cancelled, Overdue, Paid, Partial or Credit (unpaid).</summary>
    public string StatusLabel => Status switch
    {
        PurchaseStatus.Draft => "Draft",
        PurchaseStatus.Cancelled => "Cancelled",
        _ when IsOverdue => "Overdue",
        _ => PaymentStatus switch
        {
            PaymentStatus.Paid => "Paid",
            PaymentStatus.Partial => "Partial",
            _ => "Credit"
        }
    };
}

/// <summary>Totals over the rows of a purchase list (confirmed bills only).</summary>
public sealed record PurchaseListSummary(int Count, long TaxablePaisa, long TaxPaisa, long GrandTotalPaisa, long PaidPaisa, long BalancePaisa)
{
    public static PurchaseListSummary From(IEnumerable<PurchaseListRow> rows)
    {
        var confirmed = rows.Where(r => r.Status == PurchaseStatus.Confirmed).ToList();
        return new PurchaseListSummary(confirmed.Count,
            confirmed.Sum(r => r.TaxablePaisa), confirmed.Sum(r => r.TaxAmountPaisa), confirmed.Sum(r => r.GrandTotalPaisa),
            confirmed.Sum(r => r.AmountPaidPaisa), confirmed.Sum(r => r.BalancePaisa));
    }
}

/// <summary>One row of the debit-note register.</summary>
public sealed class PurchaseReturnListRow
{
    public long Id { get; set; }
    public string ReturnNumber { get; set; } = string.Empty;
    public DateTime ReturnDate { get; set; }
    public PurchaseReturnKind Kind { get; set; }
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? PurchaseReference { get; set; }
    public PurchaseReturnReason Reason { get; set; }
    public int ItemCount { get; set; }
    public long TaxablePaisa { get; set; }
    public long TaxAmountPaisa { get; set; }
    public long NetAmountPaisa { get; set; }
    public long RefundReceivedPaisa { get; set; }
    public bool IsCancelled { get; set; }

    public decimal NetAmount => NetAmountPaisa / 100m;
    public decimal RefundReceived => RefundReceivedPaisa / 100m;
    public string KindLabel => Kind.ToLabel();
    public string ReasonLabel => Reason.ToLabel();
    public string StatusLabel => IsCancelled ? "Cancelled" : RefundReceivedPaisa >= NetAmountPaisa ? "Refunded" : "Adjust in ledger";
}

/// <summary>One line of a supplier statement. Credit = the shop owes more; debit = it owes less.</summary>
public sealed record SupplierStatementLine(
    DateTime Date,
    SupplierStatementEntryType Type,
    string DocumentNumber,
    string Description,
    long DebitPaisa,
    long CreditPaisa,
    long BalancePaisa)
{
    public decimal Debit => DebitPaisa / 100m;
    public decimal Credit => CreditPaisa / 100m;
    public decimal Balance => BalancePaisa / 100m;
    public string TypeLabel => Type switch
    {
        SupplierStatementEntryType.OpeningBalance => "Opening",
        SupplierStatementEntryType.Purchase => "Purchase",
        SupplierStatementEntryType.Payment => "Payment",
        SupplierStatementEntryType.DebitNote => "Debit Note",
        _ => "Refund"
    };
}

/// <summary>Ledger of one distributor for a period (positive balance = the shop owes the distributor).</summary>
public sealed record SupplierStatement(
    Distributor Supplier,
    DateTime From,
    DateTime To,
    long OpeningBalancePaisa,
    IReadOnlyList<SupplierStatementLine> Lines,
    long ClosingBalancePaisa)
{
    public long TotalCreditPaisa => Lines.Sum(l => l.CreditPaisa);
    public long TotalDebitPaisa => Lines.Sum(l => l.DebitPaisa);
}

/// <summary>A movement of a supplier ledger before running balances are applied (repository → statement builder).</summary>
public sealed record SupplierLedgerEntry(DateTime Date, SupplierStatementEntryType Type, string DocumentNumber, string Description,
    long DebitPaisa, long CreditPaisa, long SortKey = 0);

/// <summary>Result of a purchase posting (confirm / edit).</summary>
public sealed record PurchasePostingResult(long PurchaseId, string PurchaseNumber, IReadOnlyList<long> BatchIds);

/// <summary>Raised for a rule violation the user can fix (shown verbatim in the UI).</summary>
public sealed class PurchaseRuleException : InvalidOperationException
{
    public PurchaseRuleException(string message) : base(message) { }
}

/// <summary>Validation failure with every message, in screen order (spec 04 "Save validations").</summary>
public sealed class PurchaseValidationException : ArgumentException
{
    public IReadOnlyList<string> Errors { get; }

    public PurchaseValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Invalid purchase." : string.Join(Environment.NewLine, errors)) => Errors = errors;
}

/// <summary>"This Bill is Duplicate": the distributor already has a bill with this number (the user may override).</summary>
public sealed class DuplicatePurchaseInvoiceException : InvalidOperationException
{
    public PurchaseListRow Duplicate { get; }

    public DuplicatePurchaseInvoiceException(PurchaseListRow duplicate)
        : base($"This Bill is Duplicate: bill {duplicate.SupplierInvoiceNumber} of {duplicate.SupplierName} is already entered " +
               $"({duplicate.PurchaseNumber ?? "draft"}, {duplicate.InvoiceDate:dd-MM-yyyy}, ₹{duplicate.GrandTotal:N2}).") => Duplicate = duplicate;
}
