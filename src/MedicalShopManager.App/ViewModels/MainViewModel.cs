using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Services;
using MedicalShopManager.Core.Interfaces;

namespace MedicalShopManager.App.ViewModels;

/// <summary>
/// Main window ViewModel controlling sidebar navigation and global state.
/// Pages come from <see cref="INavigationRegistry"/>; this class never names a page ViewModel.
/// FROZEN for module agents (docs/ARCHITECTURE-MODULES.md).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Page shown at startup.</summary>
    public const string HomePageKey = "Dashboard";

    private readonly INavigationService _navigationService;
    private readonly IAuthService _authService;
    private readonly INavigationRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private string _currentPage = HomePageKey;

    [ObservableProperty]
    private string _currentUserDisplay = "Administrator";

    [ObservableProperty]
    private string _shopName = "Medical Shop Manager";

    [ObservableProperty]
    private string _syncStatus = "Synced";

    [ObservableProperty]
    private bool _hasPendingSync;

    [ObservableProperty]
    private BaseViewModel? _activeViewModel;

    /// <summary>Sidebar sections built from the registry.</summary>
    public IReadOnlyList<NavGroupViewModel> NavGroups { get; }

    public MainViewModel(
        INavigationService navigationService,
        IAuthService authService,
        INavigationRegistry registry,
        IServiceProvider services,
        ILogger<MainViewModel> logger)
    {
        _navigationService = navigationService;
        _authService = authService;
        _registry = registry;
        _services = services;
        _logger = logger;

        NavGroups = registry.Items
            .Where(i => i.ShowInSidebar)
            .GroupBy(i => i.Group)
            .OrderBy(g => g.Key)
            .Select(g => new NavGroupViewModel(g.Key, g.Select(i => new NavEntryViewModel(i)).ToList()))
            .ToList();

        foreach (var item in registry.Items.Where(i => ViewLocator.ResolveViewType(i.ViewModelType) == null))
            _logger.LogError("Page '{Key}' has no view: expected a view named after {ViewModel} in a .Views namespace", item.Key, item.ViewModelType.Name);

        _navigationService.NavigationChanged += OnNavigationChanged;
    }

    private async void OnNavigationChanged(object? sender, NavigationRequest request)
    {
        var item = _registry.Find(request.PageKey);
        if (item == null)
        {
            _logger.LogWarning("Navigation to unknown page '{Key}' ignored", request.PageKey);
            return;
        }

        CurrentPage = item.Key;
        foreach (var entry in NavGroups.SelectMany(g => g.Entries))
            entry.IsSelected = string.Equals(entry.Key, item.Key, StringComparison.OrdinalIgnoreCase);

        try
        {
            var viewModel = (BaseViewModel)_services.GetRequiredService(item.ViewModelType);
            if (viewModel is INavigationAware aware)
                aware.OnNavigatedTo(request.Parameter);

            ActiveViewModel = viewModel;
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open page '{Key}'", item.Key);
            ActiveViewModel = new PageErrorViewModel(item.Title, ex);
        }
    }

    [RelayCommand]
    private void NavigateTo(string pageName)
    {
        _navigationService.NavigateTo(pageName);
    }

    [RelayCommand]
    private Task InitializeAsync()
    {
        // Load initial page
        _navigationService.NavigateTo(HomePageKey);
        return Task.CompletedTask;
    }
}
