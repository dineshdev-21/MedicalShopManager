using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.ViewModels;

/// <summary>
/// Reports ▸ Business Reports (Localwell /reports/business-reports): the catalogue of every report by category, with
/// search (type to filter; Enter opens the selected report) and per-user favourites (Ctrl+D or the star). Opening a
/// report shows it in <see cref="ReportViewerViewModel"/>.
/// </summary>
public partial class ReportsViewModel : BaseViewModel
{
    private readonly IReportCatalogService _catalog;
    private readonly IAuthService _auth;
    private readonly INavigationService _navigation;
    private readonly ILogger<ReportsViewModel> _logger;
    private IReadOnlySet<string> _favourites = new HashSet<string>();

    public ReportsViewModel(IReportCatalogService catalog, IAuthService auth, INavigationService navigation, ILogger<ReportsViewModel> logger)
    {
        _catalog = catalog;
        _auth = auth;
        _navigation = navigation;
        _logger = logger;
        Categories = new[] { ReportCategoryItem.AllReports, ReportCategoryItem.Favourites }
            .Concat(Enum.GetValues<ReportCategory>().Select(c => new ReportCategoryItem(c.ToLabel(), c)))
            .ToList();
        _selectedCategory = Categories[0];
    }

    public IReadOnlyList<ReportCategoryItem> Categories { get; }
    public ObservableCollection<ReportCardItem> Reports { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ReportCategoryItem _selectedCategory;
    [ObservableProperty] private ReportCardItem? _selectedReport;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyMessage = string.Empty;
    [ObservableProperty] private string _countLabel = string.Empty;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _favourites = await _catalog.GetFavouritesAsync(_auth.CurrentUser?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load report favourites");
            SetError("Favourites could not be loaded; all reports are still available.");
        }
        finally
        {
            IsLoading = false;
        }
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedCategoryChanged(ReportCategoryItem value) => Refresh();

    private void Refresh()
    {
        var category = SelectedCategory ?? ReportCategoryItem.AllReports;
        var matches = ReportCatalog.Search(SearchText, category.Category)
            .Where(d => !category.IsFavourites || _favourites.Contains(d.Id))
            .ToList();
        Reports.Clear();
        foreach (var definition in matches)
            Reports.Add(new ReportCardItem(definition, _favourites.Contains(definition.Id)));
        SelectedReport = Reports.FirstOrDefault();
        IsEmpty = Reports.Count == 0;
        EmptyMessage = category.IsFavourites && string.IsNullOrWhiteSpace(SearchText)
            ? "No favourites yet. Press Ctrl+D or click ☆ on a report to keep it here."
            : $"No report matches \"{SearchText}\". Try GST, stock, expiry, profit or register.";
        CountLabel = Reports.Count == 1 ? "1 report" : $"{Reports.Count} reports";
    }

    [RelayCommand]
    private void OpenReport(ReportCardItem? item)
    {
        item ??= SelectedReport;
        if (item == null) return;
        _navigation.NavigateTo(ReportsPages.Viewer, item.Id);
    }

    [RelayCommand]
    private async Task ToggleFavouriteAsync(ReportCardItem? item)
    {
        item ??= SelectedReport;
        if (item == null) return;
        try
        {
            item.IsFavourite = await _catalog.SetFavouriteAsync(_auth.CurrentUser?.Id, item.Id, !item.IsFavourite);
            _favourites = await _catalog.GetFavouritesAsync(_auth.CurrentUser?.Id);
            SetStatus(item.IsFavourite ? $"'{item.Name}' added to favourites." : $"'{item.Name}' removed from favourites.");
            if (SelectedCategory.IsFavourites) Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling favourite {Report} failed", item.Id);
            SetError("Could not update favourites. Please try again.");
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;
}
