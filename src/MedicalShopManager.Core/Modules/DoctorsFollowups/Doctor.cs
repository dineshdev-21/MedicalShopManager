namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Doctor master (Localwell <c>doctors_add_doctor</c>: name, reg. no, specialization, hospital / clinic, mobile,
/// address, commission %, commission on profit). Stored in the Doctors table; Sales.DoctorId points here.
/// Doctors are never deleted once bills point at them: <see cref="IsActive"/> = false hides them from look-ups.
/// </summary>
public sealed record Doctor
{
    public long Id { get; init; }

    /// <summary>Name without the "Dr." prefix (added by <see cref="DisplayName"/>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Medical council registration number (NMC / state council), unique when given.</summary>
    public string? RegistrationNo { get; init; }

    public string? Qualification { get; init; }
    public string? Specialization { get; init; }

    /// <summary>Hospital or clinic name.</summary>
    public string? ClinicName { get; init; }

    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }

    /// <summary>Commission percent (0–100) on the doctor's referred sales; 0 = none.</summary>
    public decimal CommissionPercent { get; init; }

    /// <summary>True: commission on profit (taxable − cost); false: on the taxable sale value.</summary>
    public bool CommissionOnProfit { get; init; }

    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Active bills that name this doctor (read model; never written).</summary>
    public int BillCount { get; init; }

    /// <summary>Date of the latest active bill (read model).</summary>
    public DateTime? LastBillDate { get; init; }

    public string DisplayName => DoctorRules.DisplayName(Name);

    /// <summary>"MBBS, MD · Cardiologist · City Clinic".</summary>
    public string Subtitle => string.Join(" · ", new[] { Qualification, Specialization, ClinicName }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string CommissionLabel => CommissionPercent <= 0
        ? "-"
        : $"{CommissionPercent:0.##}% on {(CommissionOnProfit ? "profit" : "sales")}";

    public string StatusLabel => IsActive ? "Active" : "Inactive";

    public DoctorLookupItem ToLookupItem() =>
        new(Id, Name, RegistrationNo, Qualification, Specialization, ClinicName, Phone, IsActive);
}

/// <summary>What the billing screen needs to show and store for a doctor (Sales.DoctorId / DoctorName / DoctorRegNo).</summary>
public sealed record DoctorLookupItem(
    long Id,
    string Name,
    string? RegistrationNo,
    string? Qualification,
    string? Specialization,
    string? ClinicName,
    string? Phone,
    bool IsActive = true)
{
    public string DisplayName => DoctorRules.DisplayName(Name);

    /// <summary>"Dr. A Sharma (Reg. KMC 1234) · Cardiologist".</summary>
    public string Label
    {
        get
        {
            var text = DisplayName;
            if (!string.IsNullOrWhiteSpace(RegistrationNo)) text += $" (Reg. {RegistrationNo})";
            if (!string.IsNullOrWhiteSpace(Specialization)) text += $" · {Specialization}";
            return text;
        }
    }
}

/// <summary>Minimal doctor added from another screen (billing's "Select Doctor" modal).</summary>
public sealed record DoctorQuickAdd(
    string Name,
    string? RegistrationNo = null,
    string? Specialization = null,
    string? Phone = null,
    string? ClinicName = null);
