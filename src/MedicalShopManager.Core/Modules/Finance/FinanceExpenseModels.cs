namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>Expense head (Rent, Salary, Electricity, …). Ids 1–16 are seeded from the legacy ExpenseCategory enum (value + 1).</summary>
public sealed class FinanceExpenseCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public override string ToString() => Name;
}

/// <summary>An expense with optional GST input (purchase-side, GST added on top unless the amount includes it).</summary>
public sealed class FinanceExpense
{
    public long Id { get; set; }
    /// <summary>EXP/2627/00001 (DocumentType.Expense). Null for expenses recorded before migration 16.</summary>
    public string? ExpenseNumber { get; set; }
    public DateTime ExpenseDate { get; set; } = DateTime.Today;
    public long CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PayeeName { get; set; }
    public string? PayeeGstin { get; set; }
    /// <summary>Payee's bill / invoice number (needed to claim GST input).</summary>
    public string? InvoiceNumber { get; set; }
    public decimal GstRatePercent { get; set; }
    public bool IsInterState { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    /// <summary>Total paid (taxable + GST) — the amount that leaves the account.</summary>
    public long AmountPaisa { get; set; }
    public long? AccountId { get; set; }
    public string? AccountName { get; set; }
    public FinancePaymentMode Mode { get; set; } = FinancePaymentMode.Cash;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public bool IsCancelled { get; set; }
    public string? CancelReason { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public long TaxPaisa => CgstPaisa + SgstPaisa + IgstPaisa;
    public bool HasGst => TaxPaisa > 0;
    public string NumberDisplay => ExpenseNumber ?? $"#{Id}";
    public string ModeLabel => Mode.ToLabel();
}

/// <summary>What the expense form sends to the service.</summary>
public sealed record ExpenseRequest
{
    public long Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public long CategoryId { get; init; }
    public string? Description { get; init; }
    public string? PayeeName { get; init; }
    public string? PayeeGstin { get; init; }
    public string? InvoiceNumber { get; init; }
    /// <summary>Amount typed by the user (paisa): the total when <see cref="AmountIncludesGst"/>, else the taxable value.</summary>
    public long AmountPaisa { get; init; }
    public decimal GstRatePercent { get; init; }
    public bool AmountIncludesGst { get; init; } = true;
    public long AccountId { get; init; }
    public FinancePaymentMode Mode { get; init; } = FinancePaymentMode.Cash;
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public long? UserId { get; init; }
}

/// <summary>GST split of an expense, computed with PricingCalculator.</summary>
public sealed record ExpenseAmounts(long TaxablePaisa, long CgstPaisa, long SgstPaisa, long IgstPaisa, long TotalPaisa)
{
    public long TaxPaisa => CgstPaisa + SgstPaisa + IgstPaisa;
}

public sealed record ExpenseFilter
{
    public DateTime From { get; init; } = DateTime.Today.AddDays(-30);
    public DateTime To { get; init; } = DateTime.Today;
    public long? CategoryId { get; init; }
    public long? AccountId { get; init; }
    /// <summary>True = only expenses with GST (input tax), false = only without, null = all.</summary>
    public bool? WithGst { get; init; }
    public string? SearchText { get; init; }
    public bool IncludeCancelled { get; init; }
}

/// <summary>Totals of an expense list (cancelled excluded).</summary>
public sealed record ExpenseTotals(int Count, long TaxablePaisa, long TaxPaisa, long AmountPaisa);

/// <summary>Expense total of one category in a period.</summary>
public sealed record ExpenseCategoryTotal(string CategoryName, int Count, long AmountPaisa);
