using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// The 3-step payment wizard opened by Save (Alt+S) (spec 02 §M). Step 1: UNPAID/CREDIT vs PAID/CASH, amount paid
/// now, due date (skipped for a Cash/Counter sale, which is paid in full). Step 2: split the amount over Cash / UPI /
/// Card / Bank / Cheque. Step 3: transaction references, cheque date, cash handed over and the change. F2 continues,
/// Esc goes back. All checks come from <see cref="BillingPaymentRules"/> and <see cref="BillingCalculator.ResolvePayment"/>.
/// </summary>
public sealed partial class BillingPaymentViewModel : ObservableObject
{
    /// <summary>What the wizard decided; the billing page posts the sale with it.</summary>
    public sealed record Outcome(bool IsCredit, DateTime? DueDate, IReadOnlyList<SaleTender> Tenders, long CashTenderedPaisa);

    /// <summary>Bill facts the wizard starts from.</summary>
    public sealed record Context(long PayablePaisa, string PayingCustomer, string InvoiceNumber, DateTime InvoiceDate,
        bool IsCounterSale, bool StartAsCredit, DateTime DefaultDueDate, IReadOnlyList<SaleTender> ExistingTenders);

    private Context _context = new(0, "Cash Sale", string.Empty, DateTime.Today, true, false, DateTime.Today, Array.Empty<SaleTender>());
    private bool _allocating;

    public BillingPaymentViewModel()
    {
        foreach (var mode in BillingPaymentRules.Modes)
        {
            var row = new BillingTenderRowViewModel(mode);
            row.Changed += (_, _) => OnTendersChanged();
            Tenders.Add(row);
        }
    }

    public event EventHandler<Outcome>? Completed;
    public event EventHandler? Closed;

    public ObservableCollection<BillingTenderRowViewModel> Tenders { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private bool _isPaid = true;
    [ObservableProperty] private decimal _amountPaid;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private decimal _cashTendered;
    [ObservableProperty] private BillingTenderRowViewModel? _selectedTender;
    [ObservableProperty] private bool _isSaving;

    public decimal Payable => _context.PayablePaisa / 100m;
    public string PayingCustomer => _context.PayingCustomer;
    public string InvoiceNumber => _context.InvoiceNumber;
    public DateTime InvoiceDate => _context.InvoiceDate;
    public bool IsCounterSale => _context.IsCounterSale;
    public bool CanChooseCredit => !_context.IsCounterSale;

    public long PayingNowPaisa => IsPaid ? PricingCalculator.RoundPaisa(AmountPaid * 100m) : 0;
    public decimal PayingNow => PayingNowPaisa / 100m;
    public decimal BalanceDue => Math.Max(_context.PayablePaisa - PayingNowPaisa, 0) / 100m;
    public bool ShowDueDate => BalanceDue > 0;
    public string DueDaysLabel => DueDate is { } due ? BillingCalculator.DueDaysLabel(due, DateTime.Today) : string.Empty;
    public decimal Remaining => BillingPaymentRules.Remaining(PayingNowPaisa, CurrentTenders()) / 100m;
    public long CashTenderPaisa => Tenders.Where(t => t.Mode == TenderMode.Cash).Sum(t => t.AmountPaisa);
    public bool HasCash => CashTenderPaisa > 0;
    public decimal Change => BillingPaymentRules.Change(PricingCalculator.RoundPaisa(CashTendered * 100m), CashTenderPaisa) / 100m;
    public IEnumerable<BillingTenderRowViewModel> UsedReferenceTenders => Tenders.Where(t => t.IsUsed && t.NeedsReference);
    public bool HasErrors => Errors.Count > 0;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    public string StepTitle => Step switch
    {
        1 => "Step 1/3: Add Payment",
        2 => "Step 2/3: Payment Mode",
        _ => "Step 3/3: Txn Detail"
    };

    public string ContinueLabel => Step == 3 || (Step == 1 && !IsPaid) || (Step == 2 && !BillingPaymentRules.NeedsStep3(CurrentTenders()))
        ? "Save (F2)"
        : "Continue (F2)";

    public void Start(Context context)
    {
        _context = context;
        Errors.Clear();
        IsSaving = false;
        IsPaid = context.IsCounterSale || !context.StartAsCredit || context.PayablePaisa == 0;
        var existingPaid = context.ExistingTenders.Sum(t => t.AmountPaisa);
        AmountPaid = (existingPaid > 0 && !context.IsCounterSale ? Math.Min(existingPaid, context.PayablePaisa) : context.PayablePaisa) / 100m;
        DueDate = context.DefaultDueDate;
        CashTendered = 0m;

        _allocating = true;
        foreach (var row in Tenders)
        {
            var existing = context.ExistingTenders.FirstOrDefault(t => t.Mode == row.Mode);
            row.Amount = 0m;
            row.Reference = existing?.Reference;
            row.ChequeDate = existing?.ChequeDate ?? DateTime.Today;
        }
        _allocating = false;

        Step = context.IsCounterSale ? 2 : 1;
        if (Step == 2) PrepareStep2(context.ExistingTenders);
        SelectedTender = Tenders.FirstOrDefault(t => t.IsUsed) ?? Tenders[0];
        RaiseAll();
    }

    /// <summary>Errors from the save itself (stock changed, validation) are shown on the current step.</summary>
    public void ShowErrors(IEnumerable<string> errors)
    {
        Errors.Clear();
        foreach (var error in errors) Errors.Add(error);
        IsSaving = false;
        OnPropertyChanged(nameof(HasErrors));
    }

    partial void OnStepChanged(int value) => RaiseAll();
    partial void OnIsPaidChanged(bool value) => RaiseAll();
    partial void OnAmountPaidChanged(decimal value) => RaiseAll();
    partial void OnDueDateChanged(DateTime? value) => OnPropertyChanged(nameof(DueDaysLabel));
    partial void OnCashTenderedChanged(decimal value) => OnPropertyChanged(nameof(Change));

    /// <summary>Enter on a mode in step 2: that mode takes whatever is still unallocated.</summary>
    [RelayCommand]
    private void AssignRemaining(BillingTenderRowViewModel? row)
    {
        row ??= SelectedTender;
        if (row is null) return;
        var remaining = BillingPaymentRules.Remaining(PayingNowPaisa, CurrentTenders());
        if (remaining > 0) row.Amount += remaining / 100m;
        SelectedTender = row;
    }

    [RelayCommand]
    private void Continue()
    {
        if (IsSaving) return;
        Errors.Clear();
        switch (Step)
        {
            case 1:
                AddErrors(BillingPaymentRules.ValidateStep1(_context.PayablePaisa, _context.IsCounterSale, IsPaid,
                    PayingNowPaisa, DueDate, _context.InvoiceDate));
                if (HasErrors) return;
                if (!IsPaid || PayingNowPaisa == 0)
                {
                    Complete();
                    return;
                }
                PrepareStep2(Array.Empty<SaleTender>());
                Step = 2;
                return;
            case 2:
                AddErrors(BillingPaymentRules.ValidateStep2(PayingNowPaisa, CurrentTenders()));
                if (HasErrors) return;
                if (BillingPaymentRules.NeedsStep3(CurrentTenders()))
                {
                    if (CashTendered == 0m) CashTendered = CashTenderPaisa / 100m;
                    Step = 3;
                    return;
                }
                Complete();
                return;
            default:
                Complete();
                return;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsSaving) return;
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));
        if (Step == 3) Step = 2;
        else if (Step == 2 && !_context.IsCounterSale) Step = 1;
        else Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    private void Complete()
    {
        var isCredit = !IsPaid || PayingNowPaisa == 0;
        var tenders = isCredit ? Array.Empty<SaleTender>() : CurrentTenders().Where(t => t.AmountPaisa > 0).ToArray();
        var cashTendered = PricingCalculator.RoundPaisa(CashTendered * 100m);
        var dueDate = BalanceDue > 0 ? DueDate : null;

        // Same final check the service runs, so the user sees the message on the wizard.
        var (_, errors) = BillingCalculator.ResolvePayment(_context.PayablePaisa, new SalePostingRequest
        {
            InvoiceDate = _context.InvoiceDate,
            IsCounterSale = _context.IsCounterSale,
            CustomerId = _context.IsCounterSale ? null : 1,
            IsCredit = isCredit && _context.PayablePaisa > 0,
            DueDate = dueDate,
            Tenders = tenders,
            CashTenderedPaisa = cashTendered
        });
        if (errors.Count > 0)
        {
            AddErrors(errors);
            return;
        }

        IsSaving = true;
        Completed?.Invoke(this, new Outcome(isCredit && _context.PayablePaisa > 0, dueDate, tenders, cashTendered));
    }

    private void PrepareStep2(IReadOnlyList<SaleTender> existing)
    {
        _allocating = true;
        foreach (var row in Tenders)
            row.Amount = existing.Where(t => t.Mode == row.Mode).Sum(t => t.AmountPaisa) / 100m;
        if (BillingPaymentRules.Remaining(PayingNowPaisa, CurrentTenders()) == PayingNowPaisa)
            Tenders[0].Amount = PayingNow; // default: everything in cash
        _allocating = false;
        OnTendersChanged();
    }

    private IReadOnlyList<SaleTender> CurrentTenders() => Tenders.Select(t => t.ToTender()).ToList();

    private void OnTendersChanged()
    {
        if (_allocating) return;
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(HasCash));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(UsedReferenceTenders));
        OnPropertyChanged(nameof(ContinueLabel));
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors) Errors.Add(error);
        OnPropertyChanged(nameof(HasErrors));
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Payable), nameof(PayingCustomer), nameof(InvoiceNumber), nameof(InvoiceDate), nameof(IsCounterSale),
                     nameof(CanChooseCredit), nameof(PayingNow), nameof(BalanceDue), nameof(ShowDueDate), nameof(DueDaysLabel),
                     nameof(StepTitle), nameof(ContinueLabel), nameof(HasErrors), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3)
                 })
            OnPropertyChanged(name);
        OnTendersChanged();
    }
}
