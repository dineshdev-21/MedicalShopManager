using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.ControlCenter;
using K = MedicalShopManager.Core.Modules.ControlCenter.ControlCenterSettingKeys;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>
/// <see cref="IControlCenterSettingsService"/> over AppSettings + ControlCenterAssets. Each save: normalise →
/// validate (nothing written on error) → write the area's keys in one transaction → audit (old and new values) →
/// raise <see cref="SettingsChanged"/>.
/// </summary>
public sealed class ControlCenterSettingsService : IControlCenterSettingsService
{
    private static readonly JsonSerializerOptions AuditJson = new() { WriteIndented = false };

    private readonly ControlCenterSettingsStore _store;
    private readonly IAuditService _audit;
    private readonly ILogger<ControlCenterSettingsService> _logger;

    public ControlCenterSettingsService(ControlCenterSettingsStore store, IAuditService audit, ILogger<ControlCenterSettingsService> logger)
    {
        _store = store;
        _audit = audit;
        _logger = logger;
    }

    public event EventHandler<ControlCenterSettingsArea>? SettingsChanged;

    // ─── Business profile ───

    public async Task<BusinessProfile> GetBusinessProfileAsync() =>
        ControlCenterSettingsCodec.ReadBusinessProfile(await _store.GetAsync(K.BusinessKeys));

    public async Task SaveBusinessProfileAsync(BusinessProfile profile)
    {
        var normalized = BusinessProfileValidator.Normalize(profile);
        ThrowIfInvalid(BusinessProfileValidator.Validate(normalized));
        await SaveAreaAsync(ControlCenterSettingsArea.BusinessProfile, K.BusinessKeys,
            ControlCenterSettingsCodec.WriteBusinessProfile(normalized), "ControlCenter.BusinessProfileUpdated");
    }

    // ─── Sale settings ───

    public async Task<SaleSettings> GetSaleSettingsAsync() =>
        ControlCenterSettingsCodec.ReadSaleSettings(await _store.GetAsync(K.SaleKeys));

    public async Task SaveSaleSettingsAsync(SaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with { DefaultDoctorName = settings.DefaultDoctorName.Trim() };
        ThrowIfInvalid(SaleSettingsRules.Validate(normalized));
        await SaveAreaAsync(ControlCenterSettingsArea.SaleSettings, K.SaleKeys,
            ControlCenterSettingsCodec.WriteSaleSettings(normalized), "ControlCenter.SaleSettingsUpdated");
    }

    // ─── Invoice print settings ───

    public async Task<InvoicePrintSettings> GetInvoicePrintSettingsAsync() =>
        ControlCenterSettingsCodec.ReadInvoicePrintSettings(await _store.GetAsync(K.InvoiceKeys));

    public async Task SaveInvoicePrintSettingsAsync(InvoicePrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with
        {
            TermsText = settings.TermsText.Trim(),
            FooterNote = settings.FooterNote.Trim(),
            ContactNumber = settings.ContactNumber.Trim()
        };
        ThrowIfInvalid(InvoicePrintSettings.Validate(normalized));
        await SaveAreaAsync(ControlCenterSettingsArea.InvoicePrint, K.InvoiceKeys,
            ControlCenterSettingsCodec.WriteInvoicePrintSettings(normalized), "ControlCenter.InvoicePrintSettingsUpdated");
    }

    // ─── General settings ───

    public async Task<GeneralSettings> GetGeneralSettingsAsync() =>
        ControlCenterSettingsCodec.ReadGeneralSettings(await _store.GetAsync(K.GeneralKeys));

    public async Task SaveGeneralSettingsAsync(GeneralSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfInvalid(GeneralSettings.Validate(settings));
        await SaveAreaAsync(ControlCenterSettingsArea.General, K.GeneralKeys,
            ControlCenterSettingsCodec.WriteGeneralSettings(settings), "ControlCenter.GeneralSettingsUpdated");
    }

    // ─── Paper sizes ───

    public async Task<PaperSize?> GetPaperSizeAsync(string templateName)
    {
        if (!ControlCenterSettingsCodec.IsValidTemplateName(templateName)) return null;
        return ControlCenterSettingsCodec.ReadPaperSize(await _store.GetValueAsync(K.PaperSizeKey(templateName)));
    }

    public async Task SavePaperSizesAsync(IReadOnlyDictionary<string, PaperSize> sizesByTemplate)
    {
        ArgumentNullException.ThrowIfNull(sizesByTemplate);
        var errors = new List<SettingsError>();
        foreach (var (name, size) in sizesByTemplate)
        {
            if (!ControlCenterSettingsCodec.IsValidTemplateName(name))
                errors.Add(new SettingsError(name, $"'{name}' is not a valid print template name."));
            if (!Enum.IsDefined(size))
                errors.Add(new SettingsError(name, $"Unknown paper size for {name}."));
        }
        ThrowIfInvalid(errors);

        var values = sizesByTemplate.ToDictionary(p => K.PaperSizeKey(p.Key), p => ControlCenterSettingsCodec.WritePaperSize(p.Value));
        await SaveAreaAsync(ControlCenterSettingsArea.PaperSize, values.Keys.ToList(), values, "ControlCenter.PaperSizesUpdated");
    }

    // ─── Branding images ───

    public Task<BrandingImage?> GetBrandingImageAsync(BrandingImageKind kind) => _store.GetImageAsync(kind);

    public async Task SaveBrandingImageAsync(BrandingImageKind kind, byte[] data)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (BrandingImageRules.Validate(data) is { } error) throw new ControlCenterValidationException(kind.ToString(), error);
        await _store.SaveImageAsync(kind, BrandingImageRules.DetectContentType(data)!, data);
        await _audit.LogAsync("ControlCenter.BrandingImageUpdated", "Settings", null, null,
            $"{{\"image\":\"{kind}\",\"bytes\":{data.Length}}}");
        Raise(ControlCenterSettingsArea.Branding);
    }

    public async Task RemoveBrandingImageAsync(BrandingImageKind kind)
    {
        if (await _store.DeleteImageAsync(kind))
        {
            await _audit.LogAsync("ControlCenter.BrandingImageRemoved", "Settings", null, null, $"{{\"image\":\"{kind}\"}}");
            Raise(ControlCenterSettingsArea.Branding);
        }
    }

    // ─── helpers ───

    private static void ThrowIfInvalid(IReadOnlyList<SettingsError> errors)
    {
        if (errors.Count > 0) throw new ControlCenterValidationException(errors);
    }

    private async Task SaveAreaAsync(ControlCenterSettingsArea area, IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> values, string auditAction)
    {
        var before = await _store.GetAsync(keys);
        await _store.SetManyAsync(values);

        var changedOld = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var changedNew = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            before.TryGetValue(key, out var old);
            if (old == value) continue;
            changedOld[key] = old ?? string.Empty;
            changedNew[key] = value;
        }

        if (changedNew.Count > 0)
        {
            await _audit.LogAsync(auditAction, "Settings", null,
                JsonSerializer.Serialize(changedOld, AuditJson), JsonSerializer.Serialize(changedNew, AuditJson));
            _logger.LogInformation("Control Center {Area} saved ({Count} changed keys)", area, changedNew.Count);
        }
        Raise(area);
    }

    private void Raise(ControlCenterSettingsArea area)
    {
        try
        {
            SettingsChanged?.Invoke(this, area);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not make a successful save look like a failure.
            _logger.LogError(ex, "A SettingsChanged handler failed for {Area}", area);
        }
    }
}
