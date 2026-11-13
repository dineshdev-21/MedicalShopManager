namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// The pre-6.2 system settings Control Center owns (keys DefaultGstRate, NearExpiryDays, BackupRetentionDays,
/// AutoBackupEnabled), shown on Control Center ▸ General Settings.
/// </summary>
public sealed record GeneralSettings
{
    /// <summary>GST slabs that apply to medicines and pharmacy goods.</summary>
    public static readonly IReadOnlyList<decimal> GstSlabs = new[] { 0m, 5m, 12m, 18m, 28m };

    public decimal DefaultGstRatePercent { get; init; } = 12m;
    public int NearExpiryDays { get; init; } = 90;
    public int BackupRetentionDays { get; init; } = 30;
    public bool AutoBackupEnabled { get; init; } = true;

    public static IReadOnlyList<SettingsError> Validate(GeneralSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var errors = new List<SettingsError>();
        if (!GstSlabs.Contains(s.DefaultGstRatePercent))
            errors.Add(new(nameof(DefaultGstRatePercent), "Default GST rate must be one of 0, 5, 12, 18 or 28%."));
        if (s.NearExpiryDays is < 1 or > 365)
            errors.Add(new(nameof(NearExpiryDays), "Near-expiry alert must be between 1 and 365 days."));
        if (s.BackupRetentionDays is < 1 or > 3650)
            errors.Add(new(nameof(BackupRetentionDays), "Backup retention must be between 1 and 3650 days."));
        return errors;
    }
}
