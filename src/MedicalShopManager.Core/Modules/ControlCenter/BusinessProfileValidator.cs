namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Validation and normalisation of <see cref="BusinessProfile"/> before it is saved.
/// Rules (standard Indian retail-pharmacy practice; Localwell validates the GSTIN server side):
/// shop name and state required; GSTIN required (and must match the state) for regular and composition dealers;
/// PAN must match the GSTIN's embedded PAN; optional fields are checked only when filled in.
/// </summary>
public static class BusinessProfileValidator
{
    public const int MaxNameLength = 100;

    /// <summary>Trims every field and upper-cases GSTIN / PAN / IFSC; derives PAN from the GSTIN when blank.</summary>
    public static BusinessProfile Normalize(BusinessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var gstin = IndianIdentifiers.Normalize(profile.Gstin);
        var pan = IndianIdentifiers.Normalize(profile.Pan);
        if (pan.Length == 0 && gstin.Length == 15) pan = IndianIdentifiers.PanFromGstin(gstin);

        return profile with
        {
            ShopName = profile.ShopName.Trim(),
            LegalName = profile.LegalName.Trim(),
            AddressLine1 = profile.AddressLine1.Trim(),
            AddressLine2 = profile.AddressLine2.Trim(),
            City = profile.City.Trim(),
            Pincode = profile.Pincode.Trim(),
            StateCode = IndianStates.Find(profile.StateCode)?.Code ?? profile.StateCode.Trim(),
            Phone = profile.Phone.Trim(),
            Email = profile.Email.Trim(),
            Gstin = gstin,
            Pan = pan,
            DrugLicense20B = profile.DrugLicense20B.Trim(),
            DrugLicense21B = profile.DrugLicense21B.Trim(),
            DrugLicenseExpiry = profile.DrugLicenseExpiry?.Date,
            Fssai = profile.Fssai.Trim(),
            PharmacistName = profile.PharmacistName.Trim(),
            PharmacistRegistrationNo = profile.PharmacistRegistrationNo.Trim(),
            BankName = profile.BankName.Trim(),
            BankAccountName = profile.BankAccountName.Trim(),
            BankAccountNumber = profile.BankAccountNumber.Trim(),
            BankIfsc = IndianIdentifiers.Normalize(profile.BankIfsc),
            BankBranch = profile.BankBranch.Trim(),
            UpiId = profile.UpiId.Trim()
        };
    }

    /// <summary>Validates an already normalised profile. Empty list = valid.</summary>
    public static IReadOnlyList<SettingsError> Validate(BusinessProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<SettingsError>();
        void Add(string field, string message) => errors.Add(new SettingsError(field, message));

        if (string.IsNullOrWhiteSpace(p.ShopName)) Add(nameof(p.ShopName), "Shop name is required.");
        else if (p.ShopName.Length > MaxNameLength) Add(nameof(p.ShopName), $"Shop name must be at most {MaxNameLength} characters.");
        if (p.LegalName.Length > MaxNameLength) Add(nameof(p.LegalName), $"Legal name must be at most {MaxNameLength} characters.");

        if (!IndianStates.IsValidCode(p.StateCode))
            Add(nameof(p.StateCode), "Select the state the shop is registered in (it decides CGST + SGST or IGST).");
        if (p.Pincode.Length > 0 && !IndianIdentifiers.IsValidPincode(p.Pincode))
            Add(nameof(p.Pincode), "PIN code must be 6 digits and cannot start with 0.");
        if (p.Phone.Length > 0 && !IndianIdentifiers.IsValidPhone(p.Phone))
            Add(nameof(p.Phone), "Enter a 10-digit mobile number or a landline with STD code.");
        if (p.Email.Length > 0 && !IndianIdentifiers.IsValidEmail(p.Email))
            Add(nameof(p.Email), "Email address is not valid.");

        if (!Enum.IsDefined(p.GstRegistrationType))
            Add(nameof(p.GstRegistrationType), "Select a GST registration type.");
        else if (p.GstRegistrationType.RequiresGstin())
        {
            if (p.Gstin.Length == 0)
                Add(nameof(p.Gstin), "GSTIN is required for regular and composition dealers. Choose 'Unregistered' if the shop has no GSTIN.");
            else if (IndianIdentifiers.ValidateGstin(p.Gstin) is { } gstinError)
                Add(nameof(p.Gstin), gstinError);
            else if (IndianStates.IsValidCode(p.StateCode) && p.Gstin[..2] != p.StateCode)
                Add(nameof(p.Gstin), $"GSTIN starts with state code {p.Gstin[..2]} but the shop state is {p.StateCode}.");
        }
        else if (p.Gstin.Length > 0 && IndianIdentifiers.ValidateGstin(p.Gstin) is { } unregisteredGstinError)
        {
            Add(nameof(p.Gstin), unregisteredGstinError);
        }

        if (p.Pan.Length > 0)
        {
            if (!IndianIdentifiers.IsValidPan(p.Pan)) Add(nameof(p.Pan), "PAN format is invalid (expected e.g. AAPFU0939F).");
            else if (p.Gstin.Length == 15 && IndianIdentifiers.PanFromGstin(p.Gstin) != p.Pan)
                Add(nameof(p.Pan), "PAN does not match the PAN inside the GSTIN.");
        }

        if (p.DrugLicense20B.Length > 40) Add(nameof(p.DrugLicense20B), "Drug licence 20B must be at most 40 characters.");
        if (p.DrugLicense21B.Length > 40) Add(nameof(p.DrugLicense21B), "Drug licence 21B must be at most 40 characters.");
        if (p.Fssai.Length > 0 && !IndianIdentifiers.IsValidFssai(p.Fssai))
            Add(nameof(p.Fssai), "FSSAI number must be 14 digits.");

        var anyBank = p.BankAccountNumber.Length > 0 || p.BankIfsc.Length > 0 || p.BankName.Length > 0;
        if (anyBank)
        {
            if (!IndianIdentifiers.IsValidBankAccount(p.BankAccountNumber))
                Add(nameof(p.BankAccountNumber), "Bank account number must be 9 to 18 digits.");
            if (!IndianIdentifiers.IsValidIfsc(p.BankIfsc))
                Add(nameof(p.BankIfsc), "IFSC must be 11 characters: 4 letters, 0, then 6 letters or digits (e.g. SBIN0001234).");
        }
        if (p.UpiId.Length > 0 && !IndianIdentifiers.IsValidUpiId(p.UpiId))
            Add(nameof(p.UpiId), "UPI ID is not valid (expected e.g. myshop@okaxis).");

        return errors;
    }

    /// <summary>Licence expiry reminders: true when a licence expires within <paramref name="days"/> of <paramref name="today"/>.</summary>
    public static bool IsDrugLicenseExpiringSoon(BusinessProfile profile, DateTime today, int days = 30) =>
        profile.DrugLicenseExpiry is { } expiry && expiry.Date <= today.Date.AddDays(days);
}
