using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App.Navigation;

public static class NavigationServiceCollectionExtensions
{
    /// <summary>
    /// Registers a page in one call: the ViewModel as transient (a fresh instance per navigation) and its
    /// <see cref="NavItem"/>. Use from a module's AddXxxModule method.
    /// </summary>
    public static IServiceCollection AddPage<TViewModel>(this IServiceCollection services, INavigationRegistry navigation,
        string key, string title, NavGroup group, string iconGlyph, int order,
        string? keyGesture = null, bool showInSidebar = true)
        where TViewModel : BaseViewModel
    {
        navigation.Register(new NavItem(key, title, group, iconGlyph, order, typeof(TViewModel), keyGesture, showInSidebar));
        services.AddTransient<TViewModel>();
        return services;
    }
}
