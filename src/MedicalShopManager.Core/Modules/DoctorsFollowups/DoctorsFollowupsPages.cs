namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Page keys and navigation parameters of the DoctorsFollowups module, so other modules can open its pages:
/// <c>INavigationService.NavigateTo(DoctorsFollowupsPages.Followups, new OpenFollowupsRequest(customerId))</c>.
/// </summary>
public static class DoctorsFollowupsPages
{
    /// <summary>Sales ▸ Doctors (doctor master).</summary>
    public const string Doctors = "Doctors";

    /// <summary>Sales ▸ Doctor-wise Sales.</summary>
    public const string DoctorSales = "DoctorSales";

    /// <summary>Dashboard ▸ Patient Follow-ups (refill reminders). Accepts <see cref="OpenFollowupsRequest"/>.</summary>
    public const string Followups = "PatientFollowups";

    /// <summary>Refill rules (which compositions / medicines create follow-ups) and follow-up settings.</summary>
    public const string RefillRules = "FollowupRefillRules";
}

/// <summary>Opens the follow-ups page on a customer and / or a follow-up (dashboard "Follow Up &gt;").</summary>
public sealed record OpenFollowupsRequest(long? CustomerId = null, long? FollowupId = null, FollowupTab Tab = FollowupTab.Arriving);
