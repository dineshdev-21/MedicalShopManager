using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>A posted sale as the return screen needs it: header, payment position and what can still come back.</summary>
public sealed class ReturnableSale
{
    public long SaleId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public bool IsCancelled { get; set; }
    public bool IsCounterSale { get; set; } = true;
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerGstin { get; set; }
    public bool IsInterState { get; set; }
    public long GrandTotalPaisa { get; set; }
    /// <summary>Non-refundable custom charge on the bill (delivery fee, doctor fee).</summary>
    public long CustomChargePaisa { get; set; }
    public long AmountPaidPaisa { get; set; }
    public long BalanceDuePaisa { get; set; }
    /// <summary>Refunds already paid out on earlier (active) returns of this bill.</summary>
    public long PreviouslyRefundedPaisa { get; set; }
    /// <summary>Value of earlier (active) returns of this bill.</summary>
    public long PreviouslyReturnedPaisa { get; set; }
    public List<ReturnableSaleLine> Lines { get; set; } = new();

    public bool HasCustomer => !IsCounterSale && CustomerId is > 0;
    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerName) ? "Cash / Counter Sale" : CustomerName!;
    public bool IsFullyReturned => Lines.Count > 0 && Lines.All(l => l.ReturnableUnits == 0);
}

/// <summary>A sold line with the units still returnable (sold − returned on active returns).</summary>
public sealed class ReturnableSaleLine
{
    public long SaleItemId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? HsnCode { get; set; }
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int UnitsPerPack { get; set; } = 1;
    /// <summary>Units that left stock on the bill (paid + free).</summary>
    public long SoldUnits { get; set; }
    public long FreeUnits { get; set; }
    public long ReturnedUnits { get; set; }
    public long RatePerPackPaisa { get; set; }
    public long MrpPerPackPaisa { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal GstPercent { get; set; }
    /// <summary>What the customer paid for the line (GST included, after line and bill discounts).</summary>
    public long LineTotalPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    /// <summary>Amount and taxable value already credited for this line on active returns.</summary>
    public long ReturnedTotalPaisa { get; set; }
    public long ReturnedTaxablePaisa { get; set; }

    public long ReturnableUnits => Math.Max(0, SoldUnits - ReturnedUnits);
    public string SoldDisplay => PackQuantity.Format(SoldUnits, Math.Max(UnitsPerPack, 1));
    public string ReturnedDisplay => PackQuantity.Format(ReturnedUnits, Math.Max(UnitsPerPack, 1));
    public string ReturnableDisplay => PackQuantity.Format(ReturnableUnits, Math.Max(UnitsPerPack, 1));
    public string ExpiryLabel => ExpiryDate == default ? "" : ExpiryDate.ToString("MM/yy");
}

/// <summary>One line the user wants to return.</summary>
public sealed record SaleReturnLineRequest
{
    /// <summary>The bill line (return against a bill). Null for a return without a bill.</summary>
    public long? SaleItemId { get; init; }
    public long MedicineId { get; init; }
    public long BatchId { get; init; }
    public long Units { get; init; }
    /// <summary>Restock (default) or Damaged; an expired batch always becomes <see cref="SaleReturnDisposition.ExpiredStock"/>.</summary>
    public SaleReturnDisposition Disposition { get; init; } = SaleReturnDisposition.Restock;
    /// <summary>Return without a bill only: refund rate per pack (GST-inclusive, usually MRP) and discount %.</summary>
    public long RatePerPackPaisa { get; init; }
    public decimal DiscountPercent { get; init; }
}

/// <summary>A sale return as entered on the return screen.</summary>
public sealed record SaleReturnRequest
{
    /// <summary>Original bill; null = return without a bill (walk-in).</summary>
    public long? SaleId { get; init; }
    public DateTime ReturnDate { get; init; } = DateTime.Today;
    /// <summary>Customer for a return without a bill (required for credit). Ignored when <see cref="SaleId"/> is set.</summary>
    public long? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public SaleRefundMode RefundMode { get; init; } = SaleRefundMode.Cash;
    public PaymentMethod RefundMethod { get; init; } = PaymentMethod.Cash;
    public string? RefundReference { get; init; }
    public string? Reason { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<SaleReturnLineRequest> Lines { get; init; } = Array.Empty<SaleReturnLineRequest>();
    public long? UserId { get; init; }
}

/// <summary>Batch details for a return without a bill (authoritative values read from MedicineBatches / Medicines).</summary>
public sealed record SaleReturnBatchInfo
{
    public long BatchId { get; init; }
    public long MedicineId { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? HsnCode { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public long MrpPaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public decimal GstPercent { get; init; }
    public long QuantityRemaining { get; init; }

    /// <summary>Default refund rate per pack: the batch sale rate, else MRP.</summary>
    public long DefaultRatePaisa => SaleRatePaisa > 0 ? SaleRatePaisa : MrpPaisa;
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
    public string Display => $"{BatchNumber}  ·  Exp {ExpiryLabel}  ·  MRP {MrpPaisa / 100m:N2}  ·  1 x {UnitsPerPack}";
}

/// <summary>An item found by the return screen's product search (return without a bill).</summary>
public sealed record SaleReturnItemOption(long MedicineId, string Name, string? Manufacturer, string? PackLabel, int UnitsPerPack)
{
    public string Display => string.IsNullOrWhiteSpace(Manufacturer) ? Name : $"{Name}  ·  {Manufacturer}";
}

/// <summary>A customer found by the return screen (credit to customer on a return without a bill).</summary>
public sealed record SaleReturnCustomerOption(long Id, string Name, string? Phone)
{
    public string Display => string.IsNullOrWhiteSpace(Phone) ? Name : $"{Name}  ·  {Phone}";
}

/// <summary>A sale return / credit note: computed before posting and read back afterwards.</summary>
public sealed class SaleReturnDocument
{
    public long Id { get; set; }
    public string ReturnNumber { get; set; } = string.Empty;
    public string CreditNoteNumber { get; set; } = string.Empty;
    public DateTime ReturnDate { get; set; } = DateTime.Today;
    public SaleReturnStatus Status { get; set; } = SaleReturnStatus.Active;

    public long? SaleId { get; set; }
    public string? SaleInvoiceNumber { get; set; }
    public DateTime? SaleInvoiceDate { get; set; }

    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerGstin { get; set; }
    public bool IsInterState { get; set; }

    public SaleRefundMode RefundMode { get; set; } = SaleRefundMode.Cash;
    public PaymentMethod RefundMethod { get; set; } = PaymentMethod.Cash;
    public string? RefundReference { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }

    public long GrossPaisa { get; set; }
    public long DiscountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long TaxPaisa { get; set; }
    public long RoundOffPaisa { get; set; }
    /// <summary>Credit note value (rounded).</summary>
    public long NetAmountPaisa { get; set; }
    /// <summary>Refunds paid out against this credit note so far (derived from SaleReturnRefunds).</summary>
    public long RefundedPaisa { get; set; }

    public long? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public List<SaleReturnDocumentLine> Lines { get; set; } = new();
    public List<SaleReturnRefund> Refunds { get; set; } = new();

    public bool IsCancelled => Status == SaleReturnStatus.Cancelled;
    public bool IsWalkIn => SaleId is null;
    /// <summary>Credit still open in the customer's favour.</summary>
    public long CreditBalancePaisa => IsCancelled ? 0 : Math.Max(0, NetAmountPaisa - RefundedPaisa);
    public long TotalUnits => Lines.Sum(l => l.Units);
    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerName) ? "Cash / Counter Sale" : CustomerName!;
    public string RefundModeLabel => RefundMode.ToLabel();
    public string AgainstLabel => IsWalkIn ? "Without bill" : $"{SaleInvoiceNumber} ({SaleInvoiceDate:dd-MM-yyyy})";
}

/// <summary>One returned batch line with its GST reversal.</summary>
public sealed class SaleReturnDocumentLine
{
    public long Id { get; set; }
    public long? SaleItemId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? HsnCode { get; set; }
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int UnitsPerPack { get; set; } = 1;
    public long Units { get; set; }
    /// <summary>Rate per pack before discount (bill rate, or the entered rate for a return without a bill).</summary>
    public long RatePerPackPaisa { get; set; }
    public long MrpPerPackPaisa { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal GstPercent { get; set; }
    public long GrossPaisa { get; set; }
    public long DiscountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long TaxPaisa { get; set; }
    public long LineTotalPaisa { get; set; }
    public SaleReturnDisposition Disposition { get; set; } = SaleReturnDisposition.Restock;

    public bool GoesToStock => Disposition != SaleReturnDisposition.Damaged;
    public string QuantityDisplay => PackQuantity.Format(Units, Math.Max(UnitsPerPack, 1));
    public string ExpiryLabel => ExpiryDate == default ? "" : ExpiryDate.ToString("MM/yy");
    public string DispositionLabel => Disposition.ToLabel();
}

/// <summary>A refund paid against a credit note.</summary>
public sealed record SaleReturnRefund(long Id, DateTime RefundDate, PaymentMethod Method, long AmountPaisa, string? Reference)
{
    public string MethodLabel => SalesReturnsLabels.ModeLabel(Method);
}

/// <summary>Filters of the sale returns register / credit notes list.</summary>
public sealed record SaleReturnFilter
{
    public DateTime From { get; init; } = DateTime.Today.AddDays(-30);
    public DateTime To { get; init; } = DateTime.Today;
    public long? CustomerId { get; init; }
    public SaleRefundMode? RefundMode { get; init; }
    /// <summary>Return number, credit note number, bill number, customer name or mobile (contains).</summary>
    public string? SearchText { get; init; }
    /// <summary>Only credit notes with an open balance (date range ignored, so old open credit is never hidden).</summary>
    public bool OpenCreditOnly { get; init; }
    public bool IncludeCancelled { get; init; } = true;
    public int Limit { get; init; } = 2000;
}

/// <summary>One sale return in the register.</summary>
public sealed record SaleReturnRow
{
    public long Id { get; init; }
    public string ReturnNumber { get; init; } = string.Empty;
    public string CreditNoteNumber { get; init; } = string.Empty;
    public DateTime ReturnDate { get; init; }
    public long? SaleId { get; init; }
    public string? SaleInvoiceNumber { get; init; }
    public long? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public int ItemCount { get; init; }
    public long TaxablePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public long NetAmountPaisa { get; init; }
    public long RefundedPaisa { get; init; }
    public SaleRefundMode RefundMode { get; init; }
    public SaleReturnStatus Status { get; init; } = SaleReturnStatus.Active;
    public string? Reason { get; init; }

    public bool IsCancelled => Status == SaleReturnStatus.Cancelled;
    public long CreditBalancePaisa => IsCancelled ? 0 : Math.Max(0, NetAmountPaisa - RefundedPaisa);
    public bool HasOpenCredit => CreditBalancePaisa > 0;
    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerName) ? "Cash / Counter Sale" : CustomerName!;
    public string AgainstDisplay => string.IsNullOrWhiteSpace(SaleInvoiceNumber) ? "Without bill" : SaleInvoiceNumber!;
    public string RefundModeLabel => RefundMode.ToLabel();
    public string StatusLabel => IsCancelled ? "Cancelled" : HasOpenCredit ? "Open credit" : "Settled";
}

/// <summary>Footer totals of the returns register (cancelled returns excluded).</summary>
public sealed record SaleReturnTotals
{
    public int Count { get; init; }
    public long TaxablePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public long NetAmountPaisa { get; init; }
    public long RefundedPaisa { get; init; }
    public long OpenCreditPaisa { get; init; }

    public static SaleReturnTotals From(IEnumerable<SaleReturnRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var active = rows.Where(r => !r.IsCancelled).ToList();
        return new SaleReturnTotals
        {
            Count = active.Count,
            TaxablePaisa = active.Sum(r => r.TaxablePaisa),
            TaxPaisa = active.Sum(r => r.TaxPaisa),
            NetAmountPaisa = active.Sum(r => r.NetAmountPaisa),
            RefundedPaisa = active.Sum(r => r.RefundedPaisa),
            OpenCreditPaisa = active.Sum(r => r.CreditBalancePaisa)
        };
    }
}

/// <summary>A refund of open credit from the Credit Notes list.</summary>
public sealed record CreditRefundRequest
{
    public long SaleReturnId { get; init; }
    public long AmountPaisa { get; init; }
    public PaymentMethod Method { get; init; } = PaymentMethod.Cash;
    public string? Reference { get; init; }
    public DateTime RefundDate { get; init; } = DateTime.Today;
    public long? UserId { get; init; }
}

/// <summary>Validation failure with user-facing messages.</summary>
public sealed class SaleReturnValidationException : Exception
{
    public SaleReturnValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Validation failed." : string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public SaleReturnValidationException(string error) : this(new[] { error }) { }

    public IReadOnlyList<string> Errors { get; }
}
