using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>One column of the supplier file and the purchase field it is mapped to.</summary>
public sealed partial class PurchaseImportColumnViewModel : ObservableObject
{
    public PurchaseImportColumnViewModel(int index, string header, string sample, PurchaseImportField field)
    {
        Index = index;
        Header = string.IsNullOrWhiteSpace(header) ? $"Column {index + 1}" : header;
        Sample = sample;
        _field = field;
    }

    public int Index { get; }
    public string Header { get; }
    public string Sample { get; }

    [ObservableProperty] private PurchaseImportField _field;
}

/// <summary>
/// Import a supplier invoice from CSV / Excel (spec 04 "Import CSV"): choose the distributor, bill no and date, pick
/// the file, check the column mapping (pre-filled from the distributor's saved mapping or the header names, saved for
/// next time: "This is a One-Time Process Only"), preview the rows with their problems, then open the draft on the
/// purchase screen to review and save. Nothing is stored until the purchase is saved there.
/// </summary>
public sealed partial class PurchaseImportViewModel : BaseViewModel
{
    private readonly IPurchaseImportService _import;
    private readonly IDistributorService _distributors;
    private readonly INavigationService _navigation;
    private readonly ILogger<PurchaseImportViewModel> _logger;
    private PurchaseImportTable? _table;

    public PurchaseImportViewModel(IPurchaseImportService import, IDistributorService distributors, INavigationService navigation,
        ILogger<PurchaseImportViewModel> logger)
    {
        _import = import;
        _distributors = distributors;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<Distributor> Suppliers { get; } = new();
    public ObservableCollection<PurchaseImportColumnViewModel> Columns { get; } = new();
    public ObservableCollection<PurchaseImportRow> PreviewRows { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    public IReadOnlyList<PurchaseOption<PurchaseImportField>> FieldOptions { get; } =
        Enum.GetValues<PurchaseImportField>().Select(f => new PurchaseOption<PurchaseImportField>(f, f.ToLabel())).ToList();

    [ObservableProperty] private Distributor? _selectedSupplier;
    [ObservableProperty] private string _invoiceNumber = string.Empty;
    [ObservableProperty] private DateTime? _invoiceDate = DateTime.Today;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private int _validRowCount;
    [ObservableProperty] private int _invalidRowCount;

    public bool HasFile => _table != null;
    public bool HasPreview => PreviewRows.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public string FileSummary => _table == null ? "No file chosen" : $"{System.IO.Path.GetFileName(FilePath)} · {_table.Rows.Count} rows · {_table.Headers.Count} columns";

    public override async Task LoadAsync()
    {
        try
        {
            var list = await _distributors.SearchAsync(null);
            Suppliers.Clear();
            foreach (var d in list) Suppliers.Add(d);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading distributors failed");
            SetError(PurchaseUi.Message(ex, "load distributors"));
        }
    }

    partial void OnSelectedSupplierChanged(Distributor? value) => _ = ApplyMappingAsync();

    /// <summary>Called by the view after the user picks a file in the Open dialog.</summary>
    public async Task LoadFileAsync(string path)
    {
        IsLoading = true;
        ShowErrors(Array.Empty<string>());
        try
        {
            FilePath = path;
            _table = await _import.ReadFileAsync(path);
            await ApplyMappingAsync();
            SetStatus($"{_table.Rows.Count} rows read. Check that each column is mapped correctly, then press Preview.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading import file failed");
            _table = null;
            Columns.Clear();
            ShowErrors(PurchaseUi.Messages(ex, "read the file"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(FileSummary));
        }
    }

    private async Task ApplyMappingAsync()
    {
        if (_table == null) return;
        try
        {
            var mapping = await _import.GetMappingAsync(SelectedSupplier?.Id ?? 0, _table);
            Columns.Clear();
            for (var i = 0; i < _table.Headers.Count; i++)
            {
                var sample = _table.Rows.Count > 0 ? _table.Cell(0, i) : string.Empty;
                Columns.Add(new PurchaseImportColumnViewModel(i, _table.Headers[i], sample, i < mapping.Count ? mapping[i] : PurchaseImportField.Ignore));
            }
            PreviewRows.Clear();
            OnPropertyChanged(nameof(HasPreview));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading import mapping failed");
            ShowErrors(PurchaseUi.Messages(ex, "map the columns"));
        }
    }

    private IReadOnlyList<PurchaseImportField> CurrentMapping() => Columns.Select(c => c.Field).ToList();

    private IReadOnlyList<string> MappingProblems()
    {
        var used = Columns.Where(c => c.Field != PurchaseImportField.Ignore).GroupBy(c => c.Field).Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key.ToLabel()}' is mapped to more than one column ({string.Join(", ", g.Select(c => c.Header))}).").ToList();
        return used;
    }

    [RelayCommand]
    private void Preview()
    {
        if (_table == null)
        {
            ShowErrors(new[] { "Choose the distributor's CSV or Excel file first." });
            return;
        }
        var problems = MappingProblems();
        if (problems.Count > 0)
        {
            ShowErrors(problems);
            return;
        }
        try
        {
            var rows = _import.Preview(_table, CurrentMapping());
            PreviewRows.Clear();
            foreach (var row in rows) PreviewRows.Add(row);
            ValidRowCount = rows.Count(r => r.IsValid);
            InvalidRowCount = rows.Count - ValidRowCount;
            ShowErrors(Array.Empty<string>());
            SetStatus($"{ValidRowCount} rows ready" + (InvalidRowCount > 0 ? $", {InvalidRowCount} with problems (they will be skipped)." : "."));
        }
        catch (Exception ex)
        {
            ShowErrors(PurchaseUi.Messages(ex, "preview the rows"));
        }
        OnPropertyChanged(nameof(HasPreview));
    }

    /// <summary>Builds the draft (items matched by barcode, then name) and opens it on the purchase screen.</summary>
    [RelayCommand]
    private async Task CreateDraftAsync()
    {
        var errors = new List<string>();
        if (SelectedSupplier == null) errors.Add("Please add Distributor");
        if (string.IsNullOrWhiteSpace(InvoiceNumber)) errors.Add("Please add Invoice No");
        if (InvoiceDate == null) errors.Add("Please add Invoice Date");
        if (_table == null) errors.Add("Choose the distributor's CSV or Excel file first.");
        errors.AddRange(MappingProblems());
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        IsLoading = true;
        try
        {
            var source = FilePath is { } p && p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV"
                : FilePath is { } x && x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "CSV" : "Excel";
            var result = await _import.BuildDraftAsync(SelectedSupplier!.Id, InvoiceNumber.Trim(), InvoiceDate!.Value, _table!, CurrentMapping(), source);
            if (result.ImportedCount == 0)
            {
                ShowErrors(new[] { "No row could be imported. Fix the mapping or the item names (see the preview)." }
                    .Concat(result.Skipped.Take(10).Select(r => $"Row {r.RowNumber}: {string.Join(" ", r.Errors)}")).ToList());
                PreviewRows.Clear();
                foreach (var row in result.Skipped) PreviewRows.Add(row);
                OnPropertyChanged(nameof(HasPreview));
                return;
            }
            _navigation.NavigateTo(PurchasePages.Entry, new ImportedPurchaseDraftRequest(result.Draft, result.Skipped));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building import draft failed");
            ShowErrors(PurchaseUi.Messages(ex, "import the bill"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowErrors(IEnumerable<string> messages)
    {
        Errors.Clear();
        foreach (var message in messages) Errors.Add(message);
        OnPropertyChanged(nameof(HasErrors));
        if (Errors.Count > 0) ClearStatus();
    }
}
