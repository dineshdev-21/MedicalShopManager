using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.Modules;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Billing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// "Unsaved Drafts" (F8, spec 02 §O): auto-saved bills of the last 7 days (older ones are purged when listed).
/// Enter opens the draft on the billing screen, Del discards it. As a page (window shortcut F8) opening navigates
/// to NewBill; hosted inside the billing screen it raises <see cref="OpenRequested"/> instead.
/// </summary>
public sealed partial class BillingDraftsViewModel : BaseViewModel
{
    private readonly ISaleDraftService _drafts;
    private readonly INavigationService _navigation;
    private readonly ILogger<BillingDraftsViewModel> _logger;

    public BillingDraftsViewModel(ISaleDraftService drafts, INavigationService navigation, ILogger<BillingDraftsViewModel> logger)
    {
        _drafts = drafts;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>Set by the billing screen when it hosts this list as a popup.</summary>
    public event EventHandler<long>? OpenRequested;
    public event EventHandler? Closed;

    public bool IsHosted { get; set; }

    public ObservableCollection<SaleDraftSummary> Drafts { get; } = new();

    [ObservableProperty] private SaleDraftSummary? _selectedDraft;

    public bool IsEmpty => !IsLoading && Drafts.Count == 0;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var list = await _drafts.ListAsync();
            Drafts.Clear();
            foreach (var draft in list) Drafts.Add(draft);
            SelectedDraft = Drafts.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading sale drafts failed");
            SetError($"Drafts could not be loaded: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void Open(SaleDraftSummary? draft)
    {
        draft ??= SelectedDraft;
        if (draft is null) return;
        if (IsHosted) OpenRequested?.Invoke(this, draft.Id);
        else _navigation.NavigateTo(BillingModule.NewBillKey, new OpenSaleDraftRequest(draft.Id));
    }

    [RelayCommand]
    private async Task DeleteAsync(SaleDraftSummary? draft)
    {
        draft ??= SelectedDraft;
        if (draft is null) return;
        try
        {
            await _drafts.DeleteAsync(draft.Id);
            Drafts.Remove(draft);
            SelectedDraft = Drafts.FirstOrDefault();
            SetStatus($"Draft '{draft.Title}' discarded.");
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting draft {DraftId} failed", draft.Id);
            SetError($"The draft could not be discarded: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void Close()
    {
        if (IsHosted) Closed?.Invoke(this, EventArgs.Empty);
        else _navigation.NavigateTo(BillingModule.NewBillKey);
    }
}
