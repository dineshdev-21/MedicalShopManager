using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Control Center ▸ Business Profile (Localwell /control-center/business-profile). Every rule lives in
/// <see cref="BusinessProfileValidator"/>; this ViewModel only maps fields and shows the messages.
/// Keys: Ctrl+S save.
/// </summary>
public partial class BusinessProfileViewModel : BaseViewModel
{
    private readonly IControlCenterSettingsService _settings;

    public BusinessProfileViewModel(IControlCenterSettingsService settings) => _settings = settings;

    public IReadOnlyList<IndianState> States => IndianStates.All;
    /// <summary>Combo items: Key = enum value (SelectedValuePath), Value = label (DisplayMemberPath).</summary>
    public IReadOnlyList<KeyValuePair<GstRegistrationType, string>> RegistrationTypes { get; } =
        Enum.GetValues<GstRegistrationType>().Select(t => new KeyValuePair<GstRegistrationType, string>(t, t.ToLabel())).ToList();
    public FieldErrorMap Errors { get; } = new();

    [ObservableProperty] private string _shopName = string.Empty;
    [ObservableProperty] private string _legalName = string.Empty;
    [ObservableProperty] private string _addressLine1 = string.Empty;
    [ObservableProperty] private string _addressLine2 = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _pincode = string.Empty;
    [ObservableProperty] private IndianState? _selectedState;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private GstRegistrationType _gstRegistrationType = GstRegistrationType.Regular;
    [ObservableProperty] private string _gstin = string.Empty;
    [ObservableProperty] private string _pan = string.Empty;
    [ObservableProperty] private string _drugLicense20B = string.Empty;
    [ObservableProperty] private string _drugLicense21B = string.Empty;
    [ObservableProperty] private DateTime? _drugLicenseExpiry;
    [ObservableProperty] private string _fssai = string.Empty;
    [ObservableProperty] private string _pharmacistName = string.Empty;
    [ObservableProperty] private string _pharmacistRegistrationNo = string.Empty;
    [ObservableProperty] private string _bankName = string.Empty;
    [ObservableProperty] private string _bankAccountName = string.Empty;
    [ObservableProperty] private string _bankAccountNumber = string.Empty;
    [ObservableProperty] private string _bankIfsc = string.Empty;
    [ObservableProperty] private string _bankBranch = string.Empty;
    [ObservableProperty] private string _upiId = string.Empty;

    [ObservableProperty] private ImageSource? _logoPreview;
    [ObservableProperty] private ImageSource? _signaturePreview;

    /// <summary>"Tax Invoice" / "Bill of Supply": what sale documents will be titled.</summary>
    public string DocumentTitleHint => GstRegistrationType.SaleDocumentTitle();

    public string TaxRegimeHint => GstRegistrationType switch
    {
        GstRegistrationType.Regular =>
            $"Bills are titled \"Tax Invoice\". Sales to a party with a GSTIN from another state get IGST; all other sales CGST + SGST{(SelectedState is { } s ? $" ({s.Name})" : string.Empty)}.",
        GstRegistrationType.Composition =>
            "Composition dealers cannot collect GST: bills are titled \"Bill of Supply\" and show no tax lines.",
        _ => "Unregistered shops issue a \"Bill of Supply\" without GST lines. No GSTIN is printed."
    };

    public bool IsGstinRequired => GstRegistrationType.RequiresGstin();

    public bool HasLogo => LogoPreview != null;
    public bool HasSignature => SignaturePreview != null;

    partial void OnGstRegistrationTypeChanged(GstRegistrationType value)
    {
        OnPropertyChanged(nameof(DocumentTitleHint));
        OnPropertyChanged(nameof(TaxRegimeHint));
        OnPropertyChanged(nameof(IsGstinRequired));
    }

    partial void OnSelectedStateChanged(IndianState? value) => OnPropertyChanged(nameof(TaxRegimeHint));
    partial void OnLogoPreviewChanged(ImageSource? value) => OnPropertyChanged(nameof(HasLogo));
    partial void OnSignaturePreviewChanged(ImageSource? value) => OnPropertyChanged(nameof(HasSignature));

    /// <summary>A complete GSTIN fills the state and PAN when they are still empty (state code = first 2 digits).</summary>
    partial void OnGstinChanged(string value)
    {
        var gstin = IndianIdentifiers.Normalize(value);
        if (gstin.Length != 15 || IndianIdentifiers.ValidateGstin(gstin) != null) return;
        SelectedState ??= IndianStates.Find(gstin[..2]);
        if (string.IsNullOrWhiteSpace(Pan)) Pan = IndianIdentifiers.PanFromGstin(gstin);
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        Errors.Clear();
        try
        {
            Apply(await _settings.GetBusinessProfileAsync());
            LogoPreview = BrandingImageLoader.ToImageSource((await _settings.GetBrandingImageAsync(BrandingImageKind.Logo))?.Data);
            SignaturePreview = BrandingImageLoader.ToImageSource((await _settings.GetBrandingImageAsync(BrandingImageKind.Signature))?.Data);
        }
        catch (Exception ex)
        {
            SetError($"Could not load the business profile: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(BusinessProfile p)
    {
        ShopName = p.ShopName;
        LegalName = p.LegalName;
        AddressLine1 = p.AddressLine1;
        AddressLine2 = p.AddressLine2;
        City = p.City;
        Pincode = p.Pincode;
        SelectedState = p.State;
        Phone = p.Phone;
        Email = p.Email;
        GstRegistrationType = p.GstRegistrationType;
        Gstin = p.Gstin;
        Pan = p.Pan;
        DrugLicense20B = p.DrugLicense20B;
        DrugLicense21B = p.DrugLicense21B;
        DrugLicenseExpiry = p.DrugLicenseExpiry;
        Fssai = p.Fssai;
        PharmacistName = p.PharmacistName;
        PharmacistRegistrationNo = p.PharmacistRegistrationNo;
        BankName = p.BankName;
        BankAccountName = p.BankAccountName;
        BankAccountNumber = p.BankAccountNumber;
        BankIfsc = p.BankIfsc;
        BankBranch = p.BankBranch;
        UpiId = p.UpiId;
    }

    private BusinessProfile ToProfile() => new()
    {
        ShopName = ShopName,
        LegalName = LegalName,
        AddressLine1 = AddressLine1,
        AddressLine2 = AddressLine2,
        City = City,
        Pincode = Pincode,
        StateCode = SelectedState?.Code ?? string.Empty,
        Phone = Phone,
        Email = Email,
        GstRegistrationType = GstRegistrationType,
        Gstin = Gstin,
        Pan = Pan,
        DrugLicense20B = DrugLicense20B,
        DrugLicense21B = DrugLicense21B,
        DrugLicenseExpiry = DrugLicenseExpiry,
        Fssai = Fssai,
        PharmacistName = PharmacistName,
        PharmacistRegistrationNo = PharmacistRegistrationNo,
        BankName = BankName,
        BankAccountName = BankAccountName,
        BankAccountNumber = BankAccountNumber,
        BankIfsc = BankIfsc,
        BankBranch = BankBranch,
        UpiId = UpiId
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ClearStatus();
        try
        {
            await _settings.SaveBusinessProfileAsync(ToProfile());
            Errors.Clear();
            Apply(await _settings.GetBusinessProfileAsync()); // show the normalised values (upper-case GSTIN, derived PAN)
            SetStatus("Business profile saved. New invoices will print these details.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Could not save the business profile: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task DiscardChangesAsync() => LoadAsync();

    [RelayCommand]
    private Task ChooseLogoAsync() => ChooseImageAsync(BrandingImageKind.Logo, "Choose shop logo");

    [RelayCommand]
    private Task ChooseSignatureAsync() => ChooseImageAsync(BrandingImageKind.Signature, "Choose authorised signature");

    [RelayCommand]
    private Task RemoveLogoAsync() => RemoveImageAsync(BrandingImageKind.Logo);

    [RelayCommand]
    private Task RemoveSignatureAsync() => RemoveImageAsync(BrandingImageKind.Signature);

    private async Task ChooseImageAsync(BrandingImageKind kind, string title)
    {
        ClearStatus();
        try
        {
            var data = BrandingImageLoader.PickImageFile(title);
            if (data == null) return;
            await _settings.SaveBrandingImageAsync(kind, data);
            SetPreview(kind, BrandingImageLoader.ToImageSource(data));
            SetStatus(kind == BrandingImageKind.Logo ? "Logo saved." : "Signature saved.");
        }
        catch (ControlCenterValidationException ex)
        {
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Could not use that image: {ex.Message}");
        }
    }

    private async Task RemoveImageAsync(BrandingImageKind kind)
    {
        ClearStatus();
        try
        {
            await _settings.RemoveBrandingImageAsync(kind);
            SetPreview(kind, null);
            SetStatus(kind == BrandingImageKind.Logo ? "Logo removed." : "Signature removed.");
        }
        catch (Exception ex)
        {
            SetError($"Could not remove the image: {ex.Message}");
        }
    }

    private void SetPreview(BrandingImageKind kind, ImageSource? image)
    {
        if (kind == BrandingImageKind.Logo) LogoPreview = image;
        else SignaturePreview = image;
    }
}
