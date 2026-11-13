using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Control Center ▸ General Settings (page key "Settings", moved here from App/ViewModels). The shop profile moved to
/// Business Profile and the invoice prefix to Prefix &amp; Sequences; what remains are the system-wide defaults.
/// Keys: Ctrl+S save.
/// </summary>
public partial class SettingsViewModel : BaseViewModel
{
    private readonly IControlCenterSettingsService _settings;

    public SettingsViewModel(IControlCenterSettingsService settings) => _settings = settings;

    public IReadOnlyList<decimal> GstSlabs => GeneralSettings.GstSlabs;
    public FieldErrorMap Errors { get; } = new();

    [ObservableProperty] private decimal _defaultGstRate = 12m;
    [ObservableProperty] private string _nearExpiryDays = "90";
    [ObservableProperty] private string _backupRetentionDays = "30";
    [ObservableProperty] private bool _autoBackupEnabled = true;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        Errors.Clear();
        try
        {
            var s = await _settings.GetGeneralSettingsAsync();
            DefaultGstRate = s.DefaultGstRatePercent;
            NearExpiryDays = s.NearExpiryDays.ToString(CultureInfo.InvariantCulture);
            BackupRetentionDays = s.BackupRetentionDays.ToString(CultureInfo.InvariantCulture);
            AutoBackupEnabled = s.AutoBackupEnabled;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsLoading) return;
        ClearStatus();
        var parseErrors = new List<SettingsError>();
        if (!int.TryParse(NearExpiryDays?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nearExpiry))
            parseErrors.Add(new SettingsError(nameof(GeneralSettings.NearExpiryDays), "Near-expiry alert must be a whole number of days."));
        if (!int.TryParse(BackupRetentionDays?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention))
            parseErrors.Add(new SettingsError(nameof(GeneralSettings.BackupRetentionDays), "Backup retention must be a whole number of days."));
        if (parseErrors.Count > 0)
        {
            Errors.Set(parseErrors);
            SetError(FieldErrorMap.Summary(new ControlCenterValidationException(parseErrors)));
            return;
        }

        IsLoading = true;
        try
        {
            await _settings.SaveGeneralSettingsAsync(new GeneralSettings
            {
                DefaultGstRatePercent = DefaultGstRate,
                NearExpiryDays = nearExpiry,
                BackupRetentionDays = retention,
                AutoBackupEnabled = AutoBackupEnabled
            });
            Errors.Clear();
            SetStatus("Settings saved successfully.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Failed to save settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task DiscardChangesAsync() => LoadAsync();
}
