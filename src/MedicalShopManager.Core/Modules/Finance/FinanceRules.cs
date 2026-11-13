using System.Globalization;
using System.Text.RegularExpressions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>Validation failure of a Finance operation; <see cref="Errors"/> are user-facing sentences.</summary>
public sealed class FinanceValidationException : Exception
{
    public FinanceValidationException(IReadOnlyList<string> errors)
        : base(errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "The entry is not valid.") => Errors = errors;

    public FinanceValidationException(string error) : this(new[] { error }) { }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Pure Finance rules: oldest-first allocation, ageing buckets, running balances, cheque state machine, expense GST
/// (through <see cref="PricingCalculator"/>) and input validation. No I/O; fully unit tested.
/// </summary>
public static partial class FinanceRules
{
    public const int MaxNameLength = 80;
    public const int MaxReferenceLength = 60;
    public const int MaxNotesLength = 250;

    // ───────────────────────── Allocation ─────────────────────────

    /// <summary>
    /// Settles the oldest dues first (MASTER-SPEC §3.5 "auto-allocation goes oldest first"): the opening balance, then
    /// documents by date and id. Returns one allocation per document touched; the remainder (if any) stays on account.
    /// </summary>
    public static IReadOnlyList<VoucherAllocation> AllocateOldestFirst(long amountPaisa, IEnumerable<DueDocument> dues)
    {
        ArgumentNullException.ThrowIfNull(dues);
        if (amountPaisa <= 0) return Array.Empty<VoucherAllocation>();
        var result = new List<VoucherAllocation>();
        var left = amountPaisa;
        foreach (var due in OldestFirst(dues))
        {
            if (left == 0) break;
            if (due.BalancePaisa <= 0) continue;
            var share = Math.Min(left, due.BalancePaisa);
            result.Add(new VoucherAllocation(due.Target, due.DocumentId, due.Number, share));
            left -= share;
        }
        return result;
    }

    /// <summary>Opening balance first, then by document date, then id (stable).</summary>
    public static IEnumerable<DueDocument> OldestFirst(IEnumerable<DueDocument> dues) =>
        dues.OrderBy(d => d.Target == AllocationTarget.OpeningBalance ? 0 : 1)
            .ThenBy(d => d.Date.Date)
            .ThenBy(d => d.DocumentId);

    public static long TotalDue(IEnumerable<DueDocument> dues) => dues.Sum(d => Math.Max(0, d.BalancePaisa));

    /// <summary>Checks explicit allocations against the open documents and the voucher amount.</summary>
    public static IReadOnlyList<string> ValidateAllocations(long amountPaisa, IReadOnlyList<VoucherAllocation> allocations,
        IReadOnlyList<DueDocument> dues)
    {
        var errors = new List<string>();
        var seen = new HashSet<(AllocationTarget, long)>();
        foreach (var allocation in allocations)
        {
            if (allocation.AmountPaisa <= 0)
            {
                errors.Add($"The amount paid against {allocation.Number} must be more than zero.");
                continue;
            }
            if (!seen.Add((allocation.Target, allocation.DocumentId)))
            {
                errors.Add($"{allocation.Number} is selected twice.");
                continue;
            }
            var due = dues.FirstOrDefault(d => d.Target == allocation.Target && d.DocumentId == allocation.DocumentId);
            if (due is null)
                errors.Add($"{allocation.Number} has no balance due any more (it may have been paid or cancelled). Reload the dues.");
            else if (allocation.AmountPaisa > due.BalancePaisa)
                errors.Add($"Paying {Rupees(allocation.AmountPaisa)} against {allocation.Number} is more than its balance due of {Rupees(due.BalancePaisa)}.");
        }
        var total = allocations.Sum(a => Math.Max(0, a.AmountPaisa));
        if (total > amountPaisa)
            errors.Add($"The bills selected add up to {Rupees(total)}, more than the amount of {Rupees(amountPaisa)}.");
        return errors;
    }

    /// <summary>Paid when nothing is owed, Partial when part was paid, else Unpaid.</summary>
    public static PaymentStatus PaymentStatusFor(long totalPaisa, long paidPaisa)
    {
        if (paidPaisa >= totalPaisa) return PaymentStatus.Paid;
        return paidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid;
    }

    // ───────────────────────── Ageing ─────────────────────────

    /// <summary>Bucket index for an age in days: 0 = 0–30, 1 = 31–60, 2 = 61–90, 3 = over 90.</summary>
    public static int AgeBucket(int ageDays) => ageDays switch
    {
        <= 30 => 0,
        <= 60 => 1,
        <= 90 => 2,
        _ => 3
    };

    /// <summary>Splits the open balance of each document into age buckets by its date relative to <paramref name="asOf"/>.</summary>
    public static AgeingBuckets Ageing(IEnumerable<DueDocument> dues, DateTime asOf)
    {
        var buckets = new long[4];
        foreach (var due in dues)
        {
            if (due.BalancePaisa <= 0) continue;
            buckets[AgeBucket(due.AgeDays(asOf))] += due.BalancePaisa;
        }
        return new AgeingBuckets
        {
            Days0To30Paisa = buckets[0], Days31To60Paisa = buckets[1], Days61To90Paisa = buckets[2], Over90Paisa = buckets[3]
        };
    }

    public static PartyBalanceTotals Totals(IReadOnlyCollection<PartyBalanceRow> rows) =>
        new(rows.Aggregate(new AgeingBuckets(), (sum, r) => sum + r.Ageing), rows.Sum(r => r.CreditsPaisa), rows.Count);

    // ───────────────────────── Running balances ─────────────────────────

    /// <summary>Orders statement lines by date and source order and fills the running balance from <paramref name="openingPaisa"/>.</summary>
    public static IReadOnlyList<AccountStatementLine> WithRunningBalance(long openingPaisa, IEnumerable<AccountStatementLine> lines)
    {
        var balance = openingPaisa;
        var result = new List<AccountStatementLine>();
        foreach (var line in lines.OrderBy(l => l.Date.Date).ThenBy(l => l.SortKey))
        {
            balance += line.InPaisa - line.OutPaisa;
            result.Add(line with { BalancePaisa = balance });
        }
        return result;
    }

    /// <summary>Party ledger running balance (debit raises, credit lowers what the party owes).</summary>
    public static IReadOnlyList<PartyLedgerLine> WithRunningBalance(long openingPaisa, IEnumerable<PartyLedgerLine> lines)
    {
        var balance = openingPaisa;
        var result = new List<PartyLedgerLine>();
        foreach (var line in lines.OrderBy(l => l.Date.Date).ThenBy(l => l.SortKey))
        {
            balance += line.DebitPaisa - line.CreditPaisa;
            result.Add(line with { BalancePaisa = balance });
        }
        return result;
    }

    /// <summary>"₹1,250.00 Dr" (receivable) / "₹300.00 Cr" (payable) / "₹0.00".</summary>
    public static string DrCr(long signedPaisa) => signedPaisa switch
    {
        > 0 => $"{Rupees(signedPaisa)} Dr",
        < 0 => $"{Rupees(-signedPaisa)} Cr",
        _ => Rupees(0)
    };

    // ───────────────────────── Cheques ─────────────────────────

    /// <summary>
    /// Allowed cheque moves: Pending → Deposited / Cleared / Bounced / Cancelled; Deposited → Cleared / Bounced.
    /// Cleared, Bounced and Cancelled are final (a cheque returned after clearing is recorded as a new entry).
    /// </summary>
    public static bool CanTransition(ChequeStatus from, ChequeStatus to) => (from, to) switch
    {
        (ChequeStatus.Pending, ChequeStatus.Deposited) => true,
        (ChequeStatus.Pending, ChequeStatus.Cleared) => true,
        (ChequeStatus.Pending, ChequeStatus.Bounced) => true,
        (ChequeStatus.Pending, ChequeStatus.Cancelled) => true,
        (ChequeStatus.Deposited, ChequeStatus.Cleared) => true,
        (ChequeStatus.Deposited, ChequeStatus.Bounced) => true,
        _ => false
    };

    public static IReadOnlyList<string> ValidateChequeChange(FinanceCheque cheque, ChequeStatusChange change)
    {
        var errors = new List<string>();
        if (!CanTransition(cheque.Status, change.NewStatus))
        {
            errors.Add($"A {cheque.Status.ToLabel().ToLowerInvariant()} cheque cannot be marked {change.NewStatus.ToLabel().ToLowerInvariant()}.");
            return errors;
        }
        if (change.Date.Date < cheque.ChequeDate.Date.AddDays(-180))
            errors.Add("The date is too far before the cheque date.");
        if (change.NewStatus is ChequeStatus.Deposited or ChequeStatus.Cleared && change.AccountId is null && cheque.AccountId is null)
            errors.Add(cheque.Direction == ChequeDirection.Received
                ? "Choose the bank account the cheque was deposited into."
                : "Choose the bank account the cheque is drawn on.");
        if (change.NewStatus is ChequeStatus.Cleared && change.Date.Date < cheque.ChequeDate.Date)
            errors.Add($"A cheque cannot clear before its cheque date ({cheque.ChequeDate:dd-MM-yyyy}).");
        if (change.NewStatus is ChequeStatus.Bounced or ChequeStatus.Cancelled && string.IsNullOrWhiteSpace(change.Note))
            errors.Add(change.NewStatus == ChequeStatus.Bounced
                ? "Enter the bounce reason (e.g. insufficient funds, signature mismatch)."
                : "Enter why the cheque is cancelled.");
        if (change.Note?.Length > MaxNotesLength) errors.Add($"The note can be at most {MaxNotesLength} characters.");
        return errors;
    }

    // ───────────────────────── Expenses ─────────────────────────

    /// <summary>
    /// Expense GST (purchase side, MASTER-SPEC §3.6): when the typed amount includes GST the taxable value is extracted
    /// (<see cref="PricingCalculator.ExtractTaxable"/>), otherwise GST is added on top (<see cref="PricingCalculator.TaxOnTaxable"/>).
    /// The tax splits into CGST + SGST, or IGST for an inter-state payee.
    /// </summary>
    public static ExpenseAmounts ComputeExpense(long amountPaisa, decimal gstPercent, bool amountIncludesGst, bool isInterState)
    {
        if (gstPercent <= 0) return new ExpenseAmounts(amountPaisa, 0, 0, 0, amountPaisa);
        long taxable, tax;
        if (amountIncludesGst)
        {
            taxable = PricingCalculator.ExtractTaxable(amountPaisa, gstPercent);
            tax = amountPaisa - taxable;
        }
        else
        {
            taxable = amountPaisa;
            tax = PricingCalculator.TaxOnTaxable(taxable, gstPercent);
        }
        var split = PricingCalculator.SplitTax(tax, isInterState);
        return new ExpenseAmounts(taxable, split.CgstPaisa, split.SgstPaisa, split.IgstPaisa, taxable + tax);
    }

    /// <summary>GST rates allowed on an expense bill.</summary>
    public static IReadOnlyList<decimal> ExpenseGstRates { get; } = new[] { 0m, 5m, 12m, 18m, 28m };

    public static IReadOnlyList<string> ValidateExpense(ExpenseRequest request, DateTime today)
    {
        var errors = new List<string>();
        if (request.CategoryId <= 0) errors.Add("Choose an expense category.");
        if (request.AmountPaisa <= 0) errors.Add("Enter the expense amount (more than zero).");
        if (request.AmountPaisa > 100_000_000_00L) errors.Add("The amount is too large. Check the figure.");
        if (request.ExpenseDate.Date > today.Date) errors.Add("The expense date cannot be in the future.");
        if (request.AccountId <= 0) errors.Add("Choose the account the expense was paid from.");
        if (!ExpenseGstRates.Contains(request.GstRatePercent)) errors.Add("GST rate must be 0, 5, 12, 18 or 28 %.");
        if (request.Mode == FinancePaymentMode.Cheque) errors.Add("Record a cheque expense as a bank transfer from the bank account.");
        if (!string.IsNullOrWhiteSpace(request.PayeeGstin) && !IsValidGstin(request.PayeeGstin))
            errors.Add("Payee GSTIN must be 15 characters, e.g. 27AAACM1234A1Z5.");
        if (request.GstRatePercent > 0 && string.IsNullOrWhiteSpace(request.PayeeGstin))
            errors.Add("Enter the payee's GSTIN to record GST on an expense (needed to claim input tax).");
        if (request.GstRatePercent > 0 && string.IsNullOrWhiteSpace(request.InvoiceNumber))
            errors.Add("Enter the payee's bill number to record GST on an expense.");
        AddLengthErrors(errors, request.PayeeName, MaxNameLength, "Payee name");
        AddLengthErrors(errors, request.Description, MaxNotesLength, "Description");
        AddLengthErrors(errors, request.InvoiceNumber, 30, "Bill number");
        AddLengthErrors(errors, request.Reference, MaxReferenceLength, "Reference");
        AddLengthErrors(errors, request.Notes, MaxNotesLength, "Notes");
        return errors;
    }

    // ───────────────────────── Vouchers, accounts, movements ─────────────────────────

    public static IReadOnlyList<string> ValidateVoucher(VoucherRequest request, DateTime today)
    {
        var errors = new List<string>();
        if (request.PartyId <= 0)
            errors.Add(request.Kind == FinanceVoucherKind.Receipt ? "Choose the customer who paid." : "Choose the supplier you paid.");
        var expectedParty = request.Kind == FinanceVoucherKind.Receipt ? PartyType.Customer : PartyType.Supplier;
        if (request.PartyType != expectedParty)
            errors.Add(request.Kind == FinanceVoucherKind.Receipt
                ? "Receipts are taken from customers."
                : "Payments are made to suppliers.");
        if (request.AmountPaisa <= 0) errors.Add("Enter the amount (more than zero).");
        if (request.AmountPaisa > 100_000_000_00L) errors.Add("The amount is too large. Check the figure.");
        if (request.Date.Date > today.Date) errors.Add("The payment date cannot be in the future.");
        if (request.Mode == FinancePaymentMode.Cheque)
        {
            if (request.Cheque is null || string.IsNullOrWhiteSpace(request.Cheque.ChequeNumber))
                errors.Add("Enter the cheque number.");
            else if (!ChequeNumberPattern().IsMatch(request.Cheque.ChequeNumber.Trim()))
                errors.Add("Cheque number must be 6 digits (as printed on the cheque leaf).");
            if (request.Cheque is { } cheque && Math.Abs((cheque.ChequeDate.Date - request.Date.Date).TotalDays) > 180)
                errors.Add("The cheque date is more than 6 months from the payment date (a cheque is valid for 3 months).");
            AddLengthErrors(errors, request.Cheque?.BankName, MaxNameLength, "Bank name");
        }
        else if (request.AccountId is null or <= 0)
            errors.Add(request.Kind == FinanceVoucherKind.Receipt
                ? "Choose the account the money was received into."
                : "Choose the account the money was paid from.");
        AddLengthErrors(errors, request.Reference, MaxReferenceLength, "Reference");
        AddLengthErrors(errors, request.Notes, MaxNotesLength, "Notes");
        return errors;
    }

    public static IReadOnlyList<string> ValidateAccount(FinanceAccount account)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(account.Name)) errors.Add("Enter the account name.");
        AddLengthErrors(errors, account.Name, MaxNameLength, "Account name");
        if (!Enum.IsDefined(account.AccountType)) errors.Add("Choose the account type.");
        if (account.AccountType == FinanceAccountType.Bank)
        {
            if (!string.IsNullOrWhiteSpace(account.AccountNumber) && !BankAccountPattern().IsMatch(account.AccountNumber.Trim()))
                errors.Add("Bank account number must be 9 to 18 digits.");
            if (!string.IsNullOrWhiteSpace(account.Ifsc) && !IfscPattern().IsMatch(account.Ifsc.Trim().ToUpperInvariant()))
                errors.Add("IFSC must be 11 characters: 4 letters, 0, then 6 letters or digits (e.g. SBIN0001234).");
        }
        if (account.AccountType == FinanceAccountType.UpiWallet && !string.IsNullOrWhiteSpace(account.UpiId)
            && !UpiPattern().IsMatch(account.UpiId.Trim()))
            errors.Add("UPI ID must look like name@bank.");
        if (Math.Abs(account.OpeningBalancePaisa) > 100_000_000_00L) errors.Add("The opening balance is too large. Check the figure.");
        if (account.AccountType == FinanceAccountType.Cash && account.OpeningBalancePaisa < 0)
            errors.Add("Cash in hand cannot have a negative opening balance.");
        AddLengthErrors(errors, account.Notes, MaxNotesLength, "Notes");
        return errors;
    }

    public static IReadOnlyList<string> ValidateMovement(FinanceMovement movement, DateTime today)
    {
        var errors = new List<string>();
        if (movement.AmountPaisa <= 0) errors.Add("Enter the amount (more than zero).");
        if (movement.MovementDate.Date > today.Date) errors.Add("The date cannot be in the future.");
        switch (movement.Kind)
        {
            case FinanceMovementKind.Transfer:
                if (movement.FromAccountId is null || movement.ToAccountId is null) errors.Add("Choose both the from and to accounts.");
                else if (movement.FromAccountId == movement.ToAccountId) errors.Add("Choose two different accounts to transfer between.");
                break;
            case FinanceMovementKind.AddMoney:
                if (movement.ToAccountId is null) errors.Add("Choose the account to add money to.");
                break;
            case FinanceMovementKind.ReduceMoney:
                if (movement.FromAccountId is null) errors.Add("Choose the account to take money from.");
                break;
            default:
                errors.Add("Choose transfer, add money or reduce money.");
                break;
        }
        if (movement.Kind != FinanceMovementKind.Transfer && string.IsNullOrWhiteSpace(movement.Notes))
            errors.Add("Enter a note explaining the adjustment (e.g. owner capital, drawings, cash shortage).");
        AddLengthErrors(errors, movement.Reference, MaxReferenceLength, "Reference");
        AddLengthErrors(errors, movement.Notes, MaxNotesLength, "Notes");
        return errors;
    }

    /// <summary>
    /// Money cannot be taken out of cash in hand that is not there (the till cannot go negative); a bank account may be
    /// overdrawn (OD / CC limit), so only cash is checked. <paramref name="account"/> carries its current derived balance.
    /// </summary>
    public static IReadOnlyList<string> ValidateCashAvailable(FinanceAccount account, long amountPaisa)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AccountType != FinanceAccountType.Cash || amountPaisa <= account.BalancePaisa) return Array.Empty<string>();
        return new[] { $"{account.Name} has only {Rupees(Math.Max(0, account.BalancePaisa))}. You cannot pay out {Rupees(amountPaisa)} from it." };
    }

    /// <summary>A cancel / bounce reason: required, at most <see cref="MaxNotesLength"/> characters.</summary>
    public static IReadOnlyList<string> ValidateReason(string? reason, string what)
    {
        if (string.IsNullOrWhiteSpace(reason)) return new[] { $"Enter why the {what} is cancelled." };
        return reason.Trim().Length > MaxNotesLength
            ? new[] { $"The reason can be at most {MaxNotesLength} characters." }
            : Array.Empty<string>();
    }

    /// <summary>Expense category name: required, at most 40 characters.</summary>
    public static IReadOnlyList<string> ValidateCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new[] { "Enter the category name." };
        return name.Trim().Length > 40 ? new[] { "Category name can be at most 40 characters." } : Array.Empty<string>();
    }

    // ───────────────────────── Helpers ─────────────────────────

    public static bool IsValidGstin(string gstin) => GstinPattern().IsMatch(gstin.Trim().ToUpperInvariant());

    /// <summary>Rupees typed on screen to paisa (rounded once, half away from zero).</summary>
    public static long ToPaisa(decimal rupees) => PricingCalculator.RoundPaisa(rupees * 100m);

    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Rupees(long paisa) => "₹" + (paisa / 100m).ToString("N2", India);

    private static void AddLengthErrors(List<string> errors, string? value, int max, string label)
    {
        if (value is not null && value.Trim().Length > max) errors.Add($"{label} can be at most {max} characters.");
    }

    [GeneratedRegex(@"^\d{2}[A-Z]{5}\d{4}[A-Z][A-Z0-9]Z[A-Z0-9]$")]
    private static partial Regex GstinPattern();

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex ChequeNumberPattern();

    [GeneratedRegex(@"^\d{9,18}$")]
    private static partial Regex BankAccountPattern();

    [GeneratedRegex(@"^[A-Z]{4}0[A-Z0-9]{6}$")]
    private static partial Regex IfscPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{2,256}@[A-Za-z]{2,64}$")]
    private static partial Regex UpiPattern();
}
