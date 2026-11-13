using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>
/// Sales ▸ Doctor-wise Sales: bills, patients, sales, taxable, profit and commission per doctor for a period
/// (bills that only carry a typed doctor name are listed too, marked "not in doctor master"); the bills of the
/// selected doctor below; Ctrl+P prints, F2 previews.
/// </summary>
public sealed partial class DoctorSalesViewModel : BaseViewModel, INavigationAware
{
    private readonly IDoctorService _doctors;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<DoctorSalesSummary> _template;
    private readonly INavigationService _navigation;
    private readonly ILogger<DoctorSalesViewModel> _logger;
    private long? _focusDoctorId;
    private DoctorSalesSummary? _summary;

    public DoctorSalesViewModel(IDoctorService doctors, IPrintService print, IDocumentTemplate<DoctorSalesSummary> template,
        INavigationService navigation, ILogger<DoctorSalesViewModel> logger)
    {
        _doctors = doctors;
        _print = print;
        _template = template;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<DoctorSalesRow> Rows { get; } = new();
    public ObservableCollection<DoctorSaleBill> Bills { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private bool _includeUnlinked = true;
    [ObservableProperty] private DoctorSalesRow? _selectedRow;
    [ObservableProperty] private DoctorSalesTotals _totals = DoctorSalesTotals.Empty;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasSelection => SelectedRow is not null;
    public string BillsTitle => SelectedRow is null ? string.Empty : $"Bills of {SelectedRow.DisplayName} ({Bills.Count})";

    public void OnNavigatedTo(object? parameter) => _focusDoctorId = parameter as long?;

    partial void OnIncludeUnlinkedChanged(bool value) => _ = RefreshAsync();
    partial void OnSelectedRowChanged(DoctorSalesRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadBillsAsync(value);
    }

    public override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ClearStatus();
        if (FromDate is null || ToDate is null)
        {
            SetError("Choose the from and to dates.");
            return;
        }

        IsLoading = true;
        try
        {
            var keep = _focusDoctorId ?? SelectedRow?.DoctorId;
            var keepName = SelectedRow?.DoctorName;
            _focusDoctorId = null;
            _summary = await _doctors.GetSalesSummaryAsync(FromDate.Value, ToDate.Value, IncludeUnlinked);
            Rows.Clear();
            foreach (var row in _summary.Rows) Rows.Add(row);
            Totals = _summary.Totals;
            SelectedRow = Rows.FirstOrDefault(r => keep is not null && r.DoctorId == keep)
                          ?? Rows.FirstOrDefault(r => keep is null && r.DoctorName == keepName)
                          ?? Rows.FirstOrDefault();
            if (SelectedRow is null) Bills.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading doctor-wise sales failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load doctor-wise sales"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(BillsTitle));
        }
    }

    private async Task LoadBillsAsync(DoctorSalesRow? row)
    {
        Bills.Clear();
        if (row is null || FromDate is null || ToDate is null)
        {
            OnPropertyChanged(nameof(BillsTitle));
            return;
        }
        try
        {
            foreach (var bill in await _doctors.GetBillsAsync(row, FromDate.Value, ToDate.Value)) Bills.Add(bill);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the bills of a doctor failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the doctor's bills"));
        }
        OnPropertyChanged(nameof(BillsTitle));
    }

    [RelayCommand]
    private Task ThisMonthAsync()
    {
        FromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ToDate = DateTime.Today;
        return RefreshAsync();
    }

    [RelayCommand]
    private Task LastMonthAsync()
    {
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        FromDate = first;
        ToDate = first.AddMonths(1).AddDays(-1);
        return RefreshAsync();
    }

    [RelayCommand]
    private Task ThisFinancialYearAsync()
    {
        var today = DateTime.Today;
        var start = new DateTime(today.Month >= 4 ? today.Year : today.Year - 1, 4, 1);
        FromDate = start;
        ToDate = today < start.AddYears(1) ? today : start.AddYears(1).AddDays(-1);
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (_summary is null || Rows.Count == 0)
        {
            SetError("Nothing to print for this period.");
            return;
        }
        try
        {
            if (await _print.PrintAsync(_template, _summary with { GeneratedAt = DateTime.Now })) SetStatus("Doctor-wise sales sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing doctor-wise sales failed");
            SetError(DoctorsFollowupsUi.Message(ex, "print"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_summary is null || Rows.Count == 0)
        {
            SetError("Nothing to preview for this period.");
            return;
        }
        try
        {
            await _print.PreviewAsync(_template, _summary with { GeneratedAt = DateTime.Now });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing doctor-wise sales failed");
            SetError(DoctorsFollowupsUi.Message(ex, "preview"));
        }
    }

    [RelayCommand]
    private void OpenDoctors() => _navigation.NavigateTo(DoctorsFollowupsPages.Doctors);
}
