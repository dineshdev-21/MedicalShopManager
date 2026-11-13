using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>
/// Full customer record as the billing screen uses it (spec 02 §C/§D, data model "Customer"): the pre-6.2
/// <c>Customers</c> columns plus the Billing migration's individual / business fields. Money is paisa.
/// </summary>
public sealed class BillingCustomer
{
    public long Id { get; set; }
    public CustomerKind Kind { get; set; } = CustomerKind.Individual;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    // Individual
    public PersonGender Gender { get; set; }
    public DateTime? DateOfBirth { get; set; }

    // Business
    public string? Gstin { get; set; }
    /// <summary>Derived from the GSTIN (characters 3–12) on save.</summary>
    public string? Pan { get; set; }
    public bool IsCompositionDealer { get; set; }

    /// <summary>Default sales discount % applied to this customer's lines (0–100).</summary>
    public decimal DefaultDiscountPercent { get; set; }
    public string? ReferenceNo { get; set; }
    /// <summary>Positive = the customer owes the shop.</summary>
    public long OpeningBalancePaisa { get; set; }
    public DateTime? OpeningBalanceDate { get; set; }
    /// <summary>Default credit period; the payment step proposes invoice date + credit days as the due date.</summary>
    public int CreditDays { get; set; }
    /// <summary>Preferred doctor (pre-6.2 column), pre-fills the bill's doctor.</summary>
    public string? DoctorName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Opening balance + unpaid credit sales, derived in SQL from source rows (never stored).</summary>
    public long OutstandingBalancePaisa { get; set; }
    public int InvoiceCount { get; set; }
    public DateTime? LastSaleDate { get; set; }

    public bool IsBusiness => Kind == CustomerKind.Business;
    public decimal OutstandingBalance => OutstandingBalancePaisa / 100m;
    public string KindLabel => Kind == CustomerKind.Business ? "Business" : "Individual";
    public int? AgeYears => DateOfBirth is { } dob ? BillingCustomerRules.AgeInYears(dob, DateTime.Today) : null;

    /// <summary>"To Receive ₹x" / "To Pay ₹x" / "-" as the customer picker shows the balance.</summary>
    public string BalanceLabel => OutstandingBalancePaisa switch
    {
        > 0 => $"To Receive ₹{OutstandingBalancePaisa / 100m:N2}",
        < 0 => $"To Pay ₹{-OutstandingBalancePaisa / 100m:N2}",
        _ => "-"
    };

    public BillingCustomer Clone() => (BillingCustomer)MemberwiseClone();
}

/// <summary>Validation and derived fields for <see cref="BillingCustomer"/> (spec 02 §D messages).</summary>
public static class BillingCustomerRules
{
    public const int GstinLength = 15;
    public const int MobileLength = 10;

    /// <summary>User-facing errors; empty when the customer can be saved.</summary>
    public static IReadOnlyList<string> Validate(BillingCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(customer.Name))
            errors.Add("Please fill Customer Name");
        else if (customer.Name.Trim().Length > 120)
            errors.Add("Customer Name must be 120 characters or fewer");

        var phone = customer.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone) && (phone.Length != MobileLength || !phone.All(char.IsAsciiDigit)))
            errors.Add("Mobile number must be 10 digits");

        var gstin = customer.Gstin?.Trim();
        if (customer.Kind == CustomerKind.Business && !string.IsNullOrEmpty(gstin))
        {
            if (gstin.Length != GstinLength)
                errors.Add("Customer GSTIN must be 15 characters long");
            else if (!IsGstinFormat(gstin))
                errors.Add("Customer GSTIN is not valid (expected 2-digit state code, 10-character PAN, entity code, 'Z', check digit)");
        }

        if (customer.DefaultDiscountPercent is < 0m or > 100m)
            errors.Add("Sales Discount must be between 0% and 100%");
        if (customer.CreditDays is < 0 or > 365)
            errors.Add("Credit days must be between 0 and 365");
        if (customer.DateOfBirth is { } dob && dob.Date > DateTime.Today)
            errors.Add("Date of birth cannot be in the future");
        if (customer.OpeningBalancePaisa != 0 && customer.OpeningBalanceDate is null)
            errors.Add("Please fill Opening Balance Date");
        if (!string.IsNullOrWhiteSpace(customer.Email) && !customer.Email.Contains('@'))
            errors.Add("Email address is not valid");
        return errors;
    }

    /// <summary>Normalises text fields (trim, upper-case GSTIN) and derives PAN; returns a new instance.</summary>
    public static BillingCustomer Normalize(BillingCustomer customer)
    {
        var copy = customer.Clone();
        copy.Name = customer.Name.Trim();
        copy.Phone = Blank(customer.Phone);
        copy.Email = Blank(customer.Email);
        copy.Address = Blank(customer.Address);
        copy.ReferenceNo = Blank(customer.ReferenceNo);
        copy.DoctorName = Blank(customer.DoctorName);
        copy.Gstin = Blank(customer.Gstin)?.ToUpperInvariant();
        copy.Pan = PanFromGstin(copy.Gstin);
        if (copy.Kind == CustomerKind.Individual)
        {
            copy.Gstin = null;
            copy.Pan = null;
            copy.IsCompositionDealer = false;
        }
        else
        {
            copy.Gender = PersonGender.Unspecified;
            copy.DateOfBirth = null;
        }
        return copy;
    }

    /// <summary>GSTIN pattern: 2 digits, 5 letters, 4 digits, 1 letter, 1 alphanumeric, 'Z', 1 alphanumeric.</summary>
    public static bool IsGstinFormat(string gstin)
    {
        if (gstin.Length != GstinLength) return false;
        var g = gstin.ToUpperInvariant();
        return char.IsAsciiDigit(g[0]) && char.IsAsciiDigit(g[1])
            && g[2..7].All(char.IsAsciiLetter) && g[7..11].All(char.IsAsciiDigit) && char.IsAsciiLetter(g[11])
            && char.IsAsciiLetterOrDigit(g[12]) && g[13] == 'Z' && char.IsAsciiLetterOrDigit(g[14]);
    }

    /// <summary>PAN is characters 3–12 of a GSTIN (spec 02 §D "PAN NUMBER").</summary>
    public static string? PanFromGstin(string? gstin) =>
        gstin is { Length: GstinLength } ? gstin.Substring(2, 10).ToUpperInvariant() : null;

    public static int AgeInYears(DateTime dateOfBirth, DateTime today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth.Date > today.AddYears(-age)) age--;
        return Math.Max(age, 0);
    }

    /// <summary>Approximate date of birth from an age in years + months (spec 02 §D "auto-fills DOB approx.").</summary>
    public static DateTime DateOfBirthFromAge(int years, int months, DateTime today)
    {
        if (years < 0 || months < 0 || months > 11) throw new ArgumentOutOfRangeException(nameof(years), "Age must be positive with 0–11 months.");
        return today.Date.AddYears(-years).AddMonths(-months);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Small display helpers shared by billing grids and prints.</summary>
public static class BillingFormat
{
    /// <summary>"3 + 5" style quantity for a unit count.</summary>
    public static string Quantity(long units, int unitsPerPack) => PackQuantity.Format(units, Math.Max(unitsPerPack, 1));

    public static decimal Rupees(long paisa) => paisa / 100m;
}
