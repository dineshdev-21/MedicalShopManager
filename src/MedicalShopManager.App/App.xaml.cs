using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Modules;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.App;

/// <summary>
/// Application entry point and DI container host.
/// FROZEN for module agents: modules register themselves in App/Modules/&lt;Name&gt;Module.cs.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global unhandled exception handlers
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Medical Shop Manager starting up...");

        try
        {
            // 1. Initialize SQLite database & migrations
            var db = _serviceProvider.GetRequiredService<DatabaseManager>();
            await db.InitializeAsync();
            _logger.LogInformation("Database initialized successfully at {Path}", db.DatabasePath);

            // 2. Initialize Excel sync workbook if needed
            var excelService = _serviceProvider.GetRequiredService<IExcelSyncService>();
            await excelService.InitializeWorkbookAsync();

            // 3. Set default authenticated user session
            var authService = _serviceProvider.GetRequiredService<IAuthService>();
            var userRepo = _serviceProvider.GetRequiredService<IUserRepository>();
            var adminUser = await userRepo.GetByUsernameAsync("admin");
            if (adminUser != null)
            {
                authService.SetCurrentUser(adminUser);
            }

            // 4. Resolve and show MainWindow
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var mainVm = _serviceProvider.GetRequiredService<MainViewModel>();
            await mainVm.InitializeCommand.ExecuteAsync(null);

            mainWindow.Show();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Fatal error during application startup");
            MessageBox.Show(
                $"Fatal error during application startup:\n\n{ex.Message}\n\nCheck log files for details.",
                "Startup Error - Medical Shop Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ─── Logging ───
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var logDir = Path.Combine(docs, "MedicalShopManager", "Logs");
        Directory.CreateDirectory(logDir);

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // ─── Modules ───
        // Each module registers its own services, ViewModels and pages (App/Modules/<Name>Module.cs).
        // Core runs first so a module can Replace() a shared service. Add a line here only for a brand-new module.
        var navigation = new NavigationRegistry();
        services.AddSingleton<INavigationRegistry>(navigation);

        services
            .AddCoreModule(navigation)
            .AddDashboardSearchModule(navigation)
            .AddBillingModule(navigation)
            .AddSalesReturnsModule(navigation)
            .AddPurchasesModule(navigation)
            .AddStockModule(navigation)
            .AddAuditLabelsModule(navigation)
            .AddOrderbookModule(navigation)
            .AddFinanceModule(navigation)
            .AddDoctorsFollowupsModule(navigation)
            .AddReportsModule(navigation)
            .AddControlCenterModule(navigation);

        // ─── Shell ───
        services.AddSingleton<MainViewModel>();

        // ─── Views ───
        services.AddSingleton<MainWindow>();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI dispatcher exception");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Application Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true; // Prevent crash if recoverable
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Unhandled AppDomain crash");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Medical Shop Manager shutting down...");
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
