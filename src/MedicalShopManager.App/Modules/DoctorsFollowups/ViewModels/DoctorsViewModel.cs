using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>
/// Sales ▸ Doctors (doctor master, Localwell "Select Doctor" / doctors_add_doctor): search by name, reg. no, phone,
/// clinic or specialization; add (F2) / edit (F4, Enter) with registration number, qualification, specialization,
/// clinic, contact and commission %; deactivate (Del, bills keep their doctor); doctor-wise sales (F6).
/// </summary>
public sealed partial class DoctorsViewModel : BaseViewModel
{
    private readonly IDoctorService _doctors;
    private readonly INavigationService _navigation;
    private readonly ILogger<DoctorsViewModel> _logger;
    private CancellationTokenSource? _search;

    public DoctorsViewModel(IDoctorService doctors, INavigationService navigation, ILogger<DoctorsViewModel> logger)
    {
        _doctors = doctors;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<Doctor> Doctors { get; } = new();
    public DoctorEditorViewModel Editor { get; } = new();

    [ObservableProperty] private Doctor? _selectedDoctor;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private bool _isEditorOpen;

    public bool IsEmpty => !IsLoading && Doctors.Count == 0;
    public string EmptyTitle => string.IsNullOrWhiteSpace(SearchQuery) ? "No doctors yet" : "No doctors match your search";
    public string EmptyHint => string.IsNullOrWhiteSpace(SearchQuery)
        ? "Press F2 to add the doctors whose prescriptions you fill. Bills can then name the doctor, and the doctor-wise sales report shows their referrals."
        : "Try part of the name, the registration number or the phone number, or press F2 to add the doctor.";
    public string ActivateLabel => SelectedDoctor is { IsActive: false } ? "Activate (Del)" : "Deactivate (Del)";
    public int ActiveCount => Doctors.Count(d => d.IsActive);

    partial void OnSelectedDoctorChanged(Doctor? value) => OnPropertyChanged(nameof(ActivateLabel));
    partial void OnSearchQueryChanged(string value) => _ = DebouncedRefreshAsync();
    partial void OnIncludeInactiveChanged(bool value) => _ = RefreshAsync();

    public override Task LoadAsync() => RefreshAsync();

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
            var selectedId = SelectedDoctor?.Id;
            var list = await _doctors.SearchAsync(SearchQuery, IncludeInactive);
            Doctors.Clear();
            foreach (var d in list) Doctors.Add(d);
            SelectedDoctor = Doctors.FirstOrDefault(d => d.Id == selectedId) ?? Doctors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading doctors failed");
            SetError(DoctorsFollowupsUi.Message(ex, "load the doctors"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(ActiveCount));
        }
    }

    [RelayCommand]
    private void Add()
    {
        // Typed search text that is not a number becomes the new doctor's name.
        var seed = SearchQuery.Trim();
        Editor.Load(new Doctor { Name = seed.Any(char.IsLetter) ? seed : string.Empty, RegistrationNo = seed.Any(char.IsLetter) ? null : seed });
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void Edit(Doctor? doctor)
    {
        doctor ??= SelectedDoctor;
        if (doctor is null) return;
        Editor.Load(doctor);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsEditorOpen) return;
        Editor.Errors.Clear();
        try
        {
            var doctor = Editor.ToDoctor();
            var isNew = doctor.Id == 0;
            var id = await _doctors.SaveAsync(doctor);
            IsEditorOpen = false;
            await RefreshAsync();
            SelectedDoctor = Doctors.FirstOrDefault(d => d.Id == id) ?? SelectedDoctor;
            SetStatus(isNew ? $"{DoctorRules.DisplayName(doctor.Name)} added." : $"{DoctorRules.DisplayName(doctor.Name)} updated.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving doctor failed");
            foreach (var message in DoctorsFollowupsUi.Messages(ex, "save the doctor")) Editor.Errors.Add(message);
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(Doctor? doctor)
    {
        doctor ??= SelectedDoctor;
        if (doctor is null || IsEditorOpen) return;
        try
        {
            await _doctors.SetActiveAsync(doctor.Id, !doctor.IsActive);
            SetStatus(doctor.IsActive
                ? $"{doctor.DisplayName} deactivated; bills that name the doctor keep it. Tick 'Show inactive' to see the doctor again."
                : $"{doctor.DisplayName} is active again.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing doctor status failed");
            SetError(DoctorsFollowupsUi.Message(ex, "change the doctor"));
        }
    }

    [RelayCommand]
    private void ViewSales(Doctor? doctor)
    {
        doctor ??= SelectedDoctor;
        _navigation.NavigateTo(DoctorsFollowupsPages.DoctorSales, doctor?.Id);
    }
}
