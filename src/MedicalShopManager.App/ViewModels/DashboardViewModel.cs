using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.ViewModels;

/// <summary>Dashboard with live KPI cards and charts.</summary>
public partial class DashboardViewModel : BaseViewModel
{
    private readonly IReportService _reportService;

    [ObservableProperty] private decimal _todaySales;
    [ObservableProperty] private decimal _todayPurchases;
    [ObservableProperty] private decimal _todayGrossProfit;
    [ObservableProperty] private int _totalActiveMedicines;
    [ObservableProperty] private int _totalStockUnits;
    [ObservableProperty] private int _lowStockCount;
    [ObservableProperty] private int _nearExpiryCount;
    [ObservableProperty] private int _expiredCount;
    [ObservableProperty] private decimal _pendingSupplierPayments;
    [ObservableProperty] private int _todayBillCount;

    public DashboardViewModel(IReportService reportService)
    {
        _reportService = reportService;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var data = await _reportService.GetDashboardDataAsync();
            TodaySales = data.TodaySales;
            TodayPurchases = data.TodayPurchases;
            TodayGrossProfit = data.TodayGrossProfit;
            TotalActiveMedicines = data.TotalActiveMedicines;
            TotalStockUnits = data.TotalStockUnits;
            LowStockCount = data.LowStockCount;
            NearExpiryCount = data.NearExpiryCount;
            ExpiredCount = data.ExpiredCount;
            PendingSupplierPayments = data.PendingSupplierPayments;
            TodayBillCount = data.TodayBillCount;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load dashboard: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
