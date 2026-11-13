using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>A money account of the shop (cash in hand, bank, UPI wallet, card POS) with its opening balance.</summary>
public sealed class FinanceAccount
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public FinanceAccountType AccountType { get; set; } = FinanceAccountType.Bank;
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? Ifsc { get; set; }
    public string? HolderName { get; set; }
    public string? UpiId { get; set; }
    /// <summary>Opening balance in paisa on <see cref="OpeningDate"/> (may be negative for an overdrawn bank account).</summary>
    public long OpeningBalancePaisa { get; set; }
    public DateTime OpeningDate { get; set; } = DateTime.Today;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>Current balance (derived in SQL from source rows on read; never stored).</summary>
    public long BalancePaisa { get; set; }
    public DateTime? LastTransactionDate { get; set; }

    public string TypeLabel => AccountType.ToLabel();
    public decimal Balance => BalancePaisa / 100m;

    public string DisplayName => AccountType switch
    {
        FinanceAccountType.Bank when !string.IsNullOrWhiteSpace(AccountNumber) => $"{Name} (…{Last4(AccountNumber)})",
        FinanceAccountType.UpiWallet when !string.IsNullOrWhiteSpace(UpiId) => $"{Name} ({UpiId})",
        _ => Name
    };

    public override string ToString() => DisplayName;

    private static string Last4(string number)
    {
        var digits = number.Trim();
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}

/// <summary>Which account a legacy payment method books into when a Payments / Expenses row has no account of its own.</summary>
public sealed record PaymentMethodAccount(PaymentMethod Method, long AccountId)
{
    public string MethodLabel => Method switch
    {
        PaymentMethod.UPI => "UPI",
        PaymentMethod.BankTransfer => "Bank transfer / cheque",
        PaymentMethod.Split => "Split (legacy)",
        _ => Method.ToString()
    };
}

/// <summary>Transfer between accounts, or a manual add / reduce money entry on one account.</summary>
public sealed class FinanceMovement
{
    public long Id { get; set; }
    public FinanceMovementKind Kind { get; set; }
    public DateTime MovementDate { get; set; } = DateTime.Today;
    /// <summary>Money leaves this account (null for <see cref="FinanceMovementKind.AddMoney"/>).</summary>
    public long? FromAccountId { get; set; }
    /// <summary>Money enters this account (null for <see cref="FinanceMovementKind.ReduceMoney"/>).</summary>
    public long? ToAccountId { get; set; }
    public string? FromAccountName { get; set; }
    public string? ToAccountName { get; set; }
    public long AmountPaisa { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public bool IsCancelled { get; set; }
    public long? CreatedByUserId { get; set; }

    public string KindLabel => Kind.ToLabel();
}

/// <summary>One line of an account statement.</summary>
public sealed record AccountStatementLine
{
    public DateTime Date { get; init; }
    public AccountEntrySource Source { get; init; }
    /// <summary>Document number (invoice, receipt, expense, transfer reference).</summary>
    public string? Reference { get; init; }
    public string? PartyName { get; init; }
    public string? Description { get; init; }
    /// <summary>Money in (paisa, ≥ 0).</summary>
    public long InPaisa { get; init; }
    /// <summary>Money out (paisa, ≥ 0).</summary>
    public long OutPaisa { get; init; }
    /// <summary>Running balance after this line (filled by <see cref="FinanceRules.WithRunningBalance"/>).</summary>
    public long BalancePaisa { get; init; }
    /// <summary>Stable tie-break within a day (source rows in insertion order).</summary>
    public long SortKey { get; init; }

    public string SourceLabel => Source.ToLabel();
}

/// <summary>Statement of one account for a date range: opening, lines with running balance, closing.</summary>
public sealed record AccountStatement
{
    public required FinanceAccount Account { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public long OpeningBalancePaisa { get; init; }
    public IReadOnlyList<AccountStatementLine> Lines { get; init; } = Array.Empty<AccountStatementLine>();
    public long TotalInPaisa => Lines.Sum(l => l.InPaisa);
    public long TotalOutPaisa => Lines.Sum(l => l.OutPaisa);
    public long ClosingBalancePaisa => OpeningBalancePaisa + TotalInPaisa - TotalOutPaisa;
}
