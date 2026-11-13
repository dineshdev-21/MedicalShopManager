using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>One payment mode row of the payment wizard (step 2 amount, step 3 reference / cheque date).</summary>
public sealed partial class BillingTenderRowViewModel : ObservableObject
{
    public BillingTenderRowViewModel(TenderMode mode) => Mode = mode;

    public event EventHandler? Changed;

    public TenderMode Mode { get; }
    public string Label => BillingPaymentRules.Label(Mode);
    public bool NeedsReference => BillingPaymentRules.NeedsReference(Mode);
    public bool IsCheque => Mode == TenderMode.Cheque;
    public string ReferenceLabel => Mode switch
    {
        TenderMode.Cheque => "CHEQUE NO (6+ digits)",
        TenderMode.Upi => "UPI TXN REF / UTR",
        TenderMode.BankTransfer => "TXN REF NO OR UTR",
        _ => "REFERENCE"
    };

    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string? _reference;
    [ObservableProperty] private DateTime? _chequeDate = DateTime.Today;

    public long AmountPaisa => PricingCalculator.RoundPaisa(Amount * 100m);
    public bool IsUsed => AmountPaisa > 0;

    public SaleTender ToTender() => new()
    {
        Mode = Mode,
        AmountPaisa = AmountPaisa,
        Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim(),
        ChequeDate = IsCheque ? ChequeDate : null
    };

    partial void OnAmountChanged(decimal value)
    {
        OnPropertyChanged(nameof(IsUsed));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
