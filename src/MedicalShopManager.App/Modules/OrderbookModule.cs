using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Navigation;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Orderbook: shortbook (min stock + manual), purchase orders per distributor, PO print/export (6.2). Migration number 15 (overflow 150–159). Nav group: Orderbook.
/// Not built yet: the owning module agent fills this in (pages via services.AddPage, services, templates).
/// </summary>
public static class OrderbookModule
{
    public static IServiceCollection AddOrderbookModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        return services;
    }
}
