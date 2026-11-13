using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Dashboard widgets + global search (Ctrl+F) + shortcuts panel (F9). Migration number 20 (overflow 200–209).
/// The "Dashboard" key is the shell's home page (MainViewModel.HomePageKey); keep it.
/// </summary>
public static class DashboardSearchModule
{
    public static IServiceCollection AddDashboardSearchModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        services.AddPage<DashboardViewModel>(navigation, "Dashboard", "Dashboard", NavGroup.Dashboard, "", 10);
        return services;
    }
}
