using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>A file column and the field it maps to (editable in the mapping grid).</summary>
public partial class StockImportColumnMapping : ObservableObject
{
    public int Index { get; init; }
    public string Header { get; init; } = string.Empty;
    public string Sample { get; init; } = string.Empty;
    [ObservableProperty] private StockImportField _field;
}

/// <summary>
/// Upload Stock: choose a CSV (opening stock or item master), check the suggested column mapping, preview every row
/// with its errors, then import the valid rows. Keys: Ctrl+O choose file, F5 preview, Alt+S import.
/// </summary>
public partial class StockImportViewModel : BaseViewModel
{
    /// <summary>Header row of the downloadable template (matches the auto-mapping synonyms).</summary>
    public const string TemplateHeader =
        "Item Name,Manufacturer,Composition,Category,Pack,Units Per Pack,HSN,GST,Schedule,Barcode,Rack,Min Stock,Max Stock,Batch No,Expiry,MRP,Purchase Rate,PTR,Sale Rate,Qty,Loose";

    private readonly IStockImportService _import;
    private string _csvText = string.Empty;
    private StockImportPreview? _preview;

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _showOnlyErrors;
    public ObservableCollection<StockImportColumnMapping> Columns { get; } = new();
    public ObservableCollection<StockImportRow> Rows { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();
    public IReadOnlyList<StockImportField> Fields { get; } = Enum.GetValues<StockImportField>();

    public StockImportViewModel(IStockImportService import) => _import = import;

    public bool HasFile => _csvText.Length > 0;
    public bool HasRows => Rows.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool CanImport => _preview?.CanImport == true;

    partial void OnShowOnlyErrorsChanged(bool value) => ShowRows();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose opening stock / item master CSV",
            Filter = "CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        await LoadFileAsync(dialog.FileName);
    }

    /// <summary>Reads the file and suggests a mapping from its header row.</summary>
    public async Task LoadFileAsync(string path)
    {
        ClearStatus();
        Reset();
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 20 * 1024 * 1024)
            {
                SetError("The file is larger than 20 MB. Split it and import the parts one by one.");
                return;
            }
            _csvText = await File.ReadAllTextAsync(path, Encoding.UTF8);
            FilePath = path;
            var records = StockCsv.Parse(_csvText);
            var sample = records.Count > 1 ? records[1] : Array.Empty<string>();
            foreach (var column in _import.SuggestMapping(_csvText))
            {
                Columns.Add(new StockImportColumnMapping
                {
                    Index = column.Index,
                    Header = column.Header,
                    Sample = column.Index < sample.Length ? sample[column.Index] : string.Empty,
                    Field = column.Field
                });
            }
            OnPropertyChanged(nameof(HasFile));
            if (Columns.Count == 0) SetError("The file is empty.");
            else SetStatus($"{Path.GetFileName(path)}: {records.Count - 1} data row(s). Check the column mapping, then press Preview (F5).");
        }
        catch (IOException ex)
        {
            SetError($"Could not read the file (is it open in Excel?): {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetError($"Could not read the file: {ex.Message}");
        }
    }

    private void Reset()
    {
        _csvText = string.Empty;
        _preview = null;
        Columns.Clear();
        Rows.Clear();
        Messages.Clear();
        SummaryText = string.Empty;
        RaiseState();
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (!HasFile)
        {
            SetError("Choose a CSV file first (Ctrl+O).");
            return;
        }
        IsLoading = true;
        ClearStatus();
        try
        {
            var mapping = Columns.Select(c => new StockImportColumn(c.Index, c.Header, c.Field)).ToList();
            _preview = await _import.PreviewAsync(_csvText, mapping);
            Messages.Clear();
            foreach (var e in _preview.FileErrors) Messages.Add(e);
            ShowRows();
            SummaryText = _preview.FileErrors.Count > 0
                ? "Fix the mapping problems listed below."
                : $"{_preview.ValidCount} valid row(s): {_preview.NewItemCount} new item(s), {_preview.BatchCount} opening batch(es). {_preview.InvalidCount} row(s) with errors will be skipped.";
        }
        catch (Exception ex)
        {
            SetError($"Preview failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            RaiseState();
        }
    }

    private void ShowRows()
    {
        Rows.Clear();
        if (_preview == null) return;
        foreach (var row in _preview.Rows.Where(r => !ShowOnlyErrors || !r.IsValid)) Rows.Add(row);
        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_preview is not { CanImport: true } preview)
        {
            SetError("Preview the file and fix its errors before importing.");
            return;
        }
        IsLoading = true;
        ClearStatus();
        try
        {
            var result = await _import.ImportAsync(preview);
            Messages.Clear();
            foreach (var e in result.Errors) Messages.Add(e);
            _preview = null;
            SetStatus($"Import finished: {result.ItemsCreated} item(s) created, {result.BatchesCreated} batch(es) added, {result.RowsSkipped} row(s) skipped.");
        }
        catch (StockValidationException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            SetError($"Import failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            RaiseState();
        }
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save import template",
            FileName = "stock-import-template.csv",
            Filter = "CSV files (*.csv)|*.csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName,
                TemplateHeader + Environment.NewLine +
                "Dolo 650,Micro Labs,Paracetamol 650mg,Tablets,15's,15,30049099,12,OTC,,A-1,30,150,DL24051,08/27,30.91,22.10,24.50,30.91,10,5" + Environment.NewLine,
                Encoding.UTF8);
            SetStatus($"Template saved to {dialog.FileName}.");
        }
        catch (IOException ex)
        {
            SetError($"Could not save the template: {ex.Message}");
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(CanImport));
    }
}
