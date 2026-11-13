using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>Line entry: product search (name / composition / barcode), batch popup, scans, bill discount, custom charge.</summary>
public sealed partial class BillingViewModel
{
    private CancellationTokenSource? _searchCts;
    private SaleCatalogItem? _batchItem;
    private BillLineViewModel? _batchTarget;
    private bool _syncingDiscount;

    public ObservableCollection<SaleCatalogItem> ProductResults { get; } = new();
    public ObservableCollection<CustomChargeOption> CustomChargeOptions { get; } = new();

    [ObservableProperty] private string _productSearchText = string.Empty;
    [ObservableProperty] private SaleCatalogItem? _selectedProduct;
    [ObservableProperty] private bool _isProductDropdownOpen;
    [ObservableProperty] private string? _productSearchMessage;
    /// <summary>ADD SCAN QTY: a scan adds a pack (true) or one unit (false).</summary>
    [ObservableProperty] private bool _scanInPacks = true;
    /// <summary>BATCH ORDER: LAST IN, FIRST OUT instead of the default first expiry first out.</summary>
    [ObservableProperty] private bool _batchOrderLifo;

    [ObservableProperty] private decimal? _billDiscountPercent;
    [ObservableProperty] private decimal? _billDiscountAmount;
    [ObservableProperty] private string? _customChargeName;
    [ObservableProperty] private decimal _customChargeAmount;

    // ───────────────────────── Product search ─────────────────────────

    partial void OnProductSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        var text = value?.Trim() ?? string.Empty;
        if (text.Length < BillingCalculator.MinSearchLength || BillingCalculator.LooksLikeBarcode(text))
        {
            ProductResults.Clear();
            IsProductDropdownOpen = false;
            ProductSearchMessage = text.Length is > 0 and < BillingCalculator.MinSearchLength ? "Type Min 3 Characters" : null;
            return;
        }
        _searchCts = new CancellationTokenSource();
        _ = SearchProductsAsync(text, _searchCts.Token);
    }

    private async Task SearchProductsAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            var results = await _billing.SearchItemsAsync(text);
            token.ThrowIfCancellationRequested();
            ProductResults.Clear();
            foreach (var item in results) ProductResults.Add(item);
            SelectedProduct = ProductResults.FirstOrDefault();
            IsProductDropdownOpen = ProductResults.Count > 0;
            ProductSearchMessage = ProductResults.Count == 0 ? $"No product matches '{text}'" : null;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Product search failed for '{Text}'", text);
            ProductSearchMessage = $"Search failed: {ex.Message}";
        }
    }

    /// <summary>Enter in the product box: a barcode adds the item at once, otherwise the highlighted result is picked.</summary>
    [RelayCommand]
    private async Task SubmitProductAsync()
    {
        if (!IsEditable) return;
        var text = ProductSearchText?.Trim() ?? string.Empty;
        if (BillingCalculator.LooksLikeBarcode(text))
        {
            await AddScannedAsync(text);
            return;
        }
        if (SelectedProduct is { } product) await PickProductAsync(product);
    }

    [RelayCommand]
    private void MoveProductSelection(string? direction)
    {
        if (ProductResults.Count == 0) return;
        var index = SelectedProduct is null ? -1 : ProductResults.IndexOf(SelectedProduct);
        index = direction == "Up" ? Math.Max(index - 1, 0) : Math.Min(index + 1, ProductResults.Count - 1);
        SelectedProduct = ProductResults[index];
        IsProductDropdownOpen = true;
    }

    [RelayCommand]
    private void CloseProductDropdown() => IsProductDropdownOpen = false;

    [RelayCommand]
    private async Task PickProductAsync(SaleCatalogItem? item)
    {
        item ??= SelectedProduct;
        if (item is null || !IsEditable) return;
        IsProductDropdownOpen = false;
        await OpenBatchPickerAsync(item, null);
    }

    private async Task OpenBatchPickerAsync(SaleCatalogItem item, BillLineViewModel? target)
    {
        try
        {
            var batches = await _billing.GetBatchesAsync(item.MedicineId, BuildRequest().InvoiceDate);
            _batchItem = item;
            _batchTarget = target;
            BatchPicker.Load(item, batches, target?.BatchId);
            IsBatchPickerOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading batches of medicine {MedicineId} failed", item.MedicineId);
            SetError($"Batches of {item.Name} could not be loaded: {ex.Message}");
        }
    }

    private void OnBatchPicked(object? sender, SaleBatchOption batch)
    {
        var item = _batchItem;
        var target = _batchTarget;
        CloseBatchPicker();
        if (item is null) return;

        if (target is not null)
        {
            // BATCH cell re-opened: same quantities and discount on the new batch.
            var replaced = BillingCalculator.CreateLine(item, batch, CustomerDiscount(), target.Packs, target.Loose, _settings.DefaultDiscountPercent)
                with { FreeUnits = target.FreeUnits, DiscountPercent = target.DiscountPercent };
            target.ReplaceLine(replaced, keepQuantities: false);
            SelectedLine = target;
            RequestFocus(FocusTargets.Packs);
            return;
        }

        var existing = Lines.FirstOrDefault(l => l.BatchId == batch.BatchId);
        if (existing is not null)
        {
            SelectedLine = existing;
            SetStatus($"{item.Name} batch {batch.BatchNumber} is already on the bill (row {existing.RowNumber}); change its quantity there.");
            RequestFocus(FocusTargets.Packs);
            return;
        }

        AddLine(item, batch, 1, 0);
        RequestFocus(FocusTargets.Packs);
    }

    private void CloseBatchPicker()
    {
        IsBatchPickerOpen = false;
        _batchItem = null;
        _batchTarget = null;
    }

    /// <summary>Re-opens the batch popup for a line (BATCH cell / Alt+B).</summary>
    [RelayCommand]
    private async Task ChangeBatchAsync(BillLineViewModel? line)
    {
        line ??= SelectedLine;
        if (line is null || !IsEditable) return;
        try
        {
            var match = await _billing.GetItemAsync(line.MedicineId);
            if (match is null)
            {
                SetError($"{line.MedicineName} is no longer active in the item master.");
                return;
            }
            await OpenBatchPickerAsync(match, line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing batch of {Medicine} failed", line.MedicineName);
            SetError($"Batches could not be loaded: {ex.Message}");
        }
    }

    // ───────────────────────── Barcode scans ─────────────────────────

    private async Task AddScannedAsync(string code)
    {
        try
        {
            var item = await _billing.FindByCodeAsync(code);
            ProductSearchText = string.Empty;
            if (item is null)
            {
                ProductSearchMessage = $"No item has barcode / SKU '{code}'";
                return;
            }

            var existing = Lines.FirstOrDefault(l => l.MedicineId == item.MedicineId);
            if (existing is not null)
            {
                var scanned = BillingCalculator.AddScan(existing.Line, ScanInPacks);
                existing.Packs = scanned.Packs;
                existing.Loose = scanned.Loose;
                SelectedLine = existing;
                return;
            }

            var batches = await _billing.GetBatchesAsync(item.MedicineId, BuildRequest().InvoiceDate);
            var batch = BillingCalculator.PickDefaultBatch(batches, _settings.BatchOrder);
            if (batch is null)
            {
                ProductSearchMessage = $"{item.Name} has no sellable batch (out of stock or expired)";
                return;
            }
            var (packs, loose) = BillingCalculator.FirstScanQuantity(item, batch, ScanInPacks);
            AddLine(item, batch, packs, loose);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Barcode scan '{Code}' failed", code);
            ProductSearchMessage = $"Scan failed: {ex.Message}";
        }
    }

    // ───────────────────────── Lines ─────────────────────────

    private decimal CustomerDiscount() => Customer?.DefaultDiscountPercent ?? 0m;

    private void AddLine(SaleCatalogItem item, SaleBatchOption batch, long packs, long loose)
    {
        var line = BillingCalculator.CreateLine(item, batch, CustomerDiscount(), packs, loose, _settings.DefaultDiscountPercent);
        var vm = AddLineViewModel(line);
        SelectedLine = vm;
        ProductSearchText = string.Empty;
        ProductResults.Clear();
        IsProductDropdownOpen = false;
        if (batch.IsNearExpiry) SetStatus($"{item.Name} batch {batch.BatchNumber} is near expiry ({batch.ExpiryHint}).");
        Recompute();
    }

    private BillLineViewModel AddLineViewModel(BillLine line)
    {
        var vm = new BillLineViewModel(line) { IsReadOnly = IsViewMode, RowNumber = Lines.Count + 1 };
        vm.Changed += OnLineChanged;
        Lines.Add(vm);
        return vm;
    }

    [RelayCommand]
    private void RemoveLine(BillLineViewModel? line)
    {
        if (IsViewMode)
        {
            Delete(); // Del on the grid of a saved sale means "delete this sale" (spec 02 view mode)
            return;
        }
        line ??= SelectedLine;
        if (line is null || !IsEditable) return;
        var index = Lines.IndexOf(line);
        line.Changed -= OnLineChanged;
        Lines.Remove(line);
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
        if (Lines.Count == 0) AppliedBillDiscountPaisa = 0;
        Recompute();
        if (Lines.Count == 0) RequestFocus(FocusTargets.Product);
    }

    partial void OnBatchOrderLifoChanged(bool value)
    {
        var order = value ? BatchPickOrder.Lifo : BatchPickOrder.Fefo;
        if (_settings.BatchOrder == order) return;
        _settings = _settings with { BatchOrder = order };
        _ = PersistBatchOrderAsync(order);
    }

    private async Task PersistBatchOrderAsync(BatchPickOrder order)
    {
        try
        {
            await _billing.SaveBatchOrderAsync(order);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the batch order failed");
        }
    }

    // ───────────────────────── Bill discount (Ctrl+B) ─────────────────────────

    /// <summary>Base of the % ↔ ₹ conversion: the MRP value of the lines (spec 02 §I).</summary>
    private long DiscountBasePaisa => Totals.MrpValuePaisa > 0 ? Totals.MrpValuePaisa : Totals.GrossPaisa;

    partial void OnBillDiscountPercentChanged(decimal? value)
    {
        if (_syncingDiscount) return;
        _syncingDiscount = true;
        BillDiscountAmount = value is > 0 ? BillingCalculator.BillDiscountAmountFromPercent(value.Value, DiscountBasePaisa) / 100m : null;
        _syncingDiscount = false;
    }

    partial void OnBillDiscountAmountChanged(decimal? value)
    {
        if (_syncingDiscount) return;
        _syncingDiscount = true;
        BillDiscountPercent = value is > 0
            ? BillingCalculator.BillDiscountPercentFromAmount(PricingCalculator.RoundPaisa(value.Value * 100m), DiscountBasePaisa)
            : null;
        _syncingDiscount = false;
    }

    /// <summary>
    /// APPLY NOW. A percentage is added to each line's own DISC % (clamped to Sale Settings and item caps, Localwell
    /// behaviour); a rupee value is spread over the lines pro rata as a bill discount. The inputs then clear.
    /// </summary>
    [RelayCommand]
    private void ApplyBillDiscount(string? kind)
    {
        if (!IsEditable || Lines.Count == 0) return;
        var percent = BillDiscountPercent ?? 0m;
        var amount = PricingCalculator.RoundPaisa((BillDiscountAmount ?? 0m) * 100m);
        if (kind == "Amount")
        {
            if (amount < 0) { SetError("Discount amount cannot be negative"); return; }
            var subtotal = Lines.Sum(l => l.AmountPaisa) + AppliedBillDiscountPaisa;
            if (amount > subtotal) { SetError("Discount amount cannot exceed the subtotal"); return; }
            AppliedBillDiscountPaisa = amount;
        }
        else
        {
            if (percent < 0m) { SetError("Discount cannot be negative"); return; }
            if (percent > 100m) { SetError("Discount cannot exceed 100%"); return; }
            if (percent < _settings.MinDiscountPercent || percent > _settings.MaxDiscountPercent)
            {
                SetError($"Discount must be between {_settings.MinDiscountPercent:0.##}% and {_settings.MaxDiscountPercent:0.##}%");
                return;
            }
            _suppressRecompute = true;
            try
            {
                foreach (var line in Lines)
                {
                    var max = Math.Min(_settings.MaxDiscountPercent, line.Line.MaxDiscountPercent ?? 100m);
                    line.DiscountPercent = PricingCalculator.CombineBillDiscount(line.DiscountPercent, percent,
                        Math.Min(_settings.MinDiscountPercent, max), max);
                }
            }
            finally
            {
                _suppressRecompute = false;
            }
        }
        _syncingDiscount = true;
        BillDiscountPercent = null;
        BillDiscountAmount = null;
        _syncingDiscount = false;
        ClearError();
        Recompute();
    }

    [RelayCommand]
    private void ClearBillDiscount()
    {
        if (!IsEditable) return;
        AppliedBillDiscountPaisa = 0;
        Recompute();
    }

    // ───────────────────────── Custom charge (Ctrl+E) ─────────────────────────

    partial void OnCustomChargeAmountChanged(decimal value) => Recompute();
    partial void OnCustomChargeNameChanged(string? value) => ScheduleDraftSave();

    [RelayCommand]
    private async Task RemoveChargeOptionAsync(CustomChargeOption? option)
    {
        if (option is null) return;
        try
        {
            await _charges.DeactivateAsync(option.Id);
            CustomChargeOptions.Remove(option);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing custom charge {Name} failed", option.Name);
            SetError($"The charge could not be removed: {ex.Message}");
        }
    }
}
