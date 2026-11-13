namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// The shop's legal and contact identity, printed on every invoice (Control Center ▸ Business Profile,
/// Localwell <c>business_update_business_profile</c>). Read through <see cref="IControlCenterSettingsService"/>.
/// Keys are listed in <see cref="ControlCenterSettingKeys"/>; the pre-6.2 keys (ShopName, ShopAddress, ShopPhone,
/// ShopGstin, LicenseNumber, PharmacistName) are still written so the shared print header keeps working.
/// </summary>
public sealed record BusinessProfile
{
    public string ShopName { get; init; } = "My Medical Shop";

    /// <summary>Legal / trade name on GST records when it differs from the shop name (optional).</summary>
    public string LegalName { get; init; } = string.Empty;

    public string AddressLine1 { get; init; } = string.Empty;
    public string AddressLine2 { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Pincode { get; init; } = string.Empty;

    /// <summary>Two-digit GST state code (see <see cref="IndianStates"/>); decides CGST+SGST vs IGST.</summary>
    public string StateCode { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;

    public GstRegistrationType GstRegistrationType { get; init; } = GstRegistrationType.Regular;
    public string Gstin { get; init; } = string.Empty;
    public string Pan { get; init; } = string.Empty;

    /// <summary>Drug licence, Form 20B (retail sale of drugs other than Schedule C/C1/X).</summary>
    public string DrugLicense20B { get; init; } = string.Empty;

    /// <summary>Drug licence, Form 21B (retail sale of Schedule C/C1 drugs).</summary>
    public string DrugLicense21B { get; init; } = string.Empty;

    /// <summary>Expiry of the drug licences (renewed together in most states); null when not entered.</summary>
    public DateTime? DrugLicenseExpiry { get; init; }

    /// <summary>FSSAI licence / registration number (14 digits) for food and nutraceutical items.</summary>
    public string Fssai { get; init; } = string.Empty;

    public string PharmacistName { get; init; } = string.Empty;

    /// <summary>State Pharmacy Council registration number of the registered pharmacist.</summary>
    public string PharmacistRegistrationNo { get; init; } = string.Empty;

    public string BankName { get; init; } = string.Empty;
    public string BankAccountName { get; init; } = string.Empty;
    public string BankAccountNumber { get; init; } = string.Empty;
    public string BankIfsc { get; init; } = string.Empty;
    public string BankBranch { get; init; } = string.Empty;

    /// <summary>UPI VPA (e.g. shop@okbank) used for the invoice payment QR (<see cref="UpiPaymentLink"/>).</summary>
    public string UpiId { get; init; } = string.Empty;

    public IndianState? State => IndianStates.Find(StateCode);

    /// <summary>One-line address for the print header (legacy key ShopAddress).</summary>
    public string FullAddress
    {
        get
        {
            var cityPin = string.Join(" - ", new[] { City, Pincode }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            var parts = new[] { AddressLine1, AddressLine2, cityPin, State?.Name }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim());
            return string.Join(", ", parts);
        }
    }

    /// <summary>Drug licence text for the print header (legacy key LicenseNumber), e.g. "20B: KA-1, 21B: KA-2".</summary>
    public string DrugLicenseDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DrugLicense20B)) parts.Add($"20B: {DrugLicense20B.Trim()}");
            if (!string.IsNullOrWhiteSpace(DrugLicense21B)) parts.Add($"21B: {DrugLicense21B.Trim()}");
            return string.Join(", ", parts);
        }
    }

    /// <summary>GSTIN to print; an unregistered shop prints none even if one was typed earlier.</summary>
    public string EffectiveGstin => GstRegistrationType.RequiresGstin() ? Gstin.Trim().ToUpperInvariant() : string.Empty;

    /// <summary>"Tax Invoice" for regular dealers, else "Bill of Supply".</summary>
    public string SaleDocumentTitle => GstRegistrationType.SaleDocumentTitle();

    public bool HasBankDetails => !string.IsNullOrWhiteSpace(BankAccountNumber) && !string.IsNullOrWhiteSpace(BankIfsc);
}
