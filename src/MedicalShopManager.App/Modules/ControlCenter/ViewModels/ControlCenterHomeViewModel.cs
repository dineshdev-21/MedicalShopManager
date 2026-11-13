using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>A Control Center tile: opens one settings page.</summary>
public sealed record ControlCenterTile(string PageKey, string Title, string Description, string Glyph, string Shortcut);

/// <summary>
/// Control Center landing page (Localwell opens /control-center/business-profile by default; here a hub groups the
/// Control Center pages with the shared Backup and Excel pages) plus a set-up checklist of what is still missing
/// before invoices are compliant.
/// </summary>
public partial class ControlCenterHomeViewModel : BaseViewModel
{
    private readonly INavigationService _navigation;
    private readonly IControlCenterSettingsService _settings;

    public ControlCenterHomeViewModel(INavigationService navigation, IControlCenterSettingsService settings)
    {
        _navigation = navigation;
        _settings = settings;
    }

    public IReadOnlyList<ControlCenterTile> Tiles { get; } = new[]
    {
        new ControlCenterTile("BusinessProfile", "Business Profile", "Shop name, address, state, GSTIN, drug licences, FSSAI, pharmacist, logo, bank and UPI.", "", "Alt+1"),
        new ControlCenterTile("SaleSettings", "Sale Settings", "Credit default, due days, default doctor, expiry cut-off, rounding, negative stock, discounts.", "", "Alt+2"),
        new ControlCenterTile("PrefixSequences", "Prefix & Sequences", "Invoice and document numbering per type, padding, next number and FY reset.", "", "Alt+3"),
        new ControlCenterTile("PrintLayout", "Print Layout", "Paper size per document and the fields printed on invoices, with preview.", "", "Alt+4"),
        new ControlCenterTile("UserManagement", "Users & Permissions", "Staff accounts, roles, permission matrix and the audit trail.", "", "Alt+5"),
        new ControlCenterTile("Settings", "General Settings", "Default GST rate, expiry alerts and backup retention.", "", "Alt+6"),
        new ControlCenterTile("Backup", "Backup & Restore", "Create and restore verified database backups.", "", "Alt+7"),
        new ControlCenterTile("ExcelManagement", "Excel Data", "Excel workbook sync and import / export.", "", "Alt+8")
    };

    /// <summary>Things still missing for a compliant invoice (empty = all set).</summary>
    public ObservableCollection<string> SetupWarnings { get; } = new();

    public bool IsSetupComplete => SetupWarnings.Count == 0 && !IsLoading;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var profile = await _settings.GetBusinessProfileAsync();
            var logo = await _settings.GetBrandingImageAsync(BrandingImageKind.Logo);
            SetupWarnings.Clear();
            foreach (var warning in SetupChecklist(profile, logo != null, DateTime.Today))
                SetupWarnings.Add(warning);
        }
        catch (Exception ex)
        {
            SetError($"Could not load the Control Center summary: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsSetupComplete));
        }
    }

    private static IEnumerable<string> SetupChecklist(BusinessProfile p, bool hasLogo, DateTime today)
    {
        if (BusinessProfileValidator.Validate(BusinessProfileValidator.Normalize(p)).Count > 0)
            yield return "Business profile has missing or invalid details (open Business Profile to fix them).";
        if (p.State == null) yield return "Shop state is not set: GST cannot be split into CGST + SGST or IGST.";
        if (string.IsNullOrWhiteSpace(p.DrugLicense20B) && string.IsNullOrWhiteSpace(p.DrugLicense21B))
            yield return "Drug licence numbers (20B / 21B) are not entered; they must be printed on every bill.";
        if (BusinessProfileValidator.IsDrugLicenseExpiringSoon(p, today))
            yield return $"Drug licence expires on {p.DrugLicenseExpiry:dd-MMM-yyyy}. Renew it and update the profile.";
        if (string.IsNullOrWhiteSpace(p.PharmacistName)) yield return "Registered pharmacist name is not entered.";
        if (!hasLogo) yield return "No shop logo uploaded (optional).";
    }

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrWhiteSpace(pageKey)) _navigation.NavigateTo(pageKey);
    }
}
