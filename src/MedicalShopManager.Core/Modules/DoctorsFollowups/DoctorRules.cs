using System.Text.RegularExpressions;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>Normalisation and validation of the doctor master (pure; used by the service before every save).</summary>
public static partial class DoctorRules
{
    public const int MaxNameLength = 100;
    public const int MaxRegistrationLength = 30;
    public const int MaxTextLength = 200;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "Dr", "Dr.", "Dr ", "DR." at the start of the name.
    [GeneratedRegex(@"^dr\.?(\s+|\.)", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorPrefix();

    // Council registration numbers: letters, digits, spaces and / - . (e.g. "KMC 45678", "DMC/R/1234", "MCI-12345").
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 /.\-]*$")]
    private static partial Regex RegistrationPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    /// <summary>"Dr. " + name (the stored name never carries the prefix).</summary>
    public static string DisplayName(string? name)
    {
        var clean = StripPrefix(name);
        return clean.Length == 0 ? string.Empty : "Dr. " + clean;
    }

    /// <summary>Trims, collapses spaces and removes a leading "Dr." so "Dr. A  Sharma" and "A Sharma" are the same doctor.</summary>
    public static string StripPrefix(string? name)
    {
        var text = CollapseSpaces(name);
        // A name that is only the prefix ("Dr.") is no name at all: it becomes blank and fails validation.
        return DoctorPrefix().Replace(text, string.Empty, 1).Trim();
    }

    public static string CollapseSpaces(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Spaces().Replace(text.Trim(), " ");

    /// <summary>A normalised copy: trimmed text, null for blanks, registration number upper-case, phone digits only.</summary>
    public static Doctor Normalize(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        return doctor with
        {
            Name = StripPrefix(doctor.Name),
            RegistrationNo = Blank(CollapseSpaces(doctor.RegistrationNo).ToUpperInvariant()),
            Qualification = Blank(CollapseSpaces(doctor.Qualification)),
            Specialization = Blank(CollapseSpaces(doctor.Specialization)),
            ClinicName = Blank(CollapseSpaces(doctor.ClinicName)),
            Phone = Blank(FollowupMessageBuilder.NormalizeIndianPhone(doctor.Phone) ?? CollapseSpaces(doctor.Phone)),
            Email = Blank(doctor.Email?.Trim()),
            Address = Blank(doctor.Address?.Trim()),
            City = Blank(CollapseSpaces(doctor.City)),
            Notes = Blank(doctor.Notes?.Trim()),
            CommissionPercent = Math.Round(doctor.CommissionPercent, 2, MidpointRounding.AwayFromZero)
        };
    }

    /// <summary>User-facing problems with a (normalised) doctor; empty when it can be saved.</summary>
    public static IReadOnlyList<string> Validate(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        var errors = new List<string>();
        var name = StripPrefix(doctor.Name);
        if (name.Length == 0) errors.Add("Enter the doctor's name.");
        else if (name.Length < 2) errors.Add("The doctor's name is too short.");
        else if (name.Length > MaxNameLength) errors.Add($"The doctor's name can have at most {MaxNameLength} characters.");

        if (!string.IsNullOrWhiteSpace(doctor.RegistrationNo))
        {
            var reg = doctor.RegistrationNo.Trim();
            if (reg.Length > MaxRegistrationLength)
                errors.Add($"The registration number can have at most {MaxRegistrationLength} characters.");
            else if (!RegistrationPattern().IsMatch(reg))
                errors.Add("The registration number can contain only letters, digits, spaces and / - .");
        }

        if (!string.IsNullOrWhiteSpace(doctor.Phone) && FollowupMessageBuilder.NormalizeIndianPhone(doctor.Phone) is null)
            errors.Add("Enter a 10-digit mobile number, or a landline with its STD code.");

        if (!string.IsNullOrWhiteSpace(doctor.Email) && !EmailPattern().IsMatch(doctor.Email.Trim()))
            errors.Add("Enter a valid e-mail address.");

        if (doctor.CommissionPercent < 0 || doctor.CommissionPercent > 100)
            errors.Add("Commission must be between 0 and 100 %.");
        if (doctor.CommissionOnProfit && doctor.CommissionPercent <= 0)
            errors.Add("Enter the commission % to pay it on profit, or untick 'on profit'.");

        foreach (var (label, value) in new[]
                 {
                     ("Qualification", doctor.Qualification), ("Specialization", doctor.Specialization),
                     ("Hospital / clinic", doctor.ClinicName), ("City", doctor.City)
                 })
        {
            if (value is { Length: > MaxTextLength }) errors.Add($"{label} can have at most {MaxTextLength} characters.");
        }

        return errors;
    }

    /// <summary>A doctor for a quick add from another screen (active, no commission).</summary>
    public static Doctor FromQuickAdd(DoctorQuickAdd add)
    {
        ArgumentNullException.ThrowIfNull(add);
        return new Doctor
        {
            Name = add.Name,
            RegistrationNo = add.RegistrationNo,
            Specialization = add.Specialization,
            Phone = add.Phone,
            ClinicName = add.ClinicName,
            IsActive = true
        };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
