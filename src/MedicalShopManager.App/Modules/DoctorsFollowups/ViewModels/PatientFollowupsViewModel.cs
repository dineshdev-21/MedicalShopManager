using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>
/// Dashboard ▸ Patient Follow-ups (Localwell /reminders/view-reminders): refill reminders from bills of chronic
/// medicines. Tabs Arriving (overdue, due today, upcoming) | Expired (lapsed) | Actioned | All pending with counts;
/// search (customer name / mobile, medicine), gender filter and sort; tick rows (Space) for a WhatsApp message
/// (Ctrl+W copies text and link, nothing is sent) or Followed up (F2); snooze (F3), dismiss (Del), consumption,
/// helpful, remarks (Enter posts); manual follow-up (F4), recommended reminders (F6), refill rules (F7), print (Ctrl+P).
/// </summary>
public sealed partial class PatientFollowupsViewModel : BaseViewModel, INavigationAware
{
    private readonly IFollowupService _followups;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<RefillReminderList> _template;
    private readonly INavigationService _navigation;
    private readonly ILogger<PatientFollowupsViewModel> _logger;
    private CancellationTokenSource? _search;
    private long? _focusFollowupId;
    private bool _loaded;

    public PatientFollowupsViewModel(IFollowupService followups, IAuthService auth, IPrintService print,
        IDocumentTemplate<RefillReminderList> template, INavigationService navigation, ILogger<PatientFollowupsViewModel> logger)
    {
        _followups = followups;
        _auth = auth;
        _print = print;
        _template = template;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<FollowupRowViewModel> Rows { get; } = new();
    public ObservableCollection<FollowupNote> Notes { get; } = new();
    public IReadOnlyList<DoctorsFollowupsOption<FollowupSort>> Sorts => DoctorsFollowupsUi.Sorts;
    public IReadOnlyList<DoctorsFollowupsOption<int?>> Genders => DoctorsFollowupsUi.Genders;

    [ObservableProperty] private FollowupTab _tab = FollowupTab.Arriving;
    [ObservableProperty] private FollowupCounts _counts = FollowupCounts.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DoctorsFollowupsOption<int?> _selectedGender = DoctorsFollowupsUi.Genders[0];
    [ObservableProperty] private DoctorsFollowupsOption<FollowupSort> _selectedSort = DoctorsFollowupsUi.Sorts[0];
    [ObservableProperty] private long? _customerFilterId;
    [ObservableProperty] private string? _customerFilterName;
    [ObservableProperty] private FollowupRowViewModel? _selectedRow;
    [ObservableProperty] private CustomerFollowupSummary? _customerSummary;
    [ObservableProperty] private string _newNoteText = string.Empty;
    [ObservableProperty] private FollowupNote? _editingNote;
    [ObservableProperty] private string _editingNoteText = string.Empty;
    [ObservableProperty] private decimal _consumption;
    [ObservableProperty] private bool _isDisabled;
    [ObservableProperty] private bool _allChecked;

    public PatientFollowup? Selected => SelectedRow?.Followup;
    public bool HasSelection => Selected is not null;
    public bool IsSelectedPending => Selected?.IsPending == true;
    public bool IsSelectedClosed => Selected is { IsPending: false };
    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasCustomerFilter => CustomerFilterId is not null;
    public int CheckedCount => Rows.Count(r => r.IsChecked);
    public string CheckedLabel => $"Select all ({Rows.Count} follow-ups)  ·  {CheckedCount} ticked";
    public long? CurrentUserId => _auth.CurrentUser?.Id;

    public string ArrivingLabel => $"Arriving ({Counts.Arriving})";
    public string ExpiredLabel => $"Expired ({Counts.Expired})";
    public string ActionedLabel => $"Actioned ({Counts.Actioned})";
    public string AllLabel => $"All pending ({Counts.AllPending})";
    public string BucketSummary => $"Overdue {Counts.Overdue}  ·  Due today {Counts.DueToday}  ·  Upcoming {Counts.Upcoming}  ·  Later {Counts.Later}";

    public string EmptyTitle => Tab switch
    {
        FollowupTab.Expired => "No lapsed reminders",
        FollowupTab.Actioned => "Nothing followed up yet",
        _ => "No Followups Found!"
    };

    public string EmptyHint => IsDisabled
        ? "Patient reminders are switched off in Control Center ▸ Sale Settings, so bills do not create follow-ups. Manual follow-ups (F4) still work."
        : "Follow-ups are created from bills of chronic medicines sold to a named customer. Mark compositions for refill reminders (F7), add one by hand (F4) or see the recommended reminders (F6).";

    // Tab radio buttons (two-way): setting one to true switches the tab.
    public bool IsArrivingTab { get => Tab == FollowupTab.Arriving; set { if (value) Tab = FollowupTab.Arriving; } }
    public bool IsExpiredTab { get => Tab == FollowupTab.Expired; set { if (value) Tab = FollowupTab.Expired; } }
    public bool IsActionedTab { get => Tab == FollowupTab.Actioned; set { if (value) Tab = FollowupTab.Actioned; } }
    public bool IsAllTab { get => Tab == FollowupTab.AllPending; set { if (value) Tab = FollowupTab.AllPending; } }

    public void OnNavigatedTo(object? parameter)
    {
        switch (parameter)
        {
            case OpenFollowupsRequest request:
                CustomerFilterId = request.CustomerId;
                _focusFollowupId = request.FollowupId;
                Tab = request.Tab;
                break;
            case long customerId:
                CustomerFilterId = customerId;
                break;
        }
    }

    public override async Task LoadAsync()
    {
        _loaded = true;
        await RegenerateAndRefreshAsync();
    }

    partial void OnTabChanged(FollowupTab value)
    {
        OnPropertyChanged(nameof(IsArrivingTab));
        OnPropertyChanged(nameof(IsExpiredTab));
        OnPropertyChanged(nameof(IsActionedTab));
        OnPropertyChanged(nameof(IsAllTab));
        OnPropertyChanged(nameof(EmptyTitle));
        if (_loaded) _ = RefreshAsync();
    }

    partial void OnCountsChanged(FollowupCounts value)
    {
        OnPropertyChanged(nameof(ArrivingLabel));
        OnPropertyChanged(nameof(ExpiredLabel));
        OnPropertyChanged(nameof(ActionedLabel));
        OnPropertyChanged(nameof(AllLabel));
        OnPropertyChanged(nameof(BucketSummary));
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedRefreshAsync();
    partial void OnSelectedGenderChanged(DoctorsFollowupsOption<int?> value) { if (_loaded) _ = RefreshAsync(); }
    partial void OnSelectedSortChanged(DoctorsFollowupsOption<FollowupSort> value) { if (_loaded) _ = RefreshAsync(); }
    partial void OnCustomerFilterIdChanged(long? value) => OnPropertyChanged(nameof(HasCustomerFilter));
    partial void OnIsDisabledChanged(bool value) => OnPropertyChanged(nameof(EmptyHint));

    partial void OnAllCheckedChanged(bool value)
    {
        foreach (var row in Rows) row.IsChecked = value;
    }

    partial void OnSelectedRowChanged(FollowupRowViewModel? value)
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectedPending));
        OnPropertyChanged(nameof(IsSelectedClosed));
        Consumption = value?.Followup.UnitsPerDay ?? 0;
        EditingNote = null;
        _ = LoadDetailAsync(value?.Followup);
    }

    private async Task DebouncedRefreshAsync()
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        try
        {
            await Task.Delay(450, cts.Token); // Localwell debounces the follow-up search by 450 ms
            await RefreshAsync();
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>F5: re-reads recent bills for chronic medicines, then reloads the list.</summary>
    [RelayCommand]
    private async Task RegenerateAndRefreshAsync()
    {
        try
        {
            IsDisabled = !await _followups.IsEnabledAsync();
            var result = await _followups.GenerateFromSalesAsync();
            if (result.HasChanges) SetStatus(result.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating refill follow-ups failed");
            SetError(DoctorsFollowupsUi.Message(ex, "read recent bills for follow-ups"));
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var keepId = _focusFollowupId ?? Selected?.Id;
            _focusFollowupId = null;
            var result = await _followups.GetListAsync(new FollowupQuery
            {
                Tab = Tab,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                CustomerId = CustomerFilterId,
                Gender = SelectedGender?.Value,
                Sort = SelectedSort?.Value ?? FollowupSort.FollowupDateAscending
            });

            foreach (var row in Rows) row.PropertyChanged -= OnRowPropertyChanged;
            Rows.Clear();
            foreach (var item in result.Items)
            {
                var row = new FollowupRowViewModel(item);
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }
            Counts = result.Counts;
            SelectedRow = Rows.FirstOrDefault(r => r.Followup.Id == keepId) ?? Rows.FirstOrDefault();
            if (CustomerFilterId is { } customerId && CustomerFilterName is null)
                CustomerFilterName = Rows.FirstOrDefault()?.Followup.CustomerName ?? (await _followups.GetCustomerSummaryAsync(customerId))?.CustomerName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading refill follow-ups failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the follow-ups"));
        }
        finally
        {
            IsLoading = false;
            NotifyListChanged();
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FollowupRowViewModel.IsChecked)) NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedLabel));
    }

    [RelayCommand]
    private void ClearCustomerFilter()
    {
        CustomerFilterId = null;
        CustomerFilterName = null;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void FilterToSelectedCustomer()
    {
        if (Selected is null) return;
        CustomerFilterId = Selected.CustomerId;
        CustomerFilterName = Selected.CustomerName;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ResetFilters()
    {
        _loaded = false;
        SearchText = string.Empty;
        SelectedGender = DoctorsFollowupsUi.Genders[0];
        SelectedSort = DoctorsFollowupsUi.Sorts[0];
        CustomerFilterId = null;
        CustomerFilterName = null;
        _loaded = true;
        _ = RefreshAsync();
    }

    /// <summary>Space on a row: tick / untick it.</summary>
    [RelayCommand]
    private void ToggleChecked(FollowupRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null) row.IsChecked = !row.IsChecked;
    }

    // ───────────────────────── Detail: customer card and remarks ─────────────────────────

    private async Task LoadDetailAsync(PatientFollowup? followup)
    {
        Notes.Clear();
        if (followup is null)
        {
            CustomerSummary = null;
            return;
        }
        try
        {
            if (CustomerSummary?.CustomerId != followup.CustomerId)
                CustomerSummary = await _followups.GetCustomerSummaryAsync(followup.CustomerId);
            foreach (var note in await _followups.GetNotesAsync(followup.Id)) Notes.Add(note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading follow-up details failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the follow-up details"));
        }
    }

    /// <summary>Enter in the remark box (Localwell "Add Followup Remarks or Post an Update").</summary>
    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(NewNoteText)) return;
        try
        {
            await _followups.AddNoteAsync(Selected.Id, NewNoteText);
            NewNoteText = string.Empty;
            await LoadDetailAsync(Selected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding a follow-up remark failed");
            SetError(DoctorsFollowupsUi.Message(ex, "add the remark"));
        }
    }

    [RelayCommand]
    private void BeginEditNote(FollowupNote? note)
    {
        if (note is null) return;
        EditingNote = note;
        EditingNoteText = note.Note;
    }

    [RelayCommand]
    private void CancelEditNote() => EditingNote = null;

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (EditingNote is null) return;
        try
        {
            await _followups.UpdateNoteAsync(EditingNote.Id, EditingNoteText);
            EditingNote = null;
            await LoadDetailAsync(Selected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Editing a follow-up remark failed");
            SetError(DoctorsFollowupsUi.Message(ex, "save the remark"));
        }
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(FollowupNote? note)
    {
        if (note is null) return;
        try
        {
            await _followups.DeleteNoteAsync(note.Id);
            await LoadDetailAsync(Selected);
            SetStatus("Remark deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deleting a follow-up remark failed");
            SetError(DoctorsFollowupsUi.Message(ex, "delete the remark"));
        }
    }
}
