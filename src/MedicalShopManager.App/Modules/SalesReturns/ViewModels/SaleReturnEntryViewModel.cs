using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>
/// Sales ▸ Sale Return (Ctrl+Alt+T, Localwell /sale/return-sale). Two ways in:
/// <list type="bullet">
/// <item><b>Against a bill</b>: search the bill (number, customer, mobile), then enter packs + loose to return per line,
/// up to what was sold minus earlier returns; each line is credited at the price actually paid.</item>
/// <item><b>Without a bill</b> (walk-in): pick item and batch, quantity, refund rate (≤ MRP) and discount.</item>
/// </list>
/// Units go back into the same batch (expired batch → expired stock; "damaged" → not restocked). Settlement is a
/// refund now (cash / UPI / card / bank, capped at what was paid on the bill) or credit to the customer. Saving
/// (Alt+S) allocates the return and credit note numbers and posts everything in one transaction; Ctrl+P prints the
/// credit note, F2 previews it, Ctrl+N starts a new return.
/// </summary>
public partial class SaleReturnEntryViewModel : BaseViewModel, INavigationAware
{
    private readonly ISaleReturnService _service;
    private readonly ISalesRegisterService _register;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<SaleReturnDocument> _template;
    private readonly ILogger<SaleReturnEntryViewModel> _logger;
    private readonly Dictionary<long, SaleReturnBatchInfo> _walkInBatches = new();
    private RoundOffMode _roundOff = RoundOffMode.NearestRupee;
    private long? _pendingSaleId;
    private bool _loaded;
    private bool _resetting;

    public SaleReturnEntryViewModel(ISaleReturnService service, ISalesRegisterService register, IAuthService auth, IPrintService print,
        IDocumentTemplate<SaleReturnDocument> template, ILogger<SaleReturnEntryViewModel> logger)
    {
        _service = service;
        _register = register;
        _auth = auth;
        _print = print;
        _template = template;
        _logger = logger;
        _selectedRefundMode = RefundModes[0];
        _selectedRefundMethod = RefundMethods[0];
    }

    public IReadOnlyList<SalesReturnsOption<SaleRefundMode>> RefundModes => SalesReturnsUi.RefundModes;
    public IReadOnlyList<SalesReturnsOption<PaymentMethod>> RefundMethods => SalesReturnsUi.RefundMethods;
    public IReadOnlyList<string> Reasons => SalesReturnsUi.Reasons;

    public ObservableCollection<SalesRegisterRow> BillResults { get; } = new();
    public ObservableCollection<SaleReturnLineViewModel> Lines { get; } = new();
    public ObservableCollection<SaleReturnItemOption> ItemResults { get; } = new();
    public ObservableCollection<SaleReturnBatchInfo> BatchOptions { get; } = new();
    public ObservableCollection<SaleReturnCustomerOption> CustomerResults { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    // ─── Mode and bill ───
    [ObservableProperty] private bool _isAgainstBill = true;
    [ObservableProperty] private string _billSearchText = string.Empty;
    [ObservableProperty] private SalesRegisterRow? _highlightedBill;
    [ObservableProperty] private ReturnableSale? _sale;
    [ObservableProperty] private SaleReturnLineViewModel? _selectedLine;

    // ─── Without a bill ───
    [ObservableProperty] private string _itemSearchText = string.Empty;
    [ObservableProperty] private SaleReturnItemOption? _selectedItem;
    [ObservableProperty] private SaleReturnBatchInfo? _selectedBatch;
    [ObservableProperty] private long _newPacks = 1;
    [ObservableProperty] private long _newLoose;
    [ObservableProperty] private decimal _newRate;
    [ObservableProperty] private decimal _newDiscount;
    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private SaleReturnCustomerOption? _selectedCustomer;

    // ─── Settlement ───
    [ObservableProperty] private DateTime? _returnDate = DateTime.Today;
    [ObservableProperty] private SalesReturnsOption<SaleRefundMode> _selectedRefundMode;
    [ObservableProperty] private SalesReturnsOption<PaymentMethod> _selectedRefundMethod;
    [ObservableProperty] private string _refundReference = string.Empty;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    // ─── Totals ───
    [ObservableProperty] private long _taxablePaisa;
    [ObservableProperty] private long _taxPaisa;
    [ObservableProperty] private long _roundOffPaisa;
    [ObservableProperty] private long _netAmountPaisa;
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private string _nextReturnNumber = string.Empty;
    [ObservableProperty] private string _nextCreditNoteNumber = string.Empty;
    [ObservableProperty] private SaleReturnDocument? _lastPosted;

    /// <summary>"Without bill" radio button (two-way, the inverse of <see cref="IsAgainstBill"/>).</summary>
    public bool IsWalkIn
    {
        get => !IsAgainstBill;
        set => IsAgainstBill = !value;
    }
    public bool HasSale => Sale is not null;
    public bool ShowBillSearch => IsAgainstBill && Sale is null;
    public bool HasBillResults => BillResults.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasLastPosted => LastPosted is not null;
    public bool IsRefundNow => SelectedRefundMode.Value == SaleRefundMode.Cash;
    public bool HasCustomer => IsAgainstBill ? Sale?.HasCustomer == true : SelectedCustomer is not null;
    public bool IsEmpty => !IsLoading && Lines.Count == 0;

    public string SaleSummary => Sale is null ? string.Empty
        : $"{Sale.InvoiceNumber}  ·  {Sale.InvoiceDate:dd-MM-yyyy}  ·  {Sale.CustomerDisplay}" +
          (string.IsNullOrWhiteSpace(Sale.CustomerPhone) ? "" : $"  ·  {Sale.CustomerPhone}");

    public string PaymentSummary => Sale is null ? string.Empty
        : $"Bill ₹{Sale.GrandTotalPaisa / 100m:N2}  ·  paid ₹{Sale.AmountPaidPaisa / 100m:N2}  ·  due ₹{Sale.BalanceDuePaisa / 100m:N2}" +
          (Sale.PreviouslyReturnedPaisa > 0 ? $"  ·  returned earlier ₹{Sale.PreviouslyReturnedPaisa / 100m:N2}" : "") +
          $"  ·  refundable now ₹{SaleReturnCalculator.MaxRefundNow(Sale) / 100m:N2}";

    public string EmptyTitle => IsAgainstBill ? (Sale is null ? "Find the bill" : "Nothing left to return") : "Add the returned items";
    public string EmptyHint => IsAgainstBill
        ? (Sale is null
            ? "Type the bill number, customer name or mobile above and press Enter. Choose 'Without bill' for a walk-in return."
            : "Every item on this bill has already been returned.")
        : "Search the item, pick the batch, enter the quantity and refund rate, then press Enter on ADD.";

    public string WalkInCustomerLabel => SelectedCustomer?.Display ?? "Walk-in customer (refund now only)";

    partial void OnIsAgainstBillChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWalkIn));
        if (!_resetting) ResetEntry(keepMode: true);
    }

    partial void OnSaleChanged(ReturnableSale? value)
    {
        OnPropertyChanged(nameof(HasSale));
        OnPropertyChanged(nameof(ShowBillSearch));
        OnPropertyChanged(nameof(SaleSummary));
        OnPropertyChanged(nameof(PaymentSummary));
        OnPropertyChanged(nameof(HasCustomer));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    partial void OnLastPostedChanged(SaleReturnDocument? value) => OnPropertyChanged(nameof(HasLastPosted));
    partial void OnSelectedRefundModeChanged(SalesReturnsOption<SaleRefundMode> value)
    {
        OnPropertyChanged(nameof(IsRefundNow));
        Recalculate();
    }

    partial void OnSelectedCustomerChanged(SaleReturnCustomerOption? value)
    {
        OnPropertyChanged(nameof(HasCustomer));
        OnPropertyChanged(nameof(WalkInCustomerLabel));
        Recalculate();
    }

    partial void OnSelectedItemChanged(SaleReturnItemOption? value) => _ = LoadBatchesAsync(value);

    partial void OnSelectedBatchChanged(SaleReturnBatchInfo? value)
    {
        if (value is null) return;
        NewRate = value.DefaultRatePaisa / 100m;
        NewDiscount = 0m;
    }

    partial void OnReturnDateChanged(DateTime? value)
    {
        var date = value?.Date ?? DateTime.Today;
        foreach (var line in Lines) line.SetReturnDate(date);
        _ = PeekNumbersAsync();
        Recalculate();
    }

    public void OnNavigatedTo(object? parameter)
    {
        _pendingSaleId = parameter switch
        {
            StartSaleReturnRequest request => request.SaleId,
            long id => id,
            _ => null
        };
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _roundOff = await _service.GetRoundOffAsync();
            _loaded = true;
            await PeekNumbersAsync();
            if (_pendingSaleId is { } saleId)
            {
                _pendingSaleId = null;
                if (!IsAgainstBill) IsAgainstBill = true;
                await LoadSaleAsync(saleId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the sale return screen failed");
            SetError(SalesReturnsUi.Message(ex, "open the sale return screen"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private async Task PeekNumbersAsync()
    {
        try
        {
            var (returnNumber, creditNoteNumber) = await _service.PeekNumbersAsync(ReturnDate ?? DateTime.Today);
            NextReturnNumber = returnNumber;
            NextCreditNoteNumber = creditNoteNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peeking sale return numbers failed");
            NextReturnNumber = NextCreditNoteNumber = "New";
        }
    }

    // ───────────────────────── Against a bill ─────────────────────────

    [RelayCommand]
    private async Task SearchBillsAsync()
    {
        if (string.IsNullOrWhiteSpace(BillSearchText))
        {
            SetError("Type a bill number, customer name or mobile number to search.");
            return;
        }
        IsLoading = true;
        try
        {
            var results = await _register.SearchBillsAsync(BillSearchText.Trim());
            BillResults.Clear();
            foreach (var row in results) BillResults.Add(row);
            HighlightedBill = BillResults.FirstOrDefault();
            OnPropertyChanged(nameof(HasBillResults));
            if (results.Count == 0) SetError($"No active bill matches \"{BillSearchText.Trim()}\".");
            else ClearError();
            if (results.Count == 1) await LoadSaleAsync(results[0].SaleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Searching bills failed");
            SetError(SalesReturnsUi.Message(ex, "search the bills"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task OpenBillAsync(SalesRegisterRow? row)
    {
        row ??= HighlightedBill;
        return row is null ? Task.CompletedTask : LoadSaleAsync(row.SaleId);
    }

    [RelayCommand]
    private void ChangeBill()
    {
        Sale = null;
        ClearLines();
        Recalculate();
    }

    private async Task LoadSaleAsync(long saleId)
    {
        IsLoading = true;
        try
        {
            var sale = await _service.GetReturnableSaleAsync(saleId);
            if (sale is null)
            {
                SetError("That bill was not found.");
                return;
            }
            ClearLines();
            Sale = sale;
            var date = ReturnDate?.Date ?? DateTime.Today;
            foreach (var line in sale.Lines) AddLine(SaleReturnLineViewModel.ForBillLine(line, date));
            SelectedLine = Lines.FirstOrDefault(l => l.CanEdit);
            SelectedRefundMode = RefundModes.First(m => m.Value ==
                (sale.HasCustomer && SaleReturnCalculator.MaxRefundNow(sale) == 0 ? SaleRefundMode.CreditToCustomer : SaleRefundMode.Cash));
            BillResults.Clear();
            OnPropertyChanged(nameof(HasBillResults));
            ShowErrors(Array.Empty<string>());
            if (sale.IsCancelled) SetError($"Bill {sale.InvoiceNumber} is cancelled; nothing can be returned against it.");
            else if (sale.IsFullyReturned) SetError($"Every item on bill {sale.InvoiceNumber} has already been returned.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading bill {SaleId} for return failed", saleId);
            SetError(SalesReturnsUi.Message(ex, "load the bill"));
        }
        finally
        {
            IsLoading = false;
            Recalculate();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    [RelayCommand]
    private void ReturnAll(SaleReturnLineViewModel? line)
    {
        line ??= SelectedLine;
        line?.ReturnAll();
    }

    [RelayCommand]
    private void ReturnWholeBill()
    {
        foreach (var line in Lines) line.ReturnAll();
    }

    [RelayCommand]
    private void ClearQuantities()
    {
        foreach (var line in Lines) line.Clear();
    }

    // ───────────────────────── Without a bill ─────────────────────────

    [RelayCommand]
    private async Task SearchItemsAsync()
    {
        if (string.IsNullOrWhiteSpace(ItemSearchText)) return;
        try
        {
            var items = await _service.SearchItemsAsync(ItemSearchText.Trim());
            ItemResults.Clear();
            foreach (var item in items) ItemResults.Add(item);
            SelectedItem = ItemResults.FirstOrDefault();
            if (items.Count == 0) SetError($"No item matches \"{ItemSearchText.Trim()}\".");
            else ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Item search failed");
            SetError(SalesReturnsUi.Message(ex, "search the items"));
        }
    }

    private async Task LoadBatchesAsync(SaleReturnItemOption? item)
    {
        BatchOptions.Clear();
        SelectedBatch = null;
        if (item is null) return;
        try
        {
            foreach (var batch in await _service.GetItemBatchesAsync(item.MedicineId)) BatchOptions.Add(batch);
            SelectedBatch = BatchOptions.FirstOrDefault();
            if (BatchOptions.Count == 0) SetError($"{item.Name} has no batches, so it cannot be taken back into stock.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading batches of {MedicineId} failed", item.MedicineId);
            SetError(SalesReturnsUi.Message(ex, "load the batches"));
        }
    }

    [RelayCommand]
    private void AddItem()
    {
        var batch = SelectedBatch;
        if (batch is null)
        {
            SetError("Pick the item and its batch first.");
            return;
        }
        if (NewPacks < 0 || NewLoose < 0)
        {
            SetError("Quantities cannot be negative.");
            return;
        }
        var units = NewPacks * Math.Max(batch.UnitsPerPack, 1) + NewLoose;
        if (units <= 0)
        {
            SetError("Enter the quantity returned (packs and / or loose units).");
            return;
        }
        if (NewRate <= 0)
        {
            SetError("Enter the refund rate per pack.");
            return;
        }

        _walkInBatches[batch.BatchId] = batch;
        var existing = Lines.FirstOrDefault(l => l.BatchId == batch.BatchId);
        if (existing is not null)
        {
            var total = Math.Max(existing.Units, 0) + units;
            existing.Packs = total / existing.UnitsPerPack;
            existing.Loose = total % existing.UnitsPerPack;
            existing.RateDisplay = NewRate;
            existing.DiscountPercent = NewDiscount;
            SelectedLine = existing;
        }
        else
        {
            var line = SaleReturnLineViewModel.ForBatch(batch, units, SalesReturnsUi.Paisa(NewRate), NewDiscount, ReturnDate?.Date ?? DateTime.Today);
            AddLine(line);
            SelectedLine = line;
        }
        ClearError();
        NewPacks = 1;
        NewLoose = 0;
        ItemSearchText = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
        Recalculate();
    }

    [RelayCommand]
    private void RemoveLine(SaleReturnLineViewModel? line)
    {
        line ??= SelectedLine;
        if (line is null) return;
        if (!line.IsWalkIn)
        {
            line.Clear();
            return;
        }
        line.PropertyChanged -= OnLineChanged;
        Lines.Remove(line);
        OnPropertyChanged(nameof(IsEmpty));
        Recalculate();
    }

    [RelayCommand]
    private async Task SearchCustomersAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomerSearchText)) return;
        try
        {
            var customers = await _service.SearchCustomersAsync(CustomerSearchText.Trim());
            CustomerResults.Clear();
            foreach (var c in customers) CustomerResults.Add(c);
            SelectedCustomer = CustomerResults.FirstOrDefault();
            if (customers.Count == 0) SetError($"No customer matches \"{CustomerSearchText.Trim()}\". Add customers from Sales ▸ Customers.");
            else ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Customer search failed");
            SetError(SalesReturnsUi.Message(ex, "search the customers"));
        }
    }

    [RelayCommand]
    private void ClearCustomer()
    {
        SelectedCustomer = null;
        CustomerResults.Clear();
        CustomerSearchText = string.Empty;
    }

    // ───────────────────────── Totals and posting ─────────────────────────

    private void AddLine(SaleReturnLineViewModel line)
    {
        line.PropertyChanged += OnLineChanged;
        Lines.Add(line);
    }

    private void ClearLines()
    {
        foreach (var line in Lines) line.PropertyChanged -= OnLineChanged;
        Lines.Clear();
        _walkInBatches.Clear();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaleReturnLineViewModel.Packs) or nameof(SaleReturnLineViewModel.Loose)
            or nameof(SaleReturnLineViewModel.IsDamaged) or nameof(SaleReturnLineViewModel.RateDisplay)
            or nameof(SaleReturnLineViewModel.DiscountPercent))
            Recalculate();
    }

    private SaleReturnRequest BuildRequest() => new()
    {
        SaleId = IsAgainstBill ? Sale?.SaleId : null,
        ReturnDate = ReturnDate?.Date ?? default,
        CustomerId = IsWalkIn ? SelectedCustomer?.Id : null,
        CustomerName = IsWalkIn ? SelectedCustomer?.Name : null,
        CustomerPhone = IsWalkIn ? SelectedCustomer?.Phone : null,
        RefundMode = SelectedRefundMode.Value,
        RefundMethod = SelectedRefundMethod.Value,
        RefundReference = RefundReference,
        Reason = Reason,
        Notes = Notes,
        Lines = Lines.Where(l => l.HasQuantity).Select(l => l.ToRequest()).ToList(),
        UserId = _auth.CurrentUser?.Id
    };

    private (SaleReturnDocument? Document, IReadOnlyList<string> Errors) Recalculate()
    {
        if (!_loaded || _resetting) return (null, Array.Empty<string>());
        try
        {
            var (doc, errors) = _service.Preview(BuildRequest(), IsAgainstBill ? Sale : null, _walkInBatches, _roundOff);
            TaxablePaisa = doc.TaxablePaisa;
            TaxPaisa = doc.TaxPaisa;
            RoundOffPaisa = doc.RoundOffPaisa;
            NetAmountPaisa = doc.NetAmountPaisa;
            LineCount = doc.Lines.Count;
            foreach (var line in Lines)
            {
                var match = doc.Lines.FirstOrDefault(d => line.SaleItemId is { } id ? d.SaleItemId == id : d.BatchId == line.BatchId);
                line.AmountPaisa = match?.LineTotalPaisa ?? 0;
            }
            return (doc, errors);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            TaxablePaisa = TaxPaisa = RoundOffPaisa = NetAmountPaisa = 0;
            LineCount = 0;
            return (null, new[] { ex.Message });
        }
    }

    /// <summary>Save (Alt+S): validate, then post the return and credit note in one transaction.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLoading) return;
        if (IsAgainstBill && Sale is null)
        {
            ShowErrors(new[] { "Find the bill first, or switch to 'Without bill'." });
            return;
        }
        var (_, errors) = Recalculate();
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        IsLoading = true;
        try
        {
            var posted = await _service.PostAsync(BuildRequest());
            LastPosted = posted;
            ResetEntry(keepMode: true);
            await PeekNumbersAsync();
            var settlement = posted.RefundMode == SaleRefundMode.Cash
                ? $"₹{posted.NetAmountPaisa / 100m:N2} refunded ({SalesReturnsLabels.ModeLabel(posted.RefundMethod)})"
                : $"₹{posted.NetAmountPaisa / 100m:N2} credited to {posted.CustomerDisplay}";
            SetStatus($"Sale return {posted.ReturnNumber} / credit note {posted.CreditNoteNumber} saved: {settlement}. Ctrl+P prints it.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Posting sale return failed");
            ShowErrors(SalesReturnsUi.Messages(ex, "save the sale return"));
            if (ex is SaleReturnValidationException && Sale is { } sale) await ReloadSaleKeepingQuantitiesAsync(sale.SaleId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>After a "no longer returnable" refusal, refreshes what can still come back without losing the entry.</summary>
    private async Task ReloadSaleKeepingQuantitiesAsync(long saleId)
    {
        try
        {
            var fresh = await _service.GetReturnableSaleAsync(saleId);
            if (fresh is null) return;
            var entered = Lines.ToDictionary(l => l.SaleItemId ?? -l.BatchId, l => (l.Packs, l.Loose, l.IsDamaged));
            ClearLines();
            Sale = fresh;
            foreach (var line in fresh.Lines)
            {
                var vm = SaleReturnLineViewModel.ForBillLine(line, ReturnDate?.Date ?? DateTime.Today);
                if (entered.TryGetValue(line.SaleItemId, out var q))
                {
                    vm.Packs = q.Packs;
                    vm.Loose = q.Loose;
                    vm.IsDamaged = q.IsDamaged;
                }
                AddLine(vm);
            }
            Recalculate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reloading bill {SaleId} failed", saleId);
        }
    }

    [RelayCommand]
    private async Task PrintLastAsync()
    {
        if (LastPosted is null)
        {
            SetError("Save a return first; earlier credit notes are printed from Invoices ▸ Sale Returns.");
            return;
        }
        try
        {
            if (await _print.PrintAsync(_template, LastPosted))
                SetStatus($"Credit note {LastPosted.CreditNoteNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing credit note failed");
            SetError(SalesReturnsUi.Message(ex, "print the credit note"));
        }
    }

    [RelayCommand]
    private async Task PreviewLastAsync()
    {
        if (LastPosted is null)
        {
            SetError("Save a return first; earlier credit notes are printed from Invoices ▸ Sale Returns.");
            return;
        }
        try
        {
            await _print.PreviewAsync(_template, LastPosted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing credit note failed");
            SetError(SalesReturnsUi.Message(ex, "preview the credit note"));
        }
    }

    /// <summary>Ctrl+N: clears the screen for the next return (keeps the last saved one for printing).</summary>
    [RelayCommand]
    private void NewReturn()
    {
        ResetEntry(keepMode: true);
        ClearStatus();
    }

    private void ResetEntry(bool keepMode)
    {
        _resetting = true;
        try
        {
            if (!keepMode) IsAgainstBill = true;
            ClearLines();
            Sale = null;
            BillResults.Clear();
            BillSearchText = string.Empty;
            ItemResults.Clear();
            BatchOptions.Clear();
            SelectedItem = null;
            SelectedBatch = null;
            ItemSearchText = string.Empty;
            NewPacks = 1;
            NewLoose = 0;
            NewRate = 0;
            NewDiscount = 0;
            SelectedCustomer = null;
            CustomerResults.Clear();
            CustomerSearchText = string.Empty;
            SelectedRefundMode = RefundModes[0];
            SelectedRefundMethod = RefundMethods[0];
            RefundReference = string.Empty;
            Reason = string.Empty;
            Notes = string.Empty;
            TaxablePaisa = TaxPaisa = RoundOffPaisa = NetAmountPaisa = 0;
            LineCount = 0;
            Errors.Clear();
        }
        finally
        {
            _resetting = false;
        }
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasBillResults));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private void ShowErrors(IEnumerable<string> messages)
    {
        Errors.Clear();
        foreach (var message in messages) Errors.Add(message);
        OnPropertyChanged(nameof(HasErrors));
        if (Errors.Count > 0) SetError(Errors.Count == 1 ? Errors[0] : $"Please fix {Errors.Count} problems (listed below).");
        else ClearError();
    }
}
