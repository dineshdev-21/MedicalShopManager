using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// Distributor form shared by the purchase screen's quick-add and the Distributors page: name, GSTIN, drug licence,
/// phone, address, state, pincode, credit days and opening balance. Validation is done by
/// <see cref="IDistributorService.SaveAsync"/> (PurchaseValidator.ValidateDistributor); its messages land in <see cref="Errors"/>.
/// </summary>
public sealed partial class PurchaseDistributorEditorViewModel : ObservableObject
{
    public IReadOnlyList<GstState> States => GstStateCodes.All;
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private long _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _gstin = string.Empty;
    [ObservableProperty] private string _drugLicenseNumber = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _contactPerson = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private GstState? _state;
    [ObservableProperty] private string _pincode = string.Empty;
    [ObservableProperty] private int _creditDays;
    [ObservableProperty] private decimal _openingBalance;
    [ObservableProperty] private bool _isActive = true;

    public bool IsNew => Id == 0;
    public string Title => IsNew ? "Add Distributor" : $"Edit {Name}";

    partial void OnIdChanged(long value)
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Typing a GSTIN fills the state from its first two digits.</summary>
    partial void OnGstinChanged(string value)
    {
        if (value is { Length: >= 2 } g && char.IsDigit(g[0]) && char.IsDigit(g[1]) && GstStateCodes.Find(g[..2]) is { } state)
            State = state;
    }

    public void Load(Distributor d)
    {
        ArgumentNullException.ThrowIfNull(d);
        Errors.Clear();
        Id = d.Id;
        Name = d.Name;
        Gstin = d.Gstin ?? string.Empty;
        DrugLicenseNumber = d.DrugLicenseNumber ?? string.Empty;
        Phone = d.Phone ?? string.Empty;
        ContactPerson = d.ContactPerson ?? string.Empty;
        Email = d.Email ?? string.Empty;
        Address = d.Address ?? string.Empty;
        State = GstStateCodes.Find(d.EffectiveStateCode);
        Pincode = d.Pincode ?? string.Empty;
        CreditDays = d.CreditDays;
        OpeningBalance = d.OpeningBalancePaisa / 100m;
        IsActive = d.IsActive;
        OnPropertyChanged(nameof(Title));
    }

    public Distributor ToDistributor() => new()
    {
        Id = Id,
        Name = Name,
        Gstin = Gstin,
        DrugLicenseNumber = DrugLicenseNumber,
        Phone = Phone,
        ContactPerson = ContactPerson,
        Email = Email,
        Address = Address,
        StateCode = State?.Code,
        Pincode = Pincode,
        CreditDays = CreditDays,
        OpeningBalancePaisa = PurchaseCalculator.ToPaisa(OpeningBalance),
        IsActive = IsActive
    };
}
