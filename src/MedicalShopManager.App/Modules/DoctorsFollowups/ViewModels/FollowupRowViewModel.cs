using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>A row of the follow-up list: the follow-up plus its tick box for bulk WhatsApp / Followed up.</summary>
public sealed partial class FollowupRowViewModel : ObservableObject
{
    public FollowupRowViewModel(PatientFollowup followup) => Followup = followup;

    public PatientFollowup Followup { get; }

    [ObservableProperty] private bool _isChecked;
}
