using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.ControlCenter.Printing;
using MedicalShopManager.App.Modules.ControlCenter.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Control Center (6.2): business profile, sale settings, prefix &amp; sequences (IDocumentNumberService), print
/// layout (settings "Print.PaperSize.&lt;TemplateName&gt;" + invoice print toggles), users &amp; permissions, general
/// settings. Migration number 19 (overflow 190–199). Code: Core/Modules/ControlCenter,
/// Infrastructure/Modules/ControlCenter, App/Modules/ControlCenter.
/// Other modules read settings through <see cref="IControlCenterSettingsService"/> and permissions through
/// <see cref="IUserPermissionService"/> (Core interfaces).
/// </summary>
public static class ControlCenterModule
{
    public static IServiceCollection AddControlCenterModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages (sidebar group Control Center) ───
        services.AddPage<ControlCenterHomeViewModel>(navigation, "ControlCenterHome", "Overview", NavGroup.ControlCenter, "", 5);
        services.AddPage<BusinessProfileViewModel>(navigation, "BusinessProfile", "Business Profile", NavGroup.ControlCenter, "", 10);
        services.AddPage<SaleSettingsViewModel>(navigation, "SaleSettings", "Sale Settings", NavGroup.ControlCenter, "", 20);
        services.AddPage<PrefixSequencesViewModel>(navigation, "PrefixSequences", "Prefix & Sequences", NavGroup.ControlCenter, "", 30);
        services.AddPage<PrintLayoutViewModel>(navigation, "PrintLayout", "Print Layout", NavGroup.ControlCenter, "", 40);
        services.AddPage<UserManagementViewModel>(navigation, "UserManagement", "Users & Permissions", NavGroup.ControlCenter, "", 50);
        services.AddPage<SettingsViewModel>(navigation, "Settings", "General Settings", NavGroup.ControlCenter, "", 60);

        // ─── Settings, numbering, users ───
        services.AddSingleton<ControlCenterSettingsStore>();
        services.AddSingleton<IControlCenterSettingsService, ControlCenterSettingsService>();
        services.AddSingleton<ISequenceSettingsService, SequenceSettingsService>();
        services.AddSingleton<UserPermissionRepository>();
        services.AddSingleton<IUserPermissionService, UserPermissionService>();
        services.AddSingleton<IUserAdminService, UserAdminService>();

        // ─── Print templates of every module (this module registers last, so the list is complete) ───
        var templateTypes = PrintTemplateCatalog.FindTemplateServiceTypes(services);
        services.AddSingleton(sp => new PrintTemplateCatalog(sp, templateTypes));

        return services;
    }
}
