using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>Follow-up actions: followed up, dismiss, snooze, reopen, consumption, helpful, message, print.</summary>
public sealed partial class PatientFollowupsViewModel
{
    // Message (WhatsApp / call) panel
    [ObservableProperty] private bool _isMessageOpen;
    [ObservableProperty] private FollowupMessage? _message;

    // Snooze dialog
    [ObservableProperty] private bool _isSnoozeOpen;
    [ObservableProperty] private DateTime? _snoozeDate;

    // Dismiss dialog
    [ObservableProperty] private bool _isDismissOpen;
    [ObservableProperty] private string _dismissReason = string.Empty;

    [ObservableProperty] private string? _dialogError;

    /// <summary>The ticked rows, or the selected row when nothing is ticked.</summary>
    private IReadOnlyList<long> TargetIds()
    {
        var ticked = Rows.Where(r => r.IsChecked).Select(r => r.Followup.Id).ToList();
        if (ticked.Count > 0) return ticked;
        return Selected is null ? Array.Empty<long>() : new[] { Selected.Id };
    }

    /// <summary>F2: the ticked (or selected) reminders were followed up (Localwell "Followed up").</summary>
    [RelayCommand]
    private async Task MarkDoneAsync()
    {
        var ids = TargetIds();
        try
        {
            var note = string.IsNullOrWhiteSpace(NewNoteText) ? null : NewNoteText.Trim();
            var changed = await _followups.MarkDoneAsync(ids, note);
            NewNoteText = string.Empty;
            AllChecked = false;
            SetStatus($"{changed} follow-up(s) marked as followed up.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Marking follow-ups done failed");
            SetError(DoctorsFollowupsUi.Message(ex, "mark the follow-ups"));
        }
    }

    [RelayCommand]
    private void BeginDismiss()
    {
        if (TargetIds().Count == 0)
        {
            SetError("Please select a follow-up to continue.");
            return;
        }
        DismissReason = string.Empty;
        DialogError = null;
        IsDismissOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDismissAsync()
    {
        DialogError = null;
        try
        {
            var changed = await _followups.DismissAsync(TargetIds(), DismissReason);
            IsDismissOpen = false;
            AllChecked = false;
            SetStatus($"{changed} reminder(s) dismissed.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dismissing follow-ups failed");
            DialogError = DoctorsFollowupsUi.Message(ex, "dismiss the reminders");
        }
    }

    [RelayCommand]
    private async Task ReopenAsync()
    {
        if (Selected is null) return;
        try
        {
            await _followups.ReopenAsync(Selected.Id);
            SetStatus("Reminder is pending again.");
            Tab = FollowupTab.AllPending;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reopening a follow-up failed");
            SetError(DoctorsFollowupsUi.Message(ex, "reopen the reminder"));
        }
    }

    [RelayCommand]
    private void BeginSnooze()
    {
        if (Selected is not { IsPending: true })
        {
            SetError("Select a pending reminder to snooze.");
            return;
        }
        SnoozeDate = DateTime.Today.AddDays(3);
        DialogError = null;
        IsSnoozeOpen = true;
    }

    [RelayCommand]
    private void SnoozeDays(string? days)
    {
        if (int.TryParse(days, out var n)) SnoozeDate = DateTime.Today.AddDays(n);
    }

    [RelayCommand]
    private async Task ConfirmSnoozeAsync()
    {
        DialogError = null;
        if (Selected is null || SnoozeDate is null)
        {
            DialogError = "Choose the date to remind again.";
            return;
        }
        try
        {
            await _followups.SnoozeAsync(Selected.Id, SnoozeDate.Value);
            IsSnoozeOpen = false;
            SetStatus($"Reminder snoozed until {SnoozeDate:dd-MM-yyyy}.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snoozing a follow-up failed");
            DialogError = DoctorsFollowupsUi.Message(ex, "snooze the reminder");
        }
    }

    [RelayCommand]
    private async Task ClearSnoozeAsync()
    {
        if (Selected is not { IsSnoozed: true }) return;
        try
        {
            await _followups.ClearSnoozeAsync(Selected.Id);
            SetStatus("Snooze removed.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clearing a snooze failed");
            SetError(DoctorsFollowupsUi.Message(ex, "remove the snooze"));
        }
    }

    [RelayCommand]
    private async Task SaveConsumptionAsync()
    {
        if (Selected is null) return;
        try
        {
            var updated = await _followups.UpdateConsumptionAsync(Selected.Id, Consumption);
            SetStatus($"Consumption saved; next refill {updated.NextRefillDate:dd-MM-yyyy}.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving consumption failed");
            SetError(DoctorsFollowupsUi.Message(ex, "save the consumption"));
        }
    }

    [RelayCommand]
    private Task MarkHelpfulAsync() => SetHelpfulAsync(true);

    [RelayCommand]
    private Task MarkNotHelpfulAsync() => SetHelpfulAsync(false);

    private async Task SetHelpfulAsync(bool helpful)
    {
        if (Selected is null) return;
        try
        {
            await _followups.SetHelpfulAsync(Selected.Id, helpful);
            SetStatus(helpful ? "Marked as helpful." : "Marked as not helpful.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving helpful flag failed");
            SetError(DoctorsFollowupsUi.Message(ex, "save the answer"));
        }
    }

    // ───────────────────────── WhatsApp / call text ─────────────────────────

    /// <summary>Ctrl+W: builds the refill reminder for the ticked (or selected) medicines of one patient.</summary>
    [RelayCommand]
    private async Task BuildMessageAsync()
    {
        try
        {
            Message = await _followups.BuildMessageAsync(TargetIds());
            IsMessageOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building a refill reminder failed");
            SetError(DoctorsFollowupsUi.Message(ex, "build the reminder"));
        }
    }

    [RelayCommand]
    private void CopyMessage() => Copy(Message?.Message, "Reminder text copied. Paste it into WhatsApp or SMS.");

    [RelayCommand]
    private void CopyWhatsAppLink() => Copy(Message?.WhatsAppLink, "WhatsApp link copied. Open it in a browser to send the message from WhatsApp.");

    [RelayCommand]
    private void CopyPhone() => Copy(Message?.Mobile ?? Selected?.CustomerPhone, "Phone number copied.");

    [RelayCommand]
    private void CloseDialogs()
    {
        IsMessageOpen = false;
        IsSnoozeOpen = false;
        IsDismissOpen = false;
        IsManualOpen = false;
        IsRecommendationsOpen = false;
        EditingNote = null;
        DialogError = null;
    }

    private void Copy(string? text, string done)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetError("Nothing to copy: the customer has no valid mobile number.");
            return;
        }
        if (DoctorsFollowupsUi.TryCopy(text)) SetStatus(done);
        else SetError("The clipboard is busy. Try again.");
    }

    // ───────────────────────── Print and navigation ─────────────────────────

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Rows.Count == 0)
        {
            SetError("Nothing to print on this tab.");
            return;
        }
        try
        {
            var title = $"Refill reminders – {DoctorsFollowupsUi.Tabs.First(t => t.Value == Tab).Label}" +
                        (CustomerFilterName is null ? string.Empty : $" – {CustomerFilterName}");
            var list = new RefillReminderList(title, DateTime.Now, Rows.Select(r => r.Followup).ToList());
            if (await _print.PrintAsync(_template, list)) SetStatus("Reminder list sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing the reminder list failed");
            SetError(DoctorsFollowupsUi.Message(ex, "print"));
        }
    }

    [RelayCommand]
    private void OpenRefillRules() => _navigation.NavigateTo(DoctorsFollowupsPages.RefillRules);
}
