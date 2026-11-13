using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// "Add New Purchase" (spec 04 §1, Ctrl+Alt+P), reused for view / edit of an existing purchase (spec:
/// <c>add_purchase_view_mode</c> = add | view | edit). Header (distributor with quick-add, invoice no / date, due date,
/// bill has GST, rates incl. GST), the single entry row plus the lines grid, the totals footer with GST split and
/// round-off, and the payment made with the bill. Save draft keeps stock untouched; Save (Alt+S) confirms and posts stock.
/// All amounts come from <see cref="PurchaseCalculator"/>; nothing is computed here.
/// </summary>
public sealed partial class PurchaseEntryViewModel : BaseViewModel, INavigationAware
{
    private readonly IPurchaseEntryService _entry;
    private readonly IDistributorService _distributors;
    private readonly IMedicineRepository _medicines;
    private readonly IDocumentNumberService _numbers;
    private readonly INavigationService _navigation;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<PurchaseBill> _voucherTemplate;
    private readonly ILogger<PurchaseEntryViewModel> _logger;

    private object? _pendingParameter;
    private PurchaseSettings _settings = PurchaseSettings.Default;
    private PurchaseBill _bill = new();
    private bool _suppressRecalc;

    public PurchaseEntryViewModel(IPurchaseEntryService entry, IDistributorService distributors, IMedicineRepository medicines,
        IDocumentNumberService numbers, INavigationService navigation, IAuthService auth, IPrintService print,
        IDocumentTemplate<PurchaseBill> voucherTemplate, ILogger<PurchaseEntryViewModel> logger)
    {
        _entry = entry;
        _distributors = distributors;
        _medicines = medicines;
        _numbers = numbers;
        _navigation = navigation;
        _auth = auth;
        _print = print;
        _voucherTemplate = voucherTemplate;
        _logger = logger;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PurchaseLineEditorViewModel.MedicineId)) OnPropertyChanged(nameof(CanAddLine));
        };
    }

    public PurchaseLineEditorViewModel Editor { get; } = new();
    public ObservableCollection<PurchaseBillLine> Lines { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public ObservableCollection<PurchaseImportRow> SkippedImportRows { get; } = new();
    public ObservableCollection<PurchasePaymentRecord> Payments { get; } = new();
    public IReadOnlyList<PurchaseOption<PaymentMethod>> PaymentMethods => PurchaseUi.PaymentMethods;

    // ── Document state ──
    [ObservableProperty] private long _purchaseId;
    [ObservableProperty] private string _documentNumber = string.Empty;
    [ObservableProperty] private PurchaseStatus _status = PurchaseStatus.Draft;
    [ObservableProperty] private bool _isEditing = true;
    [ObservableProperty] private string? _importSource;

    // ── Header ──
    [ObservableProperty] private string _invoiceNumber = string.Empty;
    [ObservableProperty] private DateTime? _invoiceDate = DateTime.Today;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private bool _withGst = true;
    [ObservableProperty] private bool _ratesIncludeGst;
    [ObservableProperty] private bool _isInterState;
    [ObservableProperty] private decimal _billDiscountPercent;
    [ObservableProperty] private decimal _otherCharges;
    [ObservableProperty] private string _otherChargesLabel = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    // ── Totals (paisa, from PurchaseCalculator) ──
    [ObservableProperty] private long _subtotalPaisa;
    [ObservableProperty] private long _discountPaisa;
    [ObservableProperty] private long _taxablePaisa;
    [ObservableProperty] private long _cgstPaisa;
    [ObservableProperty] private long _sgstPaisa;
    [ObservableProperty] private long _igstPaisa;
    [ObservableProperty] private long _taxPaisa;
    [ObservableProperty] private long _roundOffPaisa;
    [ObservableProperty] private long _grandTotalPaisa;
    [ObservableProperty] private long _amountPaidPaisa;
    [ObservableProperty] private long _balancePaisa;
    [ObservableProperty] private long _totalUnits;
    [ObservableProperty] private PaymentStatus _paymentStatus = PaymentStatus.Unpaid;

    // ── Payment made with the bill / later ──
    [ObservableProperty] private decimal _paymentAmount;
    [ObservableProperty] private PurchaseOption<PaymentMethod>? _paymentMethod = PurchaseUi.PaymentMethods[0];
    [ObservableProperty] private string _paymentReference = string.Empty;
    [ObservableProperty] private DateTime? _paymentDate = DateTime.Today;
    [ObservableProperty] private bool _isPaymentDialogOpen;

    // ── Dialogs ──
    [ObservableProperty] private bool _isDuplicateDialogOpen;
    [ObservableProperty] private string _duplicateMessage = string.Empty;
    [ObservableProperty] private bool _isDeleteDialogOpen;
    [ObservableProperty] private string _deleteReason = string.Empty;

    [ObservableProperty] private PurchaseBillLine? _selectedLine;

    public bool IsNew => PurchaseId == 0;
    public bool IsConfirmed => Status == PurchaseStatus.Confirmed;
    public bool IsCancelled => Status == PurchaseStatus.Cancelled;
    public bool IsReadOnly => !IsEditing;
    public bool CanEdit => IsConfirmed && !IsEditing;
    public bool CanDelete => !IsNew && !IsCancelled;
    public bool CanSaveDraft => IsEditing && !IsConfirmed;
    public bool CanRecordPayment => IsConfirmed && !IsEditing && BalancePaisa > 0;
    public bool ShowInitialPayment => IsEditing && !IsConfirmed;
    public bool CanAddLine => IsEditing && Editor.HasProduct;
    public bool HasLines => Lines.Count > 0;
    public bool IsEmpty => Lines.Count == 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasSkippedImportRows => SkippedImportRows.Count > 0;
    public bool HasPayments => Payments.Count > 0;

    public string Title => IsNew ? "Add New Purchase"
        : IsCancelled ? $"Purchase {DocumentNumber} (cancelled)"
        : IsConfirmed ? (IsEditing ? $"Edit Purchase {DocumentNumber}" : $"Purchase {DocumentNumber}")
        : "Purchase draft";

    public string StatusLabel => Status switch
    {
        PurchaseStatus.Draft => IsNew ? "New" : "Draft (stock not added yet)",
        PurchaseStatus.Cancelled => "Cancelled",
        _ => PaymentStatus switch { PaymentStatus.Paid => "Paid", PaymentStatus.Partial => "Partly paid", _ => "Credit" }
    };

    public string RoundOffLabel => RoundOffPaisa == 0 ? "0.00" : (RoundOffPaisa > 0 ? "+" : "−") + (Math.Abs(RoundOffPaisa) / 100m).ToString("N2");

    public void OnNavigatedTo(object? parameter) => _pendingParameter = parameter;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            _settings = await _entry.GetSettingsAsync();
            Editor.DefaultSaleDiscountPercent = _settings.DefaultSaleDiscountPercent;
            var parameter = _pendingParameter;
            _pendingParameter = null;
            switch (parameter)
            {
                case OpenPurchaseRequest open:
                    await OpenAsync(open.PurchaseId, open.Edit);
                    break;
                case ImportedPurchaseDraftRequest imported:
                    await ApplyBillAsync(imported.Draft);
                    foreach (var row in imported.Skipped) SkippedImportRows.Add(row);
                    OnPropertyChanged(nameof(HasSkippedImportRows));
                    SetStatus($"{imported.Draft.Lines.Count} row(s) imported from the {imported.Draft.ImportSource} file. Check them, then save."
                              + (imported.Skipped.Count > 0 ? $" {imported.Skipped.Count} row(s) were skipped (listed below)." : ""));
                    break;
                default:
                    await ResetAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the purchase screen failed");
            SetError(PurchaseUi.Message(ex, "open the purchase"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenAsync(long id, bool edit)
    {
        var bill = await _entry.GetAsync(id) ?? throw new PurchaseRuleException($"Purchase #{id} was not found.");
        await ApplyBillAsync(bill);
        IsEditing = bill.Status == PurchaseStatus.Draft || (edit && bill.Status == PurchaseStatus.Confirmed);
        await LoadPaymentsAsync();
        RaiseModeChanged();
    }

    /// <summary>Shows a bill (loaded, imported or new) on the screen.</summary>
    private async Task ApplyBillAsync(PurchaseBill bill)
    {
        _suppressRecalc = true;
        try
        {
            _bill = bill;
            PurchaseId = bill.Id;
            Status = bill.Status;
            ImportSource = bill.ImportSource;
            DocumentNumber = bill.PurchaseNumber ?? (bill.Id > 0 ? $"Draft #{bill.Id}" : await PeekNumberAsync(bill.InvoiceDate));
            InvoiceNumber = bill.SupplierInvoiceNumber;
            InvoiceDate = bill.InvoiceDate == default ? DateTime.Today : bill.InvoiceDate;
            DueDate = bill.DueDate;
            WithGst = bill.WithGst;
            RatesIncludeGst = bill.RatesIncludeGst;
            IsInterState = bill.IsInterState;
            BillDiscountPercent = bill.BillDiscountPercent;
            OtherCharges = PurchaseUi.Rupees(bill.OtherChargesPaisa);
            OtherChargesLabel = bill.OtherChargesLabel ?? string.Empty;
            Notes = bill.Notes ?? string.Empty;
            AmountPaidPaisa = bill.AmountPaidPaisa;
            SelectedDistributor = bill.SupplierId > 0 ? await _distributors.GetAsync(bill.SupplierId) : null;
            DistributorSearchText = SelectedDistributor?.Name ?? bill.SupplierName;
            Lines.Clear();
            foreach (var line in bill.Lines) Lines.Add(line);
            Errors.Clear();
            SkippedImportRows.Clear();
            Payments.Clear();
            Editor.Clear();
            IsEditing = bill.Status != PurchaseStatus.Cancelled && bill.Status != PurchaseStatus.Confirmed;
        }
        finally
        {
            _suppressRecalc = false;
        }
        Recalculate();
        RaiseModeChanged();
    }

    private async Task ResetAsync()
    {
        await ApplyBillAsync(new PurchaseBill { InvoiceDate = DateTime.Today });
        PaymentAmount = 0;
        PaymentReference = string.Empty;
        PaymentDate = DateTime.Today;
        PaymentMethod = PurchaseUi.PaymentMethods[0];
    }

    private async Task<string> PeekNumberAsync(DateTime date)
    {
        try
        {
            return await _numbers.PeekNextAsync(DocumentType.Purchase, date == default ? DateTime.Today : date);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peeking the next purchase number failed");
            return "New";
        }
    }

    private async Task LoadPaymentsAsync()
    {
        Payments.Clear();
        if (PurchaseId > 0 && Status == PurchaseStatus.Confirmed)
        {
            foreach (var payment in await _entry.GetPaymentsAsync(PurchaseId)) Payments.Add(payment);
        }
        OnPropertyChanged(nameof(HasPayments));
    }

    private void RaiseModeChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsNew), nameof(IsConfirmed), nameof(IsCancelled), nameof(IsReadOnly), nameof(CanEdit), nameof(CanDelete),
                     nameof(CanSaveDraft), nameof(CanRecordPayment), nameof(ShowInitialPayment), nameof(CanAddLine), nameof(Title),
                     nameof(StatusLabel), nameof(HasLines), nameof(IsEmpty)
                 })
            OnPropertyChanged(name);
    }

    partial void OnIsEditingChanged(bool value) => RaiseModeChanged();
    partial void OnStatusChanged(PurchaseStatus value) => RaiseModeChanged();
    partial void OnWithGstChanged(bool value) => Recalculate();
    partial void OnRatesIncludeGstChanged(bool value) => Recalculate();
    partial void OnIsInterStateChanged(bool value) => Recalculate();
    partial void OnBillDiscountPercentChanged(decimal value) => Recalculate();
    partial void OnOtherChargesChanged(decimal value) => Recalculate();

    partial void OnInvoiceDateChanged(DateTime? value)
    {
        if (_suppressRecalc || value is null || SelectedDistributor is null || !IsEditing) return;
        DueDate = PurchaseCalculator.DueDate(value.Value, SelectedDistributor.CreditDays);
    }

    /// <summary>The bill as on screen (header + lines), with every computed field filled.</summary>
    private PurchaseBill BuildBill()
    {
        var bill = new PurchaseBill
        {
            Id = PurchaseId,
            PurchaseNumber = _bill.PurchaseNumber,
            Status = Status,
            SupplierId = SelectedDistributor?.Id ?? 0,
            SupplierName = SelectedDistributor?.Name ?? string.Empty,
            SupplierGstin = SelectedDistributor?.Gstin,
            SupplierInvoiceNumber = InvoiceNumber.Trim(),
            InvoiceDate = InvoiceDate?.Date ?? default,
            DueDate = DueDate?.Date,
            WithGst = WithGst,
            RatesIncludeGst = RatesIncludeGst,
            IsInterState = IsInterState,
            BillDiscountPercent = BillDiscountPercent,
            OtherChargesPaisa = PurchaseUi.Paisa(OtherCharges),
            OtherChargesLabel = string.IsNullOrWhiteSpace(OtherChargesLabel) ? null : OtherChargesLabel.Trim(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            ImportSource = ImportSource,
            AmountPaidPaisa = _bill.AmountPaidPaisa,
            CreatedByUserId = _bill.CreatedByUserId,
            CreatedAt = _bill.CreatedAt,
            ConfirmedAt = _bill.ConfirmedAt,
            Lines = Lines.ToList()
        };
        return bill;
    }

    /// <summary>Recomputes every line and the footer (PurchaseCalculator) and refreshes the entry-row preview.</summary>
    private void Recalculate()
    {
        if (_suppressRecalc) return;
        var bill = BuildBill();
        Editor.Context = new PurchaseBill
        {
            WithGst = bill.WithGst, RatesIncludeGst = bill.RatesIncludeGst, IsInterState = bill.IsInterState,
            BillDiscountPercent = bill.BillDiscountPercent is >= 0 and <= 100 ? bill.BillDiscountPercent : 0
        };
        Editor.UpdatePreview();
        try
        {
            if (bill.BillDiscountPercent is < 0 or > 100 || bill.OtherChargesPaisa < 0) return;
            var totals = PurchaseCalculator.Recalculate(bill, _settings.RoundOffToRupee ? RoundOffMode.NearestRupee : RoundOffMode.None);
            SubtotalPaisa = totals.SubtotalPaisa;
            DiscountPaisa = totals.DiscountPaisa;
            TaxablePaisa = totals.TaxablePaisa;
            CgstPaisa = totals.CgstPaisa;
            SgstPaisa = totals.SgstPaisa;
            IgstPaisa = totals.IgstPaisa;
            TaxPaisa = totals.TaxPaisa;
            RoundOffPaisa = totals.RoundOffPaisa;
            GrandTotalPaisa = totals.GrandTotalPaisa;
            TotalUnits = totals.TotalUnits;
            AmountPaidPaisa = bill.AmountPaidPaisa;
            BalancePaisa = bill.BalancePaisa;
            PaymentStatus = bill.PaymentStatus;
            RefreshLines();
        }
        catch (ArgumentException ex)
        {
            // A row with an impossible value (e.g. GST 40 %) stays visible; the save validation names it.
            _logger.LogDebug(ex, "Purchase totals not computed");
        }
        OnPropertyChanged(nameof(RoundOffLabel));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CanRecordPayment));
    }

    /// <summary>The line objects were updated in place by the calculator; re-add them so the grid shows the new values.</summary>
    private void RefreshLines()
    {
        var selected = SelectedLine;
        var items = Lines.ToList();
        Lines.Clear();
        foreach (var line in items) Lines.Add(line);
        SelectedLine = selected;
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ShowErrors(IEnumerable<string> messages)
    {
        Errors.Clear();
        foreach (var message in messages) Errors.Add(message);
        OnPropertyChanged(nameof(HasErrors));
        if (Errors.Count > 0) SetError(Errors.Count == 1 ? Errors[0] : $"Please fix {Errors.Count} problems before saving (listed below).");
    }

    private void ClearErrors()
    {
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));
        ClearError();
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        ClearStatus();
        ClearErrors();
        await ResetAsync();
    }

    [RelayCommand]
    private void BackToList() => _navigation.NavigateTo(PurchasePages.List, SelectedDistributor is null ? null : new PurchaseListRequest(SelectedDistributor.Id));
}
