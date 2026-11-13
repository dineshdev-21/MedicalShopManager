using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Control Center ▸ Sale Settings (Localwell /control-center/sale-settings). Rules: <see cref="SaleSettingsRules"/>.
/// The sale invoice's default paper size is the shared key Print.PaperSize.SaleInvoice (also editable on Print Layout).
/// Keys: Ctrl+S save.
/// </summary>
public partial class SaleSettingsViewModel : BaseViewModel
{
    /// <summary>Name of the sale invoice template (contract §9); its paper size key is Print.PaperSize.SaleInvoice.</summary>
    public const string SaleInvoiceTemplateName = "SaleInvoice";

    private readonly IControlCenterSettingsService _settings;

    public SaleSettingsViewModel(IControlCenterSettingsService settings) => _settings = settings;

    public FieldErrorMap Errors { get; } = new();

    public IReadOnlyList<KeyValuePair<bool, string>> PaymentDefaults { get; } = new[]
    {
        new KeyValuePair<bool, string>(false, "Paid (cash / UPI / card collected now)"),
        new KeyValuePair<bool, string>(true, "Credit (amount due later)")
    };

    public IReadOnlyList<KeyValuePair<RoundOffMode, string>> RoundOffModes { get; } = new[]
    {
        new KeyValuePair<RoundOffMode, string>(RoundOffMode.NearestRupee, "Nearest rupee (₹251.20 → ₹251)"),
        new KeyValuePair<RoundOffMode, string>(RoundOffMode.NearestFiftyPaise, "Nearest 50 paise (₹251.20 → ₹251.00)"),
        new KeyValuePair<RoundOffMode, string>(RoundOffMode.None, "No rounding (exact paise)")
    };

    public IReadOnlyList<KeyValuePair<int, string>> NearExpiryChoices { get; } =
        SaleSettingsRules.NearExpiryChoices.Select(m => new KeyValuePair<int, string>(m, $"Less than {m} months")).ToList();

    public IReadOnlyList<PaperSize> PaperSizes { get; } = Enum.GetValues<PaperSize>();

    [ObservableProperty] private bool _newSaleIsCredit;
    [ObservableProperty] private string _defaultDueDays = "0";
    [ObservableProperty] private string _defaultDoctorName = string.Empty;
    [ObservableProperty] private long? _defaultDoctorId;
    [ObservableProperty] private string _expiryCutoffMonths = "0";
    [ObservableProperty] private int _nearExpiryMonths = 6;
    [ObservableProperty] private RoundOffMode _roundOffMode = RoundOffMode.NearestRupee;
    [ObservableProperty] private bool _allowNegativeStock;
    [ObservableProperty] private bool _addSchemeWrtSaleQty;
    [ObservableProperty] private bool _barcodeQtyInPacks;
    [ObservableProperty] private string _defaultDiscountPercent = "0";
    [ObservableProperty] private string _minDiscountPercent = "0";
    [ObservableProperty] private string _maxDiscountPercent = "100";
    [ObservableProperty] private bool _patientReminders = true;
    [ObservableProperty] private bool _requireDoctorForScheduleH;
    [ObservableProperty] private bool _requirePrescriptionReference;
    [ObservableProperty] private PaperSize _invoicePaperSize = PaperSize.A5;

    /// <summary>A typed doctor name no longer refers to the doctor record that was picked before.</summary>
    partial void OnDefaultDoctorNameChanged(string value) => DefaultDoctorId = null;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        Errors.Clear();
        try
        {
            Apply(await _settings.GetSaleSettingsAsync());
            InvoicePaperSize = await _settings.GetPaperSizeAsync(SaleInvoiceTemplateName) ?? PaperSize.A5;
        }
        catch (Exception ex)
        {
            SetError($"Could not load sale settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(SaleSettings s)
    {
        var inv = CultureInfo.InvariantCulture;
        NewSaleIsCredit = s.NewSaleIsCredit;
        DefaultDueDays = s.DefaultDueDays.ToString(inv);
        DefaultDoctorName = s.DefaultDoctorName;
        DefaultDoctorId = s.DefaultDoctorId; // after the name: typing a new name clears the id

        ExpiryCutoffMonths = s.ExpiryCutoffMonths.ToString(inv);
        NearExpiryMonths = s.NearExpiryMonths;
        RoundOffMode = s.RoundOffMode;
        AllowNegativeStock = s.AllowNegativeStock;
        AddSchemeWrtSaleQty = s.AddSchemeWrtSaleQty;
        BarcodeQtyInPacks = s.BarcodeQtyInPacks;
        DefaultDiscountPercent = s.DefaultDiscountPercent.ToString("0.##", inv);
        MinDiscountPercent = s.MinDiscountPercent.ToString("0.##", inv);
        MaxDiscountPercent = s.MaxDiscountPercent.ToString("0.##", inv);
        PatientReminders = s.PatientReminders;
        RequireDoctorForScheduleH = s.RequireDoctorForScheduleH;
        RequirePrescriptionReference = s.RequirePrescriptionReference;
    }

    /// <summary>Parses the text boxes; unparsable numbers become field errors instead of silently becoming 0.</summary>
    private SaleSettings? TryBuild(List<SettingsError> errors)
    {
        int Int(string text, string field, string label)
        {
            if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            errors.Add(new SettingsError(field, $"{label} must be a whole number."));
            return 0;
        }

        decimal Dec(string text, string field, string label)
        {
            if (decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
            errors.Add(new SettingsError(field, $"{label} must be a number."));
            return 0m;
        }

        var settings = new SaleSettings
        {
            NewSaleIsCredit = NewSaleIsCredit,
            DefaultDueDays = Int(DefaultDueDays, nameof(SaleSettings.DefaultDueDays), "Default due days"),
            DefaultDoctorId = string.IsNullOrWhiteSpace(DefaultDoctorName) ? null : DefaultDoctorId,
            DefaultDoctorName = DefaultDoctorName,
            ExpiryCutoffMonths = Int(ExpiryCutoffMonths, nameof(SaleSettings.ExpiryCutoffMonths), "Expiry cut-off"),
            NearExpiryMonths = NearExpiryMonths,
            RoundOffMode = RoundOffMode,
            AllowNegativeStock = AllowNegativeStock,
            AddSchemeWrtSaleQty = AddSchemeWrtSaleQty,
            BarcodeQtyInPacks = BarcodeQtyInPacks,
            DefaultDiscountPercent = Dec(DefaultDiscountPercent, nameof(SaleSettings.DefaultDiscountPercent), "Default discount"),
            MinDiscountPercent = Dec(MinDiscountPercent, nameof(SaleSettings.MinDiscountPercent), "Minimum discount"),
            MaxDiscountPercent = Dec(MaxDiscountPercent, nameof(SaleSettings.MaxDiscountPercent), "Maximum discount"),
            PatientReminders = PatientReminders,
            RequireDoctorForScheduleH = RequireDoctorForScheduleH,
            RequirePrescriptionReference = RequirePrescriptionReference
        };
        return errors.Count == 0 ? settings : null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLoading) return;
        ClearStatus();
        var parseErrors = new List<SettingsError>();
        var settings = TryBuild(parseErrors);
        if (settings == null)
        {
            var ex = new ControlCenterValidationException(parseErrors);
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
            return;
        }

        IsLoading = true;
        try
        {
            await _settings.SaveSaleSettingsAsync(settings);
            await _settings.SavePaperSizesAsync(new Dictionary<string, PaperSize> { [SaleInvoiceTemplateName] = InvoicePaperSize });
            Errors.Clear();
            SetStatus(AllowNegativeStock
                ? "Sale settings saved. Note: negative stock is allowed, so bills can exceed the stock on hand."
                : "Sale settings saved. They apply to the next new bill.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Could not save sale settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task DiscardChangesAsync() => LoadAsync();

    [RelayCommand]
    private void ClearDefaultDoctor()
    {
        DefaultDoctorId = null;
        DefaultDoctorName = string.Empty;
    }
}
