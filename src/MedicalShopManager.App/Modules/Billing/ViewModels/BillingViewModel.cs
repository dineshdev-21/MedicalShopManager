using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Modules.Billing.Printing;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// The POS / "Add Sale" screen (spec 02): keyboard-first line entry with per-pack + loose quantities, customer /
/// patient / doctor / Rx capture, bill discount, custom charge, WITH GST, auto-saved drafts, the payment wizard and
/// the view / edit / delete / replicate / print modes of a saved sale. All money comes from
/// <see cref="ISaleBillingService.Compute"/> (PricingCalculator); this class only holds screen state.
/// Split over three files: this one (state, loading, customer, totals, drafts), .Lines (search, batches, discounts)
/// and .Save (payment, view-mode actions, printing).
/// </summary>
public sealed partial class BillingViewModel : BaseViewModel, INavigationAware
{
    public enum ScreenMode { New, View, Edit }

    private readonly ISaleBillingService _billing;
    private readonly ISaleDraftService _drafts;
    private readonly ICustomChargeRepository _charges;
    private readonly IBillingCustomerService _customers;
    private readonly IDocumentNumberService _numbers;
    private readonly IPrescriptionFileStore _prescriptions;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<SaleInvoicePrintModel> _invoiceTemplate;
    private readonly IControlCenterSettingsService _controlCenter;
    private readonly INavigationService _navigation;
    private readonly IAuthService _auth;
    private readonly ILogger<BillingViewModel> _logger;

    private BillingSettings _settings = new();
    private object? _pendingParameter;
    private long? _saleId;
    private long? _draftId;
    private long? _doctorId;
    private bool _suppressRecompute;
    private CancellationTokenSource? _draftCts;
    private BillingInvoice? _viewInvoice;

    public BillingViewModel(ISaleBillingService billing, ISaleDraftService drafts, ICustomChargeRepository charges,
        IBillingCustomerService customers, IDocumentNumberService numbers, IPrescriptionFileStore prescriptions,
        IPrintService print, IDocumentTemplate<SaleInvoicePrintModel> invoiceTemplate, IControlCenterSettingsService controlCenter,
        INavigationService navigation, IAuthService auth, BillingCustomerPickerViewModel customerPicker,
        BillingDraftsViewModel draftsList, ILogger<BillingViewModel> logger)
    {
        _billing = billing;
        _drafts = drafts;
        _charges = charges;
        _customers = customers;
        _numbers = numbers;
        _prescriptions = prescriptions;
        _print = print;
        _invoiceTemplate = invoiceTemplate;
        _controlCenter = controlCenter;
        _navigation = navigation;
        _auth = auth;
        _logger = logger;

        BatchPicker = new BillingBatchPickerViewModel();
        BatchPicker.Picked += OnBatchPicked;
        BatchPicker.Closed += (_, _) => CloseBatchPicker();

        CustomerPicker = customerPicker;
        CustomerPicker.Selected += (_, customer) =>
        {
            IsCustomerPickerOpen = false;
            ApplyCustomer(customer);
            RequestFocus(FocusTargets.Product);
        };
        CustomerPicker.Closed += (_, _) =>
        {
            IsCustomerPickerOpen = false;
            if (Customer is null) IsCounterSale = true;
        };

        Payment = new BillingPaymentViewModel();
        Payment.Completed += async (_, outcome) => await PostAsync(outcome);
        Payment.Closed += (_, _) => IsPaymentOpen = false;

        DraftsList = draftsList;
        DraftsList.IsHosted = true;
        DraftsList.OpenRequested += async (_, id) =>
        {
            IsDraftsOpen = false;
            await OpenDraftAsync(id);
        };
        DraftsList.Closed += (_, _) => IsDraftsOpen = false;

        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLines));
    }

    /// <summary>Names of the fields the view can focus (<see cref="FocusRequested"/>, Focus command parameter).</summary>
    public static class FocusTargets
    {
        public const string Product = "Product";
        public const string Packs = "Packs";
        public const string BillDiscount = "BillDiscount";
        public const string CustomCharge = "CustomCharge";
        public const string Customer = "Customer";
        public const string Patient = "Patient";
    }

    /// <summary>Asks the view to move keyboard focus (see <see cref="FocusTargets"/>).</summary>
    public event EventHandler<string>? FocusRequested;

    // ───────────────────────── Child dialogs ─────────────────────────

    public BillingBatchPickerViewModel BatchPicker { get; }
    public BillingCustomerPickerViewModel CustomerPicker { get; }
    public BillingPaymentViewModel Payment { get; }
    public BillingDraftsViewModel DraftsList { get; }

    [ObservableProperty] private bool _isBatchPickerOpen;
    [ObservableProperty] private bool _isCustomerPickerOpen;
    [ObservableProperty] private bool _isPaymentOpen;
    [ObservableProperty] private bool _isDraftsOpen;
    [ObservableProperty] private bool _isDeleteConfirmOpen;
    [ObservableProperty] private string _deleteReason = string.Empty;

    public bool IsAnyDialogOpen => IsBatchPickerOpen || IsCustomerPickerOpen || IsPaymentOpen || IsDraftsOpen || IsDeleteConfirmOpen;

    // ───────────────────────── Header ─────────────────────────

    [ObservableProperty] private ScreenMode _mode = ScreenMode.New;
    [ObservableProperty] private string _documentNumber = string.Empty;
    [ObservableProperty] private DateTime? _invoiceDate = DateTime.Today;
    [ObservableProperty] private bool _isCounterSale = true;
    [ObservableProperty] private BillingCustomer? _customer;
    [ObservableProperty] private string? _patientName;
    [ObservableProperty] private string? _patientAge;
    [ObservableProperty] private PersonGender _patientGender;
    [ObservableProperty] private string? _patientAddress;
    [ObservableProperty] private string? _doctorName;
    [ObservableProperty] private string? _doctorRegNo;
    [ObservableProperty] private string? _prescriptionNumber;
    [ObservableProperty] private string? _prescriptionFilePath;
    [ObservableProperty] private bool _withGst = true;
    [ObservableProperty] private string? _notes;

    public IReadOnlyList<PersonGender> Genders { get; } = new[] { PersonGender.Unspecified, PersonGender.Male, PersonGender.Female, PersonGender.Other };

    public bool IsNewMode => Mode == ScreenMode.New;
    public bool IsViewMode => Mode == ScreenMode.View;
    public bool IsEditMode => Mode == ScreenMode.Edit;
    public bool IsEditable => Mode != ScreenMode.View;
    public bool IsCancelled => _viewInvoice?.Status == SaleStatus.Cancelled;
    public string Title => Mode switch
    {
        ScreenMode.View => IsCancelled ? $"View Sale Invoice {DocumentNumber} (Deleted)" : $"View Sale Invoice {DocumentNumber}",
        ScreenMode.Edit => $"Edit Sale Invoice {DocumentNumber}",
        _ => "Create New Sale"
    };
    public string CustomerDisplayName => Customer?.Name ?? (IsCounterSale ? "Cash / Counter Sale" : "Select Customer");
    public string? CustomerChips => Customer is null ? null
        : $"Discount: {Customer.DefaultDiscountPercent:0.##}%   Balance: {Customer.BalanceLabel}" + (Customer.Gstin is { } g ? $"   GSTIN: {g}" : string.Empty);
    public bool ShopChargesGst => _settings.ShopChargesGst;
    public string DocumentKind => _settings.ShopChargesGst && WithGst ? "TAX INVOICE" : "BILL OF SUPPLY";

    // ───────────────────────── Lines and totals ─────────────────────────

    public ObservableCollection<BillLineViewModel> Lines { get; } = new();
    public ObservableCollection<string> ValidationErrors { get; } = new();

    [ObservableProperty] private BillLineViewModel? _selectedLine;
    [ObservableProperty] private BillTotals _totals = new();
    [ObservableProperty] private bool _isInterState;
    [ObservableProperty] private long _appliedBillDiscountPaisa;
    [ObservableProperty] private bool _showProfit;
    [ObservableProperty] private string? _lastSavedMessage;
    [ObservableProperty] private bool _isBillSaved;

    public bool HasLines => Lines.Count > 0;
    public bool HasValidationErrors => ValidationErrors.Count > 0;
    public int ItemCount => Lines.Count;
    public long TotalUnits => Lines.Sum(l => l.StockUnitsSafe);
    public bool HasPrescriptionDrugs => Lines.Any(l => l.IsScheduled);
    public bool HasRegisterDrugs => Lines.Any(l => l.NeedsRegister);
    public bool HasPrescriptionFile => !string.IsNullOrWhiteSpace(PrescriptionFilePath);
    public string PrescriptionPrompt => HasRegisterDrugs
        ? "Invoice has Schedule H1 / X drugs: patient, doctor and Rx number are required for the register"
        : "Invoice has Prescription drugs";
    public long AmountReceivedPaisa => _viewInvoice?.AmountPaidPaisa ?? 0;
    public long AmountDuePaisa => _viewInvoice?.BalanceDuePaisa ?? 0;

    // ───────────────────────── Loading and navigation ─────────────────────────

    public void OnNavigatedTo(object? parameter) => _pendingParameter = parameter;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            _settings = await _billing.GetSettingsAsync();
            ScanInPacks = _settings.BarcodeQtyInPacks;
            BatchOrderLifo = _settings.BatchOrder == BatchPickOrder.Lifo;
            await LoadChargesAsync();

            var parameter = _pendingParameter;
            _pendingParameter = null;
            switch (parameter)
            {
                case OpenSaleRequest open:
                    await OpenSaleAsync(open.SaleId);
                    break;
                case long saleId:
                    await OpenSaleAsync(saleId);
                    break;
                case OpenSaleDraftRequest draft:
                    await OpenDraftAsync(draft.DraftId);
                    break;
                case ReplicateSaleRequest replicate:
                    await ReplicateFromAsync(replicate.SaleId);
                    break;
                default:
                    await StartNewBillAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Billing screen failed to load");
            SetError($"The billing screen could not be loaded: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadChargesAsync()
    {
        CustomChargeOptions.Clear();
        foreach (var option in await _charges.GetActiveAsync()) CustomChargeOptions.Add(option);
    }

    /// <summary>Clears the screen for a fresh bill (Alt+N): next number peeked, today's date, counter sale.</summary>
    [RelayCommand]
    private async Task NewBillAsync()
    {
        if (IsAnyDialogOpen) return;
        await StartNewBillAsync();
        SetStatus("New bill.");
    }

    private async Task StartNewBillAsync()
    {
        _suppressRecompute = true;
        try
        {
            ResetState(ScreenMode.New);
            DoctorName = _settings.DefaultDoctorName;
            _doctorId = _settings.DefaultDoctorId;
        }
        finally
        {
            _suppressRecompute = false;
        }
        await PeekNumberAsync();
        Recompute(saveDraft: false);
        RequestFocus(FocusTargets.Product);
    }

    private void ResetState(ScreenMode mode)
    {
        CancelDraftSave();
        foreach (var line in Lines) line.Changed -= OnLineChanged;
        Lines.Clear();
        ValidationErrors.Clear();
        _saleId = null;
        _draftId = null;
        _doctorId = null;
        _viewInvoice = null;
        Mode = mode;
        InvoiceDate = DateTime.Today;
        IsCounterSale = true;
        Customer = null;
        PatientName = PatientAge = PatientAddress = DoctorName = DoctorRegNo = PrescriptionNumber = PrescriptionFilePath = Notes = null;
        PatientGender = PersonGender.Unspecified;
        WithGst = true;
        AppliedBillDiscountPaisa = 0;
        BillDiscountPercent = null;
        BillDiscountAmount = null;
        CustomChargeName = null;
        CustomChargeAmount = 0m;
        ProductSearchText = string.Empty;
        ProductResults.Clear();
        IsBillSaved = false;
        LastSavedMessage = null;
        ShowProfit = false;
        DeleteReason = string.Empty;
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    private async Task PeekNumberAsync()
    {
        try
        {
            DocumentNumber = await _numbers.PeekNextAsync(DocumentType.Sale, InvoiceDate ?? DateTime.Today);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not peek the next invoice number");
            DocumentNumber = "(auto)";
        }
    }

    partial void OnModeChanged(ScreenMode value)
    {
        foreach (var line in Lines) line.IsReadOnly = value == ScreenMode.View;
        OnPropertyChanged(nameof(IsNewMode));
        OnPropertyChanged(nameof(IsViewMode));
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsCancelled));
    }

    partial void OnDocumentNumberChanged(string value) => OnPropertyChanged(nameof(Title));

    partial void OnInvoiceDateChanged(DateTime? value)
    {
        if (IsNewMode && !_suppressRecompute) _ = PeekNumberAsync();
        Recompute();
    }

    partial void OnWithGstChanged(bool value)
    {
        OnPropertyChanged(nameof(DocumentKind));
        Recompute();
    }

    partial void OnPrescriptionFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPrescriptionFile));
        ScheduleDraftSave();
    }

    partial void OnIsBatchPickerOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsCustomerPickerOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsPaymentOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsDraftsOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsDeleteConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));

    partial void OnPatientNameChanged(string? value) => ScheduleDraftSave();
    partial void OnDoctorNameChanged(string? value) => ScheduleDraftSave();
    partial void OnPrescriptionNumberChanged(string? value) => ScheduleDraftSave();

    // ───────────────────────── Customer ─────────────────────────

    /// <summary>Opens "Select a Customer" (Space / Enter on the customer box, or unticking Cash/Counter Sale).</summary>
    [RelayCommand]
    private async Task SelectCustomerAsync(string? text)
    {
        if (!IsEditable || IsAnyDialogOpen) return;
        IsCustomerPickerOpen = true;
        await CustomerPicker.OpenAsync(text);
    }

    /// <summary>Opens the picker straight on "Add New Customer" (quick add).</summary>
    [RelayCommand]
    private async Task QuickAddCustomerAsync()
    {
        if (!IsEditable || IsAnyDialogOpen) return;
        IsCustomerPickerOpen = true;
        await CustomerPicker.OpenAsync(null);
        CustomerPicker.CreateNewCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearCustomer()
    {
        if (!IsEditable) return;
        Customer = null;
        IsCounterSale = true;
    }

    private void ApplyCustomer(BillingCustomer customer)
    {
        Customer = customer;
        IsCounterSale = false;
        if (string.IsNullOrWhiteSpace(PatientName) && !customer.IsBusiness) PatientName = customer.Name;
        if (string.IsNullOrWhiteSpace(PatientAddress)) PatientAddress = customer.Address;
        if (string.IsNullOrWhiteSpace(PatientAge) && customer.AgeYears is { } age) PatientAge = age.ToString();
        if (PatientGender == PersonGender.Unspecified) PatientGender = customer.Gender;
        if (string.IsNullOrWhiteSpace(DoctorName) && !string.IsNullOrWhiteSpace(customer.DoctorName)) DoctorName = customer.DoctorName;

        // The customer's sales discount applies to lines that have no discount yet (spec 02 §C).
        if (customer.DefaultDiscountPercent > 0)
        {
            foreach (var line in Lines.Where(l => l.DiscountPercent == 0m))
                line.DiscountPercent = PricingCalculator.ClampDiscount(customer.DefaultDiscountPercent, 0m, line.Line.MaxDiscountPercent);
        }
        Recompute();
    }

    partial void OnCustomerChanged(BillingCustomer? value)
    {
        OnPropertyChanged(nameof(CustomerDisplayName));
        OnPropertyChanged(nameof(CustomerChips));
        Recompute();
    }

    partial void OnIsCounterSaleChanged(bool value)
    {
        OnPropertyChanged(nameof(CustomerDisplayName));
        if (_suppressRecompute || !IsEditable) return;
        if (value)
        {
            Customer = null;
        }
        else if (Customer is null)
        {
            _ = SelectCustomerAsync(null);
        }
        ScheduleDraftSave();
    }

    // ───────────────────────── Request and totals ─────────────────────────

    /// <summary>The bill as the services see it.</summary>
    public SalePostingRequest BuildRequest() => new()
    {
        EditingSaleId = IsEditMode ? _saleId : null,
        DraftId = IsNewMode ? _draftId : null,
        InvoiceDate = CombineDateWithNow(InvoiceDate),
        IsCounterSale = IsCounterSale || Customer is null,
        CustomerId = Customer?.Id,
        CustomerName = Customer?.Name,
        CustomerPhone = Customer?.Phone,
        CustomerGstin = Customer?.Gstin,
        PatientName = PatientName,
        PatientAge = PatientAge,
        PatientGender = PatientGender,
        PatientAddress = PatientAddress,
        DoctorId = _doctorId,
        DoctorName = DoctorName,
        DoctorRegNo = DoctorRegNo,
        PrescriptionNumber = PrescriptionNumber,
        PrescriptionFilePath = PrescriptionFilePath,
        Lines = Lines.Select(l => l.Line).ToList(),
        BillDiscountKind = AppliedBillDiscountPaisa > 0 ? BillDiscountKind.Amount : BillDiscountKind.None,
        BillDiscountAmountPaisa = AppliedBillDiscountPaisa,
        CustomChargeName = string.IsNullOrWhiteSpace(CustomChargeName) ? null : CustomChargeName.Trim(),
        CustomChargePaisa = PricingCalculator.RoundPaisa(Math.Max(CustomChargeAmount, 0m) * 100m),
        WithGst = WithGst,
        Notes = Notes,
        UserId = _auth.CurrentUser?.Id
    };

    /// <summary>A back-dated bill keeps the chosen date with the current time; today keeps the current time.</summary>
    private static DateTime CombineDateWithNow(DateTime? date)
    {
        var now = DateTime.Now;
        return date is { } d ? d.Date.Add(now.TimeOfDay) : now;
    }

    private void OnLineChanged(object? sender, EventArgs e) => Recompute();

    /// <summary>Recomputes every line and the totals through the billing service (PricingCalculator).</summary>
    private void Recompute(bool saveDraft = true)
    {
        if (_suppressRecompute || IsViewMode) return;
        var request = BuildRequest();
        var billDate = request.InvoiceDate;
        try
        {
            var bill = _billing.Compute(request, _settings);
            for (var i = 0; i < Lines.Count && i < bill.Lines.Count; i++)
            {
                Lines[i].ApplyComputed(bill.Lines[i]);
                Lines[i].RowNumber = i + 1;
                Lines[i].ExpiryState = BillingCalculator.ExpiryStateOf(Lines[i].Line.ExpiryDate, billDate, _settings.NearExpiryMonths);
            }
            Totals = bill.Totals;
            IsInterState = _billing.IsInterState(_settings, request.CustomerGstin);
            if (HasValidationErrors) RefreshValidation(request);
        }
        catch (BillingValidationException ex)
        {
            ShowValidation(ex.Errors);
        }
        RaiseTotals();
        if (saveDraft) ScheduleDraftSave();
    }

    private void RefreshValidation(SalePostingRequest request) => ShowValidation(_billing.Validate(request, _settings));

    private void ShowValidation(IEnumerable<string> errors)
    {
        ValidationErrors.Clear();
        foreach (var error in errors) ValidationErrors.Add(error);
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(TotalUnits));
        OnPropertyChanged(nameof(HasPrescriptionDrugs));
        OnPropertyChanged(nameof(HasRegisterDrugs));
        OnPropertyChanged(nameof(PrescriptionPrompt));
        OnPropertyChanged(nameof(AmountReceivedPaisa));
        OnPropertyChanged(nameof(AmountDuePaisa));
    }

    [RelayCommand]
    private void ToggleProfit() => ShowProfit = !ShowProfit;

    // ───────────────────────── Drafts (spec 02 §O) ─────────────────────────

    /// <summary>Auto-saves an unsaved new bill one second after the last change; an emptied bill deletes its draft.</summary>
    private void ScheduleDraftSave()
    {
        if (!IsNewMode || _suppressRecompute || IsBillSaved) return;
        CancelDraftSave();
        _draftCts = new CancellationTokenSource();
        _ = SaveDraftAfterDelayAsync(_draftCts.Token);
    }

    private void CancelDraftSave()
    {
        _draftCts?.Cancel();
        _draftCts = null;
    }

    private async Task SaveDraftAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1000, token);
            if (token.IsCancellationRequested || !IsNewMode || IsBillSaved) return;
            if (Lines.Count == 0 && _draftId is null) return;
            _draftId = await _drafts.SaveAsync(BuildRequest(), _draftId, Totals.NetPayablePaisa);
        }
        catch (OperationCanceledException)
        {
            // a newer change will save the draft
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-saving the sale draft failed");
        }
    }

    /// <summary>F8: the unsaved drafts list on top of the billing screen.</summary>
    [RelayCommand]
    private async Task OpenDraftsAsync()
    {
        if (IsAnyDialogOpen) return;
        IsDraftsOpen = true;
        await DraftsList.LoadAsync();
    }

    private async Task OpenDraftAsync(long draftId)
    {
        try
        {
            var request = await _drafts.LoadAsync(draftId);
            if (request is null)
            {
                await StartNewBillAsync();
                SetError("That draft no longer exists (drafts are kept for 7 days).");
                return;
            }
            await PopulateAsync(request, ScreenMode.New);
            _draftId = draftId;
            await PeekNumberAsync();
            SetStatus("Draft restored.");
        }
        catch (BillingValidationException ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Fills the screen from a request (draft, edit, replicate). Lines keep their stored figures.</summary>
    private async Task PopulateAsync(SalePostingRequest request, ScreenMode mode)
    {
        _suppressRecompute = true;
        try
        {
            ResetState(mode);
            _saleId = request.EditingSaleId;
            InvoiceDate = request.InvoiceDate == default ? DateTime.Today : request.InvoiceDate.Date;
            if (request.CustomerId is > 0 && !request.IsCounterSale)
            {
                Customer = await _customers.GetAsync(request.CustomerId.Value) ?? new BillingCustomer
                {
                    Id = request.CustomerId.Value,
                    Name = request.CustomerName ?? "Customer",
                    Phone = request.CustomerPhone,
                    Gstin = request.CustomerGstin
                };
                IsCounterSale = false;
            }
            PatientName = request.PatientName;
            PatientAge = request.PatientAge;
            PatientGender = request.PatientGender;
            PatientAddress = request.PatientAddress;
            _doctorId = request.DoctorId;
            DoctorName = request.DoctorName;
            DoctorRegNo = request.DoctorRegNo;
            PrescriptionNumber = request.PrescriptionNumber;
            PrescriptionFilePath = request.PrescriptionFilePath;
            WithGst = request.WithGst;
            Notes = request.Notes;
            AppliedBillDiscountPaisa = request.BillDiscountKind == BillDiscountKind.Amount ? request.BillDiscountAmountPaisa : 0;
            CustomChargeName = request.CustomChargeName;
            CustomChargeAmount = request.CustomChargePaisa / 100m;
            foreach (var line in request.Lines) AddLineViewModel(line);
            _editTenders = request.Tenders;
            _editWasCredit = request.IsCredit;
            _editDueDate = request.DueDate;
        }
        finally
        {
            _suppressRecompute = false;
        }
        Recompute(saveDraft: false);
    }

    private void RequestFocus(string target) => FocusRequested?.Invoke(this, target);

    /// <summary>Focus shortcuts: Ctrl+B bill discount, Ctrl+E custom charge, F3 product search.</summary>
    [RelayCommand]
    private void Focus(string? target)
    {
        if (IsAnyDialogOpen || string.IsNullOrEmpty(target)) return;
        RequestFocus(target);
    }
}
