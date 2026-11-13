using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>Distributor picker with quick-add, and the product search / entry row of the purchase screen.</summary>
public sealed partial class PurchaseEntryViewModel
{
    private CancellationTokenSource? _productSearch;
    private CancellationTokenSource? _distributorSearch;

    // ── Distributor ──
    public ObservableCollection<Distributor> DistributorResults { get; } = new();
    [ObservableProperty] private string _distributorSearchText = string.Empty;
    [ObservableProperty] private Distributor? _selectedDistributor;
    [ObservableProperty] private Distributor? _highlightedDistributor;
    [ObservableProperty] private bool _isDistributorDropdownOpen;
    [ObservableProperty] private bool _isQuickAddOpen;

    /// <summary>Quick-add distributor form (spec 04: distributor can be created inline).</summary>
    public PurchaseDistributorEditorViewModel QuickAdd { get; } = new();

    // ── Product search ──
    public ObservableCollection<Medicine> ProductResults { get; } = new();
    [ObservableProperty] private string _productSearchText = string.Empty;
    [ObservableProperty] private Medicine? _selectedProduct;
    [ObservableProperty] private bool _isProductDropdownOpen;
    [ObservableProperty] private string? _productSearchMessage;

    /// <summary>Raised when focus should move (the view maps "Batch" / "Product" / "Distributor" to its boxes).</summary>
    public event EventHandler<string>? FocusRequested;

    public string DistributorInfo => SelectedDistributor is not { } d ? "Type or press Space to search" : string.Join("  ·  ", new[]
    {
        d.CreditDays > 0 ? $"Credit {d.CreditDays} days" : "No credit period",
        $"Balance ₹{d.OutstandingBalance:N2}",
        d.Overdue > 0 ? $"Overdue ₹{d.Overdue:N2}" : null,
        d.LastPurchaseDate is { } last ? $"Prev: {last:dd-MM-yyyy}" : null,
        string.IsNullOrWhiteSpace(d.Gstin) ? "No GSTIN" : d.Gstin,
        IsInterState ? "IGST (other state)" : null
    }.Where(s => s != null));

    partial void OnDistributorSearchTextChanged(string value)
    {
        if (_suppressRecalc || !IsEditing) return;
        if (SelectedDistributor != null && string.Equals(value, SelectedDistributor.Name, StringComparison.Ordinal)) return;
        _ = SearchDistributorsAsync(value);
    }

    private async Task SearchDistributorsAsync(string text)
    {
        _distributorSearch?.Cancel();
        var cts = _distributorSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            var results = await _distributors.SearchAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
            if (cts.IsCancellationRequested) return;
            DistributorResults.Clear();
            foreach (var d in results.Take(50)) DistributorResults.Add(d);
            HighlightedDistributor = DistributorResults.FirstOrDefault();
            IsDistributorDropdownOpen = DistributorResults.Count > 0;
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Distributor search failed");
            SetError(PurchaseUi.Message(ex, "search distributors"));
        }
    }

    /// <summary>Space on an empty box lists every distributor (spec 04 "Type or Press Space to search").</summary>
    [RelayCommand]
    private Task ShowAllDistributorsAsync() => SearchDistributorsAsync(string.Empty);

    [RelayCommand]
    private void MoveDistributorSelection(string? direction)
    {
        if (DistributorResults.Count == 0) return;
        var index = HighlightedDistributor is null ? -1 : DistributorResults.IndexOf(HighlightedDistributor);
        index = direction == "Up" ? Math.Max(0, index - 1) : Math.Min(DistributorResults.Count - 1, index + 1);
        HighlightedDistributor = DistributorResults[index];
        IsDistributorDropdownOpen = true;
    }

    [RelayCommand]
    private async Task PickDistributorAsync(Distributor? distributor)
    {
        distributor ??= HighlightedDistributor;
        if (distributor == null) return;
        IsDistributorDropdownOpen = false;
        await SelectDistributorAsync(distributor);
        FocusRequested?.Invoke(this, "Invoice");
    }

    private async Task SelectDistributorAsync(Distributor distributor)
    {
        _suppressRecalc = true;
        try
        {
            SelectedDistributor = distributor;
            DistributorSearchText = distributor.Name;
        }
        finally
        {
            _suppressRecalc = false;
        }
        IsInterState = await _entry.IsInterStateAsync(distributor);
        if (InvoiceDate is { } date) DueDate = PurchaseCalculator.DueDate(date, distributor.CreditDays);
        OnPropertyChanged(nameof(DistributorInfo));
    }

    partial void OnSelectedDistributorChanged(Distributor? value) => OnPropertyChanged(nameof(DistributorInfo));

    [RelayCommand]
    private void OpenQuickAdd()
    {
        if (!IsEditing) return;
        QuickAdd.Load(new Distributor { Name = SelectedDistributor is null ? DistributorSearchText.Trim() : string.Empty });
        IsDistributorDropdownOpen = false;
        IsQuickAddOpen = true;
    }

    [RelayCommand]
    private void CloseQuickAdd() => IsQuickAddOpen = false;

    [RelayCommand]
    private async Task SaveQuickAddAsync()
    {
        QuickAdd.Errors.Clear();
        try
        {
            var distributor = QuickAdd.ToDistributor();
            var id = await _distributors.SaveAsync(distributor);
            var saved = await _distributors.GetAsync(id) ?? distributor;
            IsQuickAddOpen = false;
            await SelectDistributorAsync(saved);
            SetStatus($"Distributor '{saved.Name}' added.");
            FocusRequested?.Invoke(this, "Invoice");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick-add distributor failed");
            foreach (var message in PurchaseUi.Messages(ex, "save the distributor")) QuickAdd.Errors.Add(message);
        }
    }

    // ── Products ──

    partial void OnProductSearchTextChanged(string value)
    {
        if (!IsEditing) return;
        _ = SearchProductsAsync(value);
    }

    private async Task SearchProductsAsync(string text)
    {
        _productSearch?.Cancel();
        var cts = _productSearch = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
        {
            ProductResults.Clear();
            IsProductDropdownOpen = false;
            ProductSearchMessage = null;
            return;
        }
        try
        {
            await Task.Delay(200, cts.Token);
            var results = await _medicines.SearchAsync(text.Trim());
            if (cts.IsCancellationRequested) return;
            ProductResults.Clear();
            foreach (var m in results.Where(m => m.Status != MedicineStatus.Discontinued).Take(50)) ProductResults.Add(m);
            SelectedProduct = ProductResults.FirstOrDefault();
            IsProductDropdownOpen = ProductResults.Count > 0;
            ProductSearchMessage = ProductResults.Count == 0 ? $"No item matches '{text.Trim()}'. Add it in Items (Alt+I) first." : null;
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Product search failed");
            ProductSearchMessage = PurchaseUi.Message(ex, "search items");
        }
    }

    [RelayCommand]
    private void MoveProductSelection(string? direction)
    {
        if (ProductResults.Count == 0) return;
        var index = SelectedProduct is null ? -1 : ProductResults.IndexOf(SelectedProduct);
        index = direction == "Up" ? Math.Max(0, index - 1) : Math.Min(ProductResults.Count - 1, index + 1);
        SelectedProduct = ProductResults[index];
        IsProductDropdownOpen = true;
    }

    /// <summary>Enter in the product box: a scanned barcode picks its item directly, else the highlighted result.</summary>
    [RelayCommand]
    private async Task SubmitProductAsync()
    {
        if (!IsEditing) return;
        try
        {
            var text = ProductSearchText.Trim();
            Medicine? medicine = null;
            if (text.Length > 0 && text.All(char.IsLetterOrDigit) && text.Any(char.IsDigit))
                medicine = await _medicines.GetByBarcodeAsync(text);
            medicine ??= SelectedProduct;
            if (medicine == null)
            {
                ProductSearchMessage = text.Length == 0 ? "Type a product name or scan its barcode." : $"No item matches '{text}'.";
                return;
            }
            await PickProductAsync(medicine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Picking a product failed");
            SetError(PurchaseUi.Message(ex, "add the item"));
        }
    }

    [RelayCommand]
    private async Task PickProductAsync(Medicine? medicine)
    {
        if (medicine == null || !IsEditing) return;
        IsProductDropdownOpen = false;
        var template = await _entry.CreateLineAsync(medicine);
        Editor.Start(template);
        ProductSearchText = string.Empty;
        ProductResults.Clear();
        ProductSearchMessage = null;
        OnPropertyChanged(nameof(CanAddLine));
        FocusRequested?.Invoke(this, "Batch");
    }

    /// <summary>ADD: puts the entry row on the grid (or replaces the row being edited).</summary>
    [RelayCommand]
    private void AddLine()
    {
        if (!IsEditing) return;
        var (line, errors) = Editor.TryBuildLine();
        if (line == null)
        {
            ShowErrors(errors);
            return;
        }

        var duplicateIndex = Lines.ToList().FindIndex(l => l.MedicineId == line.MedicineId
            && string.Equals(l.BatchNumber, line.BatchNumber, StringComparison.OrdinalIgnoreCase)
            && l.ExpiryDate.Year == line.ExpiryDate.Year && l.ExpiryDate.Month == line.ExpiryDate.Month);
        if (duplicateIndex >= 0 && duplicateIndex != Editor.EditingIndex)
        {
            ShowErrors(new[] { $"Row {duplicateIndex + 1} already has {line.MedicineName} batch {line.BatchNumber}. Edit that row instead." });
            return;
        }

        if (Editor.IsEditingExisting && Editor.EditingIndex < Lines.Count) Lines[Editor.EditingIndex] = line;
        else Lines.Add(line);

        ClearErrors();
        Editor.Clear();
        Recalculate();
        OnPropertyChanged(nameof(CanAddLine));
        FocusRequested?.Invoke(this, "Product");
    }

    [RelayCommand]
    private void EditLine(PurchaseBillLine? line)
    {
        line ??= SelectedLine;
        if (line == null || !IsEditing) return;
        Editor.Load(line, Lines.IndexOf(line));
        OnPropertyChanged(nameof(CanAddLine));
        FocusRequested?.Invoke(this, "Batch");
    }

    [RelayCommand]
    private void RemoveLine(PurchaseBillLine? line)
    {
        line ??= SelectedLine;
        if (line == null || !IsEditing) return;
        var index = Lines.IndexOf(line);
        Lines.Remove(line);
        if (Editor.EditingIndex == index) Editor.Clear();
        else if (Editor.EditingIndex > index) Editor.EditingIndex--;
        Recalculate();
    }

    [RelayCommand]
    private void CancelLineEdit()
    {
        Editor.Clear();
        OnPropertyChanged(nameof(CanAddLine));
        FocusRequested?.Invoke(this, "Product");
    }
}
