using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.Reports.Printing;
using MedicalShopManager.App.Modules.Reports.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Modules.Reports;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Reports catalogue (6.2): GSTR-1 / GSTR-3B / HSN, sales / purchase / return registers, day book, cash book, profit,
/// stock valuation, expiry, fast / slow / non-moving, Schedule H1 and narcotic / X registers, party outstanding,
/// doctor-wise sales and expenses — Excel export (ClosedXML) and print / PDF through IPrintService.
/// Migration number 18 (overflow 180–189): ReportFavourites + Reports.* settings.
/// Code: Core/Modules/Reports (catalogue, models, pure GST / profit / register builders), Infrastructure/Modules/Reports
/// (read-only report providers, runner, favourites, Excel exporter), App/Modules/Reports.
/// Pages: Reports ▸ Business Reports (catalogue) and the report viewer (not in the sidebar); other modules open a
/// report with <c>NavigateTo("ReportViewer", ReportIds.Gstr1)</c>.
/// </summary>
public static class ReportsModule
{
    public static IServiceCollection AddReportsModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<ReportsViewModel>(navigation, ReportsPages.Catalogue, "Business Reports", NavGroup.Reports, "", 10);
        services.AddPage<ReportViewerViewModel>(navigation, ReportsPages.Viewer, "Report", NavGroup.Reports, "", 20, showInSidebar: false);

        // ─── Report providers (one per group; the runner dispatches by report id) ───
        services.AddSingleton<IReportProvider, SalesReportsProvider>();
        services.AddSingleton<IReportProvider, PurchaseReportsProvider>();
        services.AddSingleton<IReportProvider, GstReportsProvider>();
        services.AddSingleton<IReportProvider, AccountReportsProvider>();
        services.AddSingleton<IReportProvider, ProfitReportsProvider>();
        services.AddSingleton<IReportProvider, StockReportsProvider>();
        services.AddSingleton<IReportProvider, ComplianceReportsProvider>();
        services.AddSingleton<IReportProvider, PartyReportsProvider>();

        // ─── Services ───
        services.AddSingleton<IReportRunner, ReportRunner>();
        services.AddSingleton<IReportFavouritesRepository, ReportFavouritesRepository>();
        services.AddSingleton<IReportCatalogService, ReportCatalogService>();
        services.AddSingleton<IReportExporter, ReportExcelExporter>();
        services.AddSingleton<IReportFileDialog, ReportFileDialog>();

        // ─── Print templates ───
        services.AddSingleton<IDocumentTemplate<ReportPrintModel>, ReportDocumentTemplate>();
        return services;
    }
}
