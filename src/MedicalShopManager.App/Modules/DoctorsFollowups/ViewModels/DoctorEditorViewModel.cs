using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>Add / edit form of the doctor master (hosted by <see cref="DoctorsViewModel"/>; view DoctorEditorView).</summary>
public sealed partial class DoctorEditorViewModel : ObservableObject
{
    private long _id;
    private bool _isActive = true;
    private DateTime _createdAt;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _registrationNo = string.Empty;
    [ObservableProperty] private string _qualification = string.Empty;
    [ObservableProperty] private string _specialization = string.Empty;
    [ObservableProperty] private string _clinicName = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private decimal _commissionPercent;
    [ObservableProperty] private bool _commissionOnProfit;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<string> Errors { get; } = new();

    public string Title => _id == 0 ? "Add doctor" : "Edit doctor";
    public bool HasErrors => Errors.Count > 0;

    public DoctorEditorViewModel() => Errors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasErrors));

    public void Load(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        _id = doctor.Id;
        _isActive = doctor.IsActive;
        _createdAt = doctor.CreatedAt;
        Name = doctor.Name;
        RegistrationNo = doctor.RegistrationNo ?? string.Empty;
        Qualification = doctor.Qualification ?? string.Empty;
        Specialization = doctor.Specialization ?? string.Empty;
        ClinicName = doctor.ClinicName ?? string.Empty;
        Phone = doctor.Phone ?? string.Empty;
        Email = doctor.Email ?? string.Empty;
        Address = doctor.Address ?? string.Empty;
        City = doctor.City ?? string.Empty;
        CommissionPercent = doctor.CommissionPercent;
        CommissionOnProfit = doctor.CommissionOnProfit;
        Notes = doctor.Notes ?? string.Empty;
        Errors.Clear();
        OnPropertyChanged(nameof(Title));
    }

    public Doctor ToDoctor() => new()
    {
        Id = _id,
        Name = Name,
        RegistrationNo = RegistrationNo,
        Qualification = Qualification,
        Specialization = Specialization,
        ClinicName = ClinicName,
        Phone = Phone,
        Email = Email,
        Address = Address,
        City = City,
        CommissionPercent = CommissionPercent,
        CommissionOnProfit = CommissionOnProfit,
        Notes = Notes,
        IsActive = _isActive,
        CreatedAt = _createdAt
    };
}
