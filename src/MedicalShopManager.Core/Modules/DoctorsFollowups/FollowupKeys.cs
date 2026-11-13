using System.Text.RegularExpressions;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Keys that decide when two sales are "the same patient, same medicine" (a newer bill then replaces the older
/// follow-up). The item key prefers the composition so a brand switch (Glycomet → Gluconorm) still counts as the
/// refill; items without a composition key on the medicine.
/// </summary>
public static partial class FollowupKeys
{
    public const int MaxNoteLength = 1000;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>"C12" for composition 12, else "M34" for medicine 34.</summary>
    public static string ItemKey(long? compositionId, long medicineId) =>
        compositionId is > 0 ? "C" + compositionId.Value : "M" + medicineId;

    /// <summary>
    /// Normalised patient: lower case, single spaces; "" when the bill names no separate patient
    /// (blank, "Self", or the customer's own name).
    /// </summary>
    public static string PatientKey(string? patientName, string? customerName)
    {
        var patient = Normalize(patientName);
        if (patient.Length == 0 || patient == "self") return string.Empty;
        return patient == Normalize(customerName) ? string.Empty : patient;
    }

    /// <summary>Two letters for the avatar ("Ramesh Kumar" → "RK", "Anita" → "AN").</summary>
    public static string Initials(string? name)
    {
        var parts = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Spaces().Replace(text.Trim(), " ").ToLowerInvariant();
}
