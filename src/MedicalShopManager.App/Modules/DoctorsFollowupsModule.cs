using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.DoctorsFollowups.Printing;
using MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Doctors master + patient follow-ups / refill reminders (6.2). Migration number 17 (overflow 170–179).
/// Code: Core/Modules/DoctorsFollowups, Infrastructure/Modules/DoctorsFollowups, App/Modules/DoctorsFollowups.
/// Pages: Sales ▸ Doctors, Sales ▸ Doctor-wise Sales, Dashboard ▸ Patient Follow-ups, and the refill rules /
/// follow-up settings screen (F7 from the follow-ups page). Other modules open them with the keys and requests in
/// <see cref="DoctorsFollowupsPages"/> (Core), e.g.
/// <c>NavigateTo(DoctorsFollowupsPages.Followups, new OpenFollowupsRequest(customerId))</c>.
/// For other modules (resolve from DI): <see cref="IDoctorLookup"/> (Billing's doctor field: find / get / quick-add,
/// store the id in Sales.DoctorId) and <see cref="IRefillReminderFeed"/> (dashboard Refill Reminders card).
/// No window-level shortcut is registered (the contract reserves none for this module).
/// </summary>
public static class DoctorsFollowupsModule
{
    public static IServiceCollection AddDoctorsFollowupsModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<DoctorsViewModel>(navigation, DoctorsFollowupsPages.Doctors, "Doctors", NavGroup.Sales, "", 40);
        services.AddPage<DoctorSalesViewModel>(navigation, DoctorsFollowupsPages.DoctorSales, "Doctor-wise Sales", NavGroup.Sales, "", 45);
        services.AddPage<PatientFollowupsViewModel>(navigation, DoctorsFollowupsPages.Followups, "Patient Follow-ups", NavGroup.Dashboard, "", 20);
        services.AddPage<RefillRulesViewModel>(navigation, DoctorsFollowupsPages.RefillRules, "Refill Rules", NavGroup.Dashboard, "", 25,
            showInSidebar: false);

        // ─── Data and services (one instance serves the full interface and the cross-module look-ups) ───
        services.AddSingleton<IDoctorRepository, DoctorRepository>();
        services.AddSingleton<IFollowupRepository, FollowupRepository>();
        services.AddSingleton<IDoctorService, DoctorService>();
        services.AddSingleton<IFollowupService, FollowupService>();
        services.AddSingleton<IDoctorLookup>(provider => provider.GetRequiredService<IDoctorService>());
        services.AddSingleton<IRefillReminderFeed>(provider => provider.GetRequiredService<IFollowupService>());

        // ─── Print templates ───
        services.AddSingleton<IDocumentTemplate<DoctorSalesSummary>, DoctorSalesSummaryTemplate>();
        services.AddSingleton<IDocumentTemplate<RefillReminderList>, RefillReminderListTemplate>();
        return services;
    }
}
