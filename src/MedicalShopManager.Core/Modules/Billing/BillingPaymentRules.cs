namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>
/// Rules of the 3-step payment wizard (spec 02 §M): step 1 decides credit vs paid and the amount paid now, step 2
/// splits that amount over tender modes, step 3 takes references and the cash handed over. The final check is
/// <see cref="BillingCalculator.ResolvePayment"/>; these helpers give each step its own messages.
/// </summary>
public static class BillingPaymentRules
{
    /// <summary>Tender modes offered in step 2, in display order.</summary>
    public static IReadOnlyList<TenderMode> Modes { get; } =
        new[] { TenderMode.Cash, TenderMode.Upi, TenderMode.Card, TenderMode.BankTransfer, TenderMode.Cheque };

    /// <summary>
    /// Step 1 errors. A counter sale must be paid in full; a paid amount must be 1 paisa … payable; anything left
    /// unpaid needs a due date on or after the invoice date.
    /// </summary>
    public static IReadOnlyList<string> ValidateStep1(long payablePaisa, bool isCounterSale, bool isPaid, long amountPaidPaisa,
        DateTime? dueDate, DateTime invoiceDate)
    {
        var errors = new List<string>();
        if (isCounterSale && (!isPaid || amountPaidPaisa != payablePaisa) && payablePaisa > 0)
            errors.Add("Cash/Counter sale must be paid in full; select a customer to give credit");
        if (isPaid)
        {
            if (amountPaidPaisa < 0) errors.Add("Payment Amount cannot be negative");
            else if (amountPaidPaisa == 0 && payablePaisa > 0) errors.Add("Please Fill Payment Amount");
            else if (amountPaidPaisa > payablePaisa) errors.Add("Payment Amount > Payable Amount");
        }
        var due = isPaid ? payablePaisa - Math.Max(amountPaidPaisa, 0) : payablePaisa;
        if (errors.Count == 0 && due > 0 && (dueDate is not { } d || d.Date < invoiceDate.Date))
            errors.Add("Please provide a valid payment due date");
        return errors;
    }

    /// <summary>Step 2 errors: the tender amounts must add up exactly to the amount being paid now.</summary>
    public static IReadOnlyList<string> ValidateStep2(long payingNowPaisa, IEnumerable<SaleTender> tenders)
    {
        var list = tenders.ToList();
        var errors = new List<string>();
        if (list.Any(t => t.AmountPaisa < 0)) errors.Add("Payment Amount cannot be negative");
        var total = list.Sum(t => Math.Max(t.AmountPaisa, 0));
        if (errors.Count == 0 && total != payingNowPaisa)
            errors.Add(total < payingNowPaisa
                ? $"Allocate ₹{(payingNowPaisa - total) / 100m:N2} more to a payment mode"
                : $"Payment modes exceed the amount paid by ₹{(total - payingNowPaisa) / 100m:N2}");
        return errors;
    }

    /// <summary>Amount still to allocate in step 2 (negative when over-allocated).</summary>
    public static long Remaining(long payingNowPaisa, IEnumerable<SaleTender> tenders) =>
        payingNowPaisa - tenders.Sum(t => Math.Max(t.AmountPaisa, 0));

    /// <summary>True when a mode needs a transaction reference in step 3 (UPI/bank UTR, cheque number).</summary>
    public static bool NeedsReference(TenderMode mode) => mode is TenderMode.Upi or TenderMode.BankTransfer or TenderMode.Cheque;

    /// <summary>
    /// Step 3 (references, cash handed over) is needed when a non-card tender is used; a card-only payment saves
    /// right after step 2 (Localwell saves a POS payment immediately).
    /// </summary>
    public static bool NeedsStep3(IEnumerable<SaleTender> tenders) =>
        tenders.Any(t => t.AmountPaisa > 0 && (NeedsReference(t.Mode) || t.Mode == TenderMode.Cash));

    /// <summary>Change to hand back: cash handed over minus the cash tender (never negative).</summary>
    public static long Change(long cashTenderedPaisa, long cashTenderPaisa) => Math.Max(cashTenderedPaisa - cashTenderPaisa, 0);

    public static string Label(TenderMode mode) => mode switch
    {
        TenderMode.Cash => "Cash",
        TenderMode.Upi => "UPI",
        TenderMode.Card => "Card / POS",
        TenderMode.BankTransfer => "Bank Transfer",
        TenderMode.Cheque => "Cheque",
        _ => mode.ToString()
    };
}
