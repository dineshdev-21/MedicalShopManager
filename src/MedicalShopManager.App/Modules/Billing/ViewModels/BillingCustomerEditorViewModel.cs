using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// "Add New Customer" / "Edit Customer" dialog (spec 02 §D): INDIVIDUAL fields (gender, DOB or age) or BUSINESS
/// fields (GSTIN → PAN, composition dealer), sales discount, reference, address, opening balance, credit days.
/// Validation and saving go through <see cref="IBillingCustomerService"/>. F2 saves, Esc closes.
/// </summary>
public sealed partial class BillingCustomerEditorViewModel : ObservableObject
{
    private readonly IBillingCustomerService _customers;
    private readonly ILogger<BillingCustomerEditorViewModel> _logger;
    private bool _syncingAge;

    public BillingCustomerEditorViewModel(IBillingCustomerService customers, ILogger<BillingCustomerEditorViewModel> logger)
    {
        _customers = customers;
        _logger = logger;
    }

    public event EventHandler<BillingCustomer>? Saved;
    public event EventHandler? Closed;

    [ObservableProperty] private long _id;
    [ObservableProperty] private bool _isBusiness;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _phone;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private PersonGender _gender;
    [ObservableProperty] private DateTime? _dateOfBirth;
    [ObservableProperty] private int? _ageYears;
    [ObservableProperty] private int? _ageMonths;
    [ObservableProperty] private string? _gstin;
    [ObservableProperty] private bool _isCompositionDealer;
    [ObservableProperty] private decimal _discountPercent;
    [ObservableProperty] private string? _referenceNo;
    [ObservableProperty] private string? _address;
    [ObservableProperty] private decimal _openingBalance;
    [ObservableProperty] private DateTime? _openingBalanceDate;
    [ObservableProperty] private int _creditDays;
    [ObservableProperty] private string? _doctorName;
    [ObservableProperty] private bool _isSaving;

    public ObservableCollection<string> Errors { get; } = new();
    public IReadOnlyList<PersonGender> Genders { get; } = new[] { PersonGender.Unspecified, PersonGender.Male, PersonGender.Female, PersonGender.Other };

    public string Title => Id > 0 ? "Edit Customer" : "Add New Customer";
    public string? Pan => BillingCustomerRules.PanFromGstin(Gstin?.Trim());
    public bool HasErrors => Errors.Count > 0;

    /// <summary>Starts a new customer, the name pre-filled from the picker's search text (digits go to MOBILE).</summary>
    public void StartNew(string? searchText)
    {
        Load(new BillingCustomer());
        var text = searchText?.Trim() ?? string.Empty;
        if (text.Length > 0 && text.All(char.IsAsciiDigit)) Phone = text;
        else Name = text;
    }

    public void Load(BillingCustomer customer)
    {
        Errors.Clear();
        Id = customer.Id;
        IsBusiness = customer.IsBusiness;
        Name = customer.Name;
        Phone = customer.Phone;
        Email = customer.Email;
        Gender = customer.Gender;
        AgeYears = null;
        AgeMonths = null;
        DateOfBirth = customer.DateOfBirth;
        Gstin = customer.Gstin;
        IsCompositionDealer = customer.IsCompositionDealer;
        DiscountPercent = customer.DefaultDiscountPercent;
        ReferenceNo = customer.ReferenceNo;
        Address = customer.Address;
        OpeningBalance = customer.OpeningBalancePaisa / 100m;
        OpeningBalanceDate = customer.OpeningBalanceDate;
        CreditDays = customer.CreditDays;
        DoctorName = customer.DoctorName;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasErrors));
    }

    partial void OnGstinChanged(string? value) => OnPropertyChanged(nameof(Pan));
    partial void OnIdChanged(long value) => OnPropertyChanged(nameof(Title));

    partial void OnDateOfBirthChanged(DateTime? value)
    {
        if (_syncingAge) return;
        _syncingAge = true;
        if (value is { } dob && dob.Date <= DateTime.Today)
        {
            var today = DateTime.Today;
            var totalMonths = (today.Year - dob.Year) * 12 + today.Month - dob.Month - (today.Day < dob.Day ? 1 : 0);
            AgeYears = Math.Max(totalMonths, 0) / 12;
            AgeMonths = Math.Max(totalMonths, 0) % 12;
        }
        _syncingAge = false;
    }

    partial void OnAgeYearsChanged(int? value) => SyncDobFromAge();
    partial void OnAgeMonthsChanged(int? value) => SyncDobFromAge();

    private void SyncDobFromAge()
    {
        if (_syncingAge || AgeYears is null) return;
        var years = AgeYears.Value;
        var months = AgeMonths ?? 0;
        if (years < 0 || years > 130 || months < 0 || months > 11) return;
        _syncingAge = true;
        DateOfBirth = BillingCustomerRules.DateOfBirthFromAge(years, months, DateTime.Today);
        _syncingAge = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving) return;
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));
        IsSaving = true;
        try
        {
            var saved = await _customers.SaveAsync(new BillingCustomer
            {
                Id = Id,
                Kind = IsBusiness ? CustomerKind.Business : CustomerKind.Individual,
                Name = Name ?? string.Empty,
                Phone = Phone,
                Email = Email,
                Gender = Gender,
                DateOfBirth = DateOfBirth,
                Gstin = Gstin,
                IsCompositionDealer = IsCompositionDealer,
                DefaultDiscountPercent = DiscountPercent,
                ReferenceNo = ReferenceNo,
                Address = Address,
                OpeningBalancePaisa = PricingCalculator.RoundPaisa(OpeningBalance * 100m),
                OpeningBalanceDate = OpeningBalance != 0m ? OpeningBalanceDate ?? DateTime.Today : OpeningBalanceDate,
                CreditDays = CreditDays,
                DoctorName = DoctorName
            });
            Saved?.Invoke(this, saved);
        }
        catch (BillingValidationException ex)
        {
            foreach (var error in ex.Errors) Errors.Add(error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving customer {Name} failed", Name);
            Errors.Add($"The customer could not be saved: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
