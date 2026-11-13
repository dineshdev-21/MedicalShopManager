using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>
/// Refill rules and follow-up settings (opened with F7 from Patient Follow-ups): which compositions (or medicines
/// without a composition) create refill reminders when billed, and at what daily consumption; plus the module
/// settings (default consumption, upcoming window, grace period, bills to scan). F2 toggles Remind on the selected
/// rule, Alt+S saves the rule being edited, Alt+T saves the settings, Esc goes back.
/// </summary>
public sealed partial class RefillRulesViewModel : BaseViewModel
{
    private readonly IFollowupService _followups;
    private readonly INavigationService _navigation;
    private readonly ILogger<RefillRulesViewModel> _logger;
    private CancellationTokenSource? _search;
    private bool _loaded;

    public RefillRulesViewModel(IFollowupService followups, INavigationService navigation, ILogger<RefillRulesViewModel> logger)
    {
        _followups = followups;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<RefillRule> Rules { get; } = new();
    public ObservableCollection<string> SettingsErrors { get; } = new();
    public IReadOnlyList<DoctorsFollowupsOption<bool?>> RuleChoices => DoctorsFollowupsUi.RuleChoices;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _chronicOnly;
    [ObservableProperty] private RefillRule? _selectedRule;
    [ObservableProperty] private DoctorsFollowupsOption<bool?> _editChoice = DoctorsFollowupsUi.RuleChoices[0];
    [ObservableProperty] private string _editUnitsPerDay = string.Empty;

    // Settings
    [ObservableProperty] private decimal _defaultUnitsPerDay = RefillCalculator.DefaultUnitsPerDay;
    [ObservableProperty] private int _upcomingDays = FollowupSettings.Default.UpcomingDays;
    [ObservableProperty] private int _graceDays = FollowupSettings.Default.GraceDays;
    [ObservableProperty] private int _lookbackDays = FollowupSettings.Default.LookbackDays;

    public bool IsEmpty => !IsLoading && Rules.Count == 0;
    public bool HasSelection => SelectedRule is not null;
    public bool HasSettingsErrors => SettingsErrors.Count > 0;
    public int RemindCount => Rules.Count(r => r.IsChronic);

    public string EmptyTitle => ChronicOnly ? "Nothing is marked for refill reminders" : "No compositions or medicines found";
    public string EmptyHint => ChronicOnly
        ? "Untick 'Only items that remind', find the composition (e.g. Metformin) and set it to Remind."
        : "Compositions come from the item master. Add items in Stock first, then mark their compositions here.";

    public string SelectedTitle => SelectedRule is null ? "Select a row to change its rule" : $"{SelectedRule.Kind}: {SelectedRule.Name}";
    public string SelectedDefaultLabel => SelectedRule is null
        ? string.Empty
        : $"Default from the item master: {(SelectedRule.DefaultIsChronic ? "remind (chronic)" : "do not remind")}";

    partial void OnSearchTextChanged(string value) => _ = DebouncedRefreshAsync();
    partial void OnChronicOnlyChanged(bool value) { if (_loaded) _ = RefreshAsync(); }

    partial void OnSelectedRuleChanged(RefillRule? value)
    {
        EditChoice = DoctorsFollowupsUi.RuleChoices.First(c => c.Value == value?.RuleIsChronic);
        EditUnitsPerDay = value?.UnitsPerDay is { } perDay ? perDay.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDefaultLabel));
    }

    public override async Task LoadAsync()
    {
        await LoadSettingsAsync();
        await RefreshAsync();
        _loaded = true;
    }

    private async Task DebouncedRefreshAsync()
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            await RefreshAsync();
        }
        catch (TaskCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var keep = SelectedRule;
            var list = await _followups.SearchRulesAsync(SearchText, ChronicOnly);
            Rules.Clear();
            foreach (var rule in list) Rules.Add(rule);
            SelectedRule = Rules.FirstOrDefault(r => keep is not null && r.CompositionId == keep.CompositionId && r.MedicineId == keep.MedicineId)
                           ?? Rules.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading refill rules failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the refill rules"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(RemindCount));
        }
    }

    /// <summary>Alt+S: saves the choice and consumption typed for the selected composition / medicine.</summary>
    [RelayCommand]
    private async Task SaveRuleAsync()
    {
        if (SelectedRule is null)
        {
            SetError("Select a composition or medicine first.");
            return;
        }

        decimal? perDay = null;
        if (!string.IsNullOrWhiteSpace(EditUnitsPerDay))
        {
            if (!decimal.TryParse(EditUnitsPerDay.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed)
                && !decimal.TryParse(EditUnitsPerDay.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                SetError("Enter the consumption as a number of units per day (e.g. 1 or 0.5), or leave it blank for the default.");
                return;
            }
            perDay = parsed;
        }
        await SaveAsync(SelectedRule with { RuleIsChronic = EditChoice?.Value, UnitsPerDay = perDay });
    }

    /// <summary>F2: switches the selected row between Remind and Do not remind (keeps its consumption).</summary>
    [RelayCommand]
    private async Task ToggleRemindAsync()
    {
        if (SelectedRule is null) return;
        await SaveAsync(SelectedRule with { RuleIsChronic = !SelectedRule.IsChronic });
    }

    private async Task SaveAsync(RefillRule rule)
    {
        try
        {
            await _followups.SaveRuleAsync(rule);
            SetStatus($"{rule.Name}: {(rule.IsChronic ? "bills now create refill reminders" : "no refill reminders")}" +
                      (rule.UnitsPerDay is { } perDay ? $", {perDay:0.##} unit(s)/day." : "."));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving a refill rule failed");
            SetError(DoctorsFollowupsUi.Message(ex, "save the refill rule"));
        }
    }

    // ───────────────────────── Settings ─────────────────────────

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _followups.GetSettingsAsync();
            DefaultUnitsPerDay = settings.DefaultUnitsPerDay;
            UpcomingDays = settings.UpcomingDays;
            GraceDays = settings.GraceDays;
            LookbackDays = settings.LookbackDays;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading follow-up settings failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the follow-up settings"));
        }
    }

    /// <summary>Alt+T: validates and saves the four follow-up settings.</summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        SettingsErrors.Clear();
        try
        {
            await _followups.SaveSettingsAsync(new FollowupSettings
            {
                DefaultUnitsPerDay = DefaultUnitsPerDay,
                UpcomingDays = UpcomingDays,
                GraceDays = GraceDays,
                LookbackDays = LookbackDays
            });
            SetStatus("Follow-up settings saved.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving follow-up settings failed");
            foreach (var message in DoctorsFollowupsUi.Messages(ex, "save the settings")) SettingsErrors.Add(message);
        }
        OnPropertyChanged(nameof(HasSettingsErrors));
    }

    [RelayCommand]
    private void Back() => _navigation.NavigateTo(DoctorsFollowupsPages.Followups);
}
