using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>Manual follow-up (F4) and recommended reminders (F6).</summary>
public sealed partial class PatientFollowupsViewModel
{
    private CancellationTokenSource? _customerSearch;
    private CancellationTokenSource? _itemSearch;

    public ObservableCollection<FollowupCustomerOption> CustomerOptions { get; } = new();
    public ObservableCollection<FollowupItemOption> ItemOptions { get; } = new();
    public ObservableCollection<string> ManualErrors { get; } = new();
    public ObservableCollection<RefillRecommendation> Recommendations { get; } = new();

    [ObservableProperty] private bool _isManualOpen;
    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private FollowupCustomerOption? _manualCustomer;
    [ObservableProperty] private string _manualPatientName = string.Empty;
    [ObservableProperty] private string _itemSearchText = string.Empty;
    [ObservableProperty] private FollowupItemOption? _manualItem;
    [ObservableProperty] private DateTime? _manualStartDate = DateTime.Today;
    [ObservableProperty] private long _manualUnits;
    [ObservableProperty] private decimal _manualUnitsPerDay = RefillCalculator.DefaultUnitsPerDay;
    [ObservableProperty] private string _manualNote = string.Empty;

    [ObservableProperty] private bool _isRecommendationsOpen;
    [ObservableProperty] private RefillRecommendation? _selectedRecommendation;
    [ObservableProperty] private bool _alwaysRemind;

    public bool HasManualErrors => ManualErrors.Count > 0;
    public bool HasRecommendations => Recommendations.Count > 0;

    /// <summary>"Due on 12-12-2026 (60 days)" preview of the manual follow-up.</summary>
    public string ManualPreview
    {
        get
        {
            if (ManualUnits <= 0 || RefillCalculator.ValidateUnitsPerDay(ManualUnitsPerDay) is not null || ManualStartDate is null) return string.Empty;
            var days = RefillCalculator.DaysSupply(ManualUnits, ManualUnitsPerDay);
            return $"Lasts {days} day(s): refill due on {ManualStartDate.Value.AddDays(days):dd-MM-yyyy}";
        }
    }

    partial void OnCustomerSearchTextChanged(string value) => _ = SearchCustomersAsync();
    partial void OnItemSearchTextChanged(string value) => _ = SearchItemsAsync();
    partial void OnManualUnitsChanged(long value) => OnPropertyChanged(nameof(ManualPreview));
    partial void OnManualUnitsPerDayChanged(decimal value) => OnPropertyChanged(nameof(ManualPreview));
    partial void OnManualStartDateChanged(DateTime? value) => OnPropertyChanged(nameof(ManualPreview));

    partial void OnManualItemChanged(FollowupItemOption? value)
    {
        if (value is null) return;
        if (value.UnitsPerDay is { } perDay) ManualUnitsPerDay = perDay;
        if (ManualUnits <= 0) ManualUnits = value.UnitsPerPack;
    }

    /// <summary>F4: add a follow-up by hand (pre-filled with the selected customer).</summary>
    [RelayCommand]
    private async Task BeginManualAsync()
    {
        ManualErrors.Clear();
        OnPropertyChanged(nameof(HasManualErrors));
        CustomerOptions.Clear();
        ItemOptions.Clear();
        ManualCustomer = null;
        ManualItem = null;
        ManualPatientName = string.Empty;
        ManualStartDate = DateTime.Today;
        ManualUnits = 0;
        ManualNote = string.Empty;
        ItemSearchText = string.Empty;
        try
        {
            ManualUnitsPerDay = (await _followups.GetSettingsAsync()).DefaultUnitsPerDay;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading follow-up settings failed");
            ManualUnitsPerDay = RefillCalculator.DefaultUnitsPerDay;
        }

        var preset = Selected;
        CustomerSearchText = preset?.CustomerName ?? CustomerFilterName ?? string.Empty;
        if (preset is not null)
        {
            var option = new FollowupCustomerOption(preset.CustomerId, preset.CustomerName, preset.CustomerPhone);
            CustomerOptions.Clear();
            CustomerOptions.Add(option);
            ManualCustomer = option;
            ManualPatientName = preset.PatientKey.Length == 0 ? string.Empty : preset.PatientDisplay;
        }
        IsManualOpen = true;
    }

    private async Task SearchCustomersAsync()
    {
        _customerSearch?.Cancel();
        var cts = _customerSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var keep = ManualCustomer;
            var options = await _followups.SearchCustomersAsync(CustomerSearchText);
            if (cts.IsCancellationRequested) return;
            CustomerOptions.Clear();
            foreach (var o in options) CustomerOptions.Add(o);
            ManualCustomer = CustomerOptions.FirstOrDefault(o => o.Id == keep?.Id) ?? CustomerOptions.FirstOrDefault();
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Customer search failed");
        }
    }

    private async Task SearchItemsAsync()
    {
        _itemSearch?.Cancel();
        var cts = _itemSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            if (string.IsNullOrWhiteSpace(ItemSearchText))
            {
                ItemOptions.Clear();
                return;
            }
            var options = await _followups.SearchItemsAsync(ItemSearchText);
            if (cts.IsCancellationRequested) return;
            ItemOptions.Clear();
            foreach (var o in options) ItemOptions.Add(o);
            ManualItem = ItemOptions.FirstOrDefault();
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Item search failed");
        }
    }

    [RelayCommand]
    private async Task SaveManualAsync()
    {
        if (!IsManualOpen) return;
        ManualErrors.Clear();
        try
        {
            var id = await _followups.CreateManualAsync(new ManualFollowupRequest
            {
                CustomerId = ManualCustomer?.Id ?? 0,
                PatientName = string.IsNullOrWhiteSpace(ManualPatientName) ? null : ManualPatientName,
                MedicineId = ManualItem?.MedicineId ?? 0,
                StartDate = ManualStartDate ?? DateTime.Today,
                QuantityUnits = ManualUnits,
                UnitsPerDay = ManualUnitsPerDay,
                Note = string.IsNullOrWhiteSpace(ManualNote) ? null : ManualNote
            });
            IsManualOpen = false;
            _focusFollowupId = id;
            Tab = FollowupTab.AllPending;
            SetStatus("Follow-up added.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding a manual follow-up failed");
            foreach (var message in DoctorsFollowupsUi.Messages(ex, "add the follow-up")) ManualErrors.Add(message);
        }
        OnPropertyChanged(nameof(HasManualErrors));
    }

    // ───────────────────────── Recommended reminders ─────────────────────────

    /// <summary>F6: customers who keep buying an item that is not marked for refill reminders.</summary>
    [RelayCommand]
    private async Task ShowRecommendationsAsync()
    {
        DialogError = null;
        AlwaysRemind = false;
        try
        {
            var list = await _followups.GetRecommendationsAsync();
            Recommendations.Clear();
            foreach (var r in list) Recommendations.Add(r);
            SelectedRecommendation = Recommendations.FirstOrDefault();
            IsRecommendationsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading recommended reminders failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the recommended reminders"));
        }
        OnPropertyChanged(nameof(HasRecommendations));
    }

    [RelayCommand]
    private async Task AcceptRecommendationAsync(RefillRecommendation? recommendation)
    {
        recommendation ??= SelectedRecommendation;
        if (recommendation is null) return;
        DialogError = null;
        try
        {
            var id = await _followups.AcceptRecommendationAsync(recommendation, AlwaysRemind);
            Recommendations.Remove(recommendation);
            SelectedRecommendation = Recommendations.FirstOrDefault();
            OnPropertyChanged(nameof(HasRecommendations));
            _focusFollowupId = id;
            SetStatus(AlwaysRemind
                ? $"Reminder added for {recommendation.PatientDisplay}; future bills of {recommendation.ItemDisplay} create reminders too."
                : $"Reminder added for {recommendation.PatientDisplay}: {recommendation.MedicineName}, due {recommendation.SuggestedDueDate:dd-MM-yyyy}.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Accepting a recommended reminder failed");
            DialogError = DoctorsFollowupsUi.Message(ex, "add the reminder");
        }
    }
}
