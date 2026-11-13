using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Modules.Reports.Printing;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.ViewModels;

/// <summary>
/// The generic report viewer: filters built from the report's definition (date range with presets, "as on" date,
/// number, drop-downs), the result as sheets (tabs) in a sortable grid with a quick search, headline figures, a totals
/// footer and notes. Keys: F5 run, Ctrl+E export to Excel, Ctrl+P print (choose "Microsoft Print to PDF" for a PDF),
/// F2 preview, Ctrl+D favourite, F3 quick search, Esc back to the catalogue.
/// </summary>
public partial class ReportViewerViewModel : BaseViewModel, INavigationAware
{
    private readonly IReportRunner _runner;
    private readonly IReportCatalogService _catalog;
    private readonly IReportExporter _exporter;
    private readonly IReportFileDialog _files;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<ReportPrintModel> _template;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuthService _auth;
    private readonly INavigationService _navigation;
    private readonly ILogger<ReportViewerViewModel> _logger;
    private CancellationTokenSource? _runCts;
    private string? _reportId;

    public ReportViewerViewModel(IReportRunner runner, IReportCatalogService catalog, IReportExporter exporter, IReportFileDialog files,
        IPrintService print, IDocumentTemplate<ReportPrintModel> template, IAppSettingsRepository settings, IAuthService auth,
        INavigationService navigation, ILogger<ReportViewerViewModel> logger)
    {
        _runner = runner;
        _catalog = catalog;
        _exporter = exporter;
        _files = files;
        _print = print;
        _template = template;
        _settings = settings;
        _auth = auth;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<ReportChoiceItem> Choices { get; } = new();
    public ObservableCollection<ReportTable> Sheets { get; } = new();
    public ObservableCollection<ReportFigure> SummaryFigures { get; } = new();
    public ObservableCollection<ReportFigure> TotalFigures { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public IReadOnlyList<ReportPresetItem> Presets { get; } = Enum.GetValues<ReportDatePreset>().Select(p => new ReportPresetItem(p)).ToList();

    [ObservableProperty] private ReportDefinition? _definition;
    [ObservableProperty] private bool _isFavourite;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private DateTime? _asOfDate = DateTime.Today;
    [ObservableProperty] private string _numberText = string.Empty;
    [ObservableProperty] private ReportResult? _result;
    [ObservableProperty] private ReportTable? _selectedSheet;
    [ObservableProperty] private string _quickFilter = string.Empty;
    [ObservableProperty] private DataView? _gridView;
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _emptyMessage = string.Empty;
    [ObservableProperty] private string _rowCountLabel = string.Empty;
    [ObservableProperty] private string? _lastExportPath;

    public string Title => Definition?.Name ?? "Report";
    public string Description => Definition?.Description ?? string.Empty;
    public string CategoryLabel => Definition?.CategoryLabel ?? string.Empty;
    public bool ShowDateRange => Definition?.Parameters.UsesDateRange == true;
    public bool ShowAsOf => Definition?.Parameters.UsesAsOfDate == true;
    public bool ShowNumber => Definition?.Parameters.Number != null;
    public string NumberLabel => Definition?.Parameters.Number?.Label ?? string.Empty;
    public bool HasChoices => Choices.Count > 0;
    public bool HasSeveralSheets => Sheets.Count > 1;
    public bool HasNotes => Notes.Count > 0;
    public string PeriodLabel => Result?.PeriodLabel ?? string.Empty;
    public string StarGlyph => IsFavourite ? "★" : "☆";

    public void OnNavigatedTo(object? parameter)
    {
        _reportId = parameter as string;
    }

    public override async Task LoadAsync()
    {
        var definition = ReportCatalog.Find(_reportId);
        if (definition == null)
        {
            SetError("Choose a report from the Reports catalogue.");
            return;
        }
        Definition = definition;
        ApplyDefaults(definition);
        try
        {
            IsFavourite = (await _catalog.GetFavouritesAsync(_auth.CurrentUser?.Id)).Contains(definition.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read favourites");
        }
        await RunAsync();
    }

    partial void OnDefinitionChanged(ReportDefinition? value)
    {
        foreach (var name in new[] { nameof(Title), nameof(Description), nameof(CategoryLabel), nameof(ShowDateRange), nameof(ShowAsOf),
                     nameof(ShowNumber), nameof(NumberLabel) })
            OnPropertyChanged(name);
    }

    partial void OnIsFavouriteChanged(bool value) => OnPropertyChanged(nameof(StarGlyph));
    partial void OnSelectedSheetChanged(ReportTable? value) => RebuildGrid();
    partial void OnQuickFilterChanged(string value) => RebuildGrid();

    private void ApplyDefaults(ReportDefinition definition)
    {
        var defaults = ReportParameters.DefaultsFor(definition, DateTime.Today);
        FromDate = defaults.From;
        ToDate = defaults.To;
        AsOfDate = defaults.AsOf;
        NumberText = defaults.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Choices.Clear();
        foreach (var choice in definition.Parameters.Choices)
            Choices.Add(new ReportChoiceItem(choice, choice.DefaultKey));
        OnPropertyChanged(nameof(HasChoices));
    }

    /// <summary>Filters from the screen, or null with an error shown when a value is not usable.</summary>
    private ReportParameters? ReadParameters(ReportDefinition definition)
    {
        int? number = null;
        if (definition.Parameters.Number is { } spec)
        {
            if (!int.TryParse(NumberText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                SetError($"{spec.Label}: enter a whole number between {spec.Min} and {spec.Max}.");
                return null;
            }
            number = n;
        }
        if (definition.Parameters.UsesDateRange && (FromDate == null || ToDate == null))
        {
            SetError("Choose both the From and To dates.");
            return null;
        }
        var parameters = new ReportParameters
        {
            From = (FromDate ?? DateTime.Today).Date,
            To = (ToDate ?? DateTime.Today).Date,
            AsOf = (AsOfDate ?? DateTime.Today).Date,
            Number = number,
            Choices = Choices.ToDictionary(c => c.Choice.Key, c => c.Selected.Key)
        };
        var errors = parameters.Validate(definition);
        if (errors.Count > 0)
        {
            SetError(string.Join(" ", errors));
            return null;
        }
        return parameters;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Definition is not { } definition) return;
        var parameters = ReadParameters(definition);
        if (parameters == null) return;

        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        IsLoading = true;
        ClearStatus();
        try
        {
            var result = await _runner.RunAsync(definition.Id, parameters, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            // a newer run replaced this one
        }
        catch (ReportException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report {Report} failed", definition.Id);
            SetError($"Could not prepare the report: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts)) IsLoading = false;
        }
    }

    private void ShowResult(ReportResult result)
    {
        Result = result;
        HasResult = true;
        Sheets.Clear();
        foreach (var sheet in result.Sheets) Sheets.Add(sheet);
        SummaryFigures.Clear();
        foreach (var stat in result.Summary) SummaryFigures.Add(new ReportFigure(stat.Label, stat.Display));
        Notes.Clear();
        foreach (var note in result.Notes) Notes.Add(note);
        OnPropertyChanged(nameof(HasSeveralSheets));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(PeriodLabel));
        SelectedSheet = Sheets.FirstOrDefault(s => s.HasRows) ?? Sheets.FirstOrDefault();
        RebuildGrid();
        SetStatus(result.IsEmpty ? "No records for the selected filters." : $"Prepared {ReportFormat.Date(result.GeneratedAt)} {result.GeneratedAt:HH:mm}.");
    }

    private ReportTable? VisibleSheet() => SelectedSheet?.Filter(QuickFilter);

    private void RebuildGrid()
    {
        var sheet = VisibleSheet();
        GridView = sheet == null ? null : ReportGridBuilder.Build(sheet).DefaultView;
        HasRows = sheet?.HasRows == true;
        TotalFigures.Clear();
        if (sheet != null)
            foreach (var figure in ReportGridBuilder.Totals(sheet)) TotalFigures.Add(figure);
        RowCountLabel = sheet == null ? string.Empty
            : string.IsNullOrWhiteSpace(QuickFilter) ? $"{sheet.Rows.Count} rows" : $"{sheet.Rows.Count} of {SelectedSheet!.Rows.Count} rows";
        EmptyMessage = SelectedSheet?.HasRows == true
            ? $"No rows contain \"{QuickFilter}\"."
            : "No records for the selected filters. Change the dates or options and press F5.";
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(ReportPresetItem? item)
    {
        if (item == null) return;
        (FromDate, ToDate) = ReportDateRanges.For(item.Preset, DateTime.Today);
        await RunAsync();
    }

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        if (Result is not { } result) return;
        var path = _files.AskExcelPath($"{result.Title} {result.PeriodLabel}");
        if (path == null) return;
        try
        {
            IsLoading = true;
            var sheets = result.Sheets.Select(s => s.Filter(QuickFilter)).ToList();
            await _exporter.ExportExcelAsync(result, path, sheets, await _settings.GetValueAsync("ShopName"));
            LastExportPath = path;
            SetStatus($"Exported to {path}.");
        }
        catch (ReportException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel export failed");
            SetError($"Excel export failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenExport()
    {
        if (LastExportPath == null) return;
        try
        {
            _files.Open(LastExportPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening {Path} failed", LastExportPath);
            SetError("The file was saved but could not be opened. Open it from the folder in Excel.");
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Result is not { } result || VisibleSheet() is not { } sheet) return;
        try
        {
            if (await _print.PrintAsync(_template, new ReportPrintModel(result, sheet)))
                SetStatus("Report sent to the printer. Choose 'Microsoft Print to PDF' in the printer list to save a PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing report failed");
            SetError($"Could not print the report: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (Result is not { } result || VisibleSheet() is not { } sheet) return;
        try
        {
            await _print.PreviewAsync(_template, new ReportPrintModel(result, sheet));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing report failed");
            SetError($"Could not preview the report: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ToggleFavouriteAsync()
    {
        if (Definition is not { } definition) return;
        try
        {
            IsFavourite = await _catalog.SetFavouriteAsync(_auth.CurrentUser?.Id, definition.Id, !IsFavourite);
            SetStatus(IsFavourite ? "Added to favourites." : "Removed from favourites.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling favourite failed");
            SetError("Could not update favourites.");
        }
    }

    [RelayCommand]
    private void NextSheet()
    {
        if (Sheets.Count < 2 || SelectedSheet == null) return;
        SelectedSheet = Sheets[(Sheets.IndexOf(SelectedSheet) + 1) % Sheets.Count];
    }

    [RelayCommand]
    private void Back()
    {
        _runCts?.Cancel();
        _navigation.NavigateTo(ReportsPages.Catalogue);
    }
}
