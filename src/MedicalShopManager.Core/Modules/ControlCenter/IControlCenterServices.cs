using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>Which settings area changed (for <see cref="IControlCenterSettingsService.SettingsChanged"/>).</summary>
public enum ControlCenterSettingsArea
{
    BusinessProfile = 1,
    SaleSettings = 2,
    InvoicePrint = 3,
    PaperSize = 4,
    Branding = 5,
    General = 6
}

/// <summary>
/// Typed access to every Control Center setting. <b>This is the interface other modules read</b> (resolve it from DI):
/// Billing reads <see cref="GetSaleSettingsAsync"/> (credit default, due days, expiry cut-off, rounding, negative
/// stock, discount bounds) and <see cref="GetInvoicePrintSettingsAsync"/> / <see cref="GetBusinessProfileAsync"/> /
/// <see cref="GetBrandingImageAsync"/> when printing; any module can use <see cref="GetBusinessProfileAsync"/> for
/// the business state code (IGST vs CGST + SGST) and the GST registration type (Tax Invoice vs Bill of Supply).
/// Saves validate (throwing <see cref="ControlCenterValidationException"/>), write all keys of an area in one
/// transaction and record an audit entry. Keys: <see cref="ControlCenterSettingKeys"/>.
/// </summary>
public interface IControlCenterSettingsService
{
    event EventHandler<ControlCenterSettingsArea>? SettingsChanged;

    Task<BusinessProfile> GetBusinessProfileAsync();
    Task SaveBusinessProfileAsync(BusinessProfile profile);

    Task<SaleSettings> GetSaleSettingsAsync();
    Task SaveSaleSettingsAsync(SaleSettings settings);

    Task<InvoicePrintSettings> GetInvoicePrintSettingsAsync();
    Task SaveInvoicePrintSettingsAsync(InvoicePrintSettings settings);

    Task<GeneralSettings> GetGeneralSettingsAsync();
    Task SaveGeneralSettingsAsync(GeneralSettings settings);

    /// <summary>Configured default paper size of a print template (key Print.PaperSize.&lt;name&gt;), null when unset.</summary>
    Task<PaperSize?> GetPaperSizeAsync(string templateName);

    /// <summary>Saves several templates' paper sizes in one transaction.</summary>
    Task SavePaperSizesAsync(IReadOnlyDictionary<string, PaperSize> sizesByTemplate);

    Task<BrandingImage?> GetBrandingImageAsync(BrandingImageKind kind);
    Task SaveBrandingImageAsync(BrandingImageKind kind, byte[] data);
    Task RemoveBrandingImageAsync(BrandingImageKind kind);
}

/// <summary>Prefix &amp; Sequences: read the overview and save a validated change through IDocumentNumberService.</summary>
public interface ISequenceSettingsService
{
    Task<IReadOnlyList<SequenceOverview>> GetOverviewAsync(DateTime? asOf = null);

    /// <summary>Validates with <see cref="SequenceChangeRules"/>, saves, audits. Throws <see cref="ControlCenterValidationException"/>.</summary>
    Task SaveAsync(DocumentSequence proposed, DateTime? asOf = null);
}

/// <summary>
/// Per-user permissions (Localwell Users &amp; Devices matrix). Other modules call <see cref="IsPermittedAsync"/> /
/// <see cref="IsCurrentUserPermittedAsync"/> with a key from <see cref="PermissionCatalog"/> before showing an action.
/// </summary>
public interface IUserPermissionService
{
    Task<IReadOnlyDictionary<string, bool>> GetEffectiveAsync(long userId);
    Task<bool> IsPermittedAsync(long userId, string permissionKey);

    /// <summary>For the signed-in user; false when nobody is signed in.</summary>
    Task<bool> IsCurrentUserPermittedAsync(string permissionKey);

    /// <summary>Stores the desired matrix (only differences from the role default are kept). Administrators cannot be edited.</summary>
    Task SavePermissionsAsync(long userId, IReadOnlyDictionary<string, bool> desired);

    /// <summary>Removes the per-user changes so the role defaults apply again.</summary>
    Task ResetToRoleDefaultsAsync(long userId);
}

/// <summary>User accounts for Control Center ▸ Users &amp; Permissions.</summary>
public interface IUserAdminService
{
    Task<IReadOnlyList<User>> GetUsersAsync();
    Task<User> CreateUserAsync(NewUserRequest request);
    Task UpdateUserAsync(UserUpdateRequest request);
    Task ResetPasswordAsync(long userId, string newPassword);
}
