namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Every AppSettings key Control Center owns. Other modules read them through
/// <see cref="IControlCenterSettingsService"/> (typed) rather than by key; the list is here so the keys are documented
/// in one place. Booleans are "1"/"0", numbers use invariant culture, dates 'yyyy-MM-dd', enums their integer value
/// (except paper sizes, which the print service parses by name).
///
/// <para><b>Pre-6.2 keys</b> (kept because the shared print header and older screens read them):
/// ShopName, ShopAddress (composed one-line address), ShopPhone, ShopGstin (blank when unregistered),
/// LicenseNumber (composed "20B: …, 21B: …"), PharmacistName, DefaultGstRate, NearExpiryDays,
/// BackupRetentionDays, AutoBackupEnabled.</para>
/// <para><b>Print.PaperSize.&lt;TemplateName&gt;</b>: default paper size per print template (A4, A5, Thermal80mm, Thermal58mm).</para>
/// <para><b>ControlCenter.Business.*</b>, <b>ControlCenter.Sale.*</b>, <b>ControlCenter.Invoice.*</b>: see the constants below.</para>
/// Logo and signature images live in table ControlCenterAssets (keys <see cref="LogoAsset"/>, <see cref="SignatureAsset"/>),
/// so they are inside the database and therefore inside every backup.
/// </summary>
public static class ControlCenterSettingKeys
{
    // ─── Pre-6.2 keys ───
    public const string ShopName = "ShopName";
    public const string ShopAddress = "ShopAddress";
    public const string ShopPhone = "ShopPhone";
    public const string ShopGstin = "ShopGstin";
    public const string LicenseNumber = "LicenseNumber";
    public const string PharmacistName = "PharmacistName";
    public const string DefaultGstRate = "DefaultGstRate";
    public const string NearExpiryDays = "NearExpiryDays";
    public const string BackupRetentionDays = "BackupRetentionDays";
    public const string AutoBackupEnabled = "AutoBackupEnabled";

    public const string PaperSizePrefix = "Print.PaperSize.";

    // ─── Business profile ───
    private const string B = "ControlCenter.Business.";
    public const string LegalName = B + "LegalName";
    public const string AddressLine1 = B + "AddressLine1";
    public const string AddressLine2 = B + "AddressLine2";
    public const string City = B + "City";
    public const string Pincode = B + "Pincode";
    public const string StateCode = B + "StateCode";
    public const string Email = B + "Email";
    public const string GstRegistrationType = B + "GstRegistrationType";
    /// <summary>GSTIN as typed (ShopGstin holds the effective one, blank for unregistered shops).</summary>
    public const string Gstin = B + "Gstin";
    public const string Pan = B + "Pan";
    public const string DrugLicense20B = B + "DrugLicense20B";
    public const string DrugLicense21B = B + "DrugLicense21B";
    public const string DrugLicenseExpiry = B + "DrugLicenseExpiry";
    public const string Fssai = B + "Fssai";
    public const string PharmacistRegistrationNo = B + "PharmacistRegistrationNo";
    public const string BankName = B + "BankName";
    public const string BankAccountName = B + "BankAccountName";
    public const string BankAccountNumber = B + "BankAccountNumber";
    public const string BankIfsc = B + "BankIfsc";
    public const string BankBranch = B + "BankBranch";
    public const string UpiId = B + "UpiId";

    // ─── Sale settings ───
    private const string S = "ControlCenter.Sale.";
    public const string NewSaleIsCredit = S + "NewSaleIsCredit";
    public const string DefaultDueDays = S + "DefaultDueDays";
    public const string DefaultDoctorId = S + "DefaultDoctorId";
    public const string DefaultDoctorName = S + "DefaultDoctorName";
    public const string ExpiryCutoffMonths = S + "ExpiryCutoffMonths";
    public const string NearExpiryMonths = S + "NearExpiryMonths";
    public const string RoundOffMode = S + "RoundOffMode";
    public const string AllowNegativeStock = S + "AllowNegativeStock";
    public const string AddSchemeWrtSaleQty = S + "AddSchemeWrtSaleQty";
    public const string BarcodeQtyInPacks = S + "BarcodeQtyInPacks";
    public const string DefaultDiscountPercent = S + "DefaultDiscountPercent";
    public const string MinDiscountPercent = S + "MinDiscountPercent";
    public const string MaxDiscountPercent = S + "MaxDiscountPercent";
    public const string PatientReminders = S + "PatientReminders";
    public const string RequireDoctorForScheduleH = S + "RequireDoctorForScheduleH";
    public const string RequirePrescriptionReference = S + "RequirePrescriptionReference";

    // ─── Invoice print settings ───
    public const string InvoiceFieldPrefix = "ControlCenter.Invoice.";
    public const string InvoiceTermsText = InvoiceFieldPrefix + "TermsText";
    public const string InvoiceFooterNote = InvoiceFieldPrefix + "FooterNote";
    public const string InvoiceContactNumber = InvoiceFieldPrefix + "ContactNumber";

    // ─── Branding images (table ControlCenterAssets) ───
    public const string LogoAsset = "Logo";
    public const string SignatureAsset = "Signature";

    public static string PaperSizeKey(string templateName) => PaperSizePrefix + templateName;
    public static string InvoiceFieldKey(string field) => InvoiceFieldPrefix + field;

    public static IReadOnlyList<string> BusinessKeys { get; } = new[]
    {
        ShopName, ShopAddress, ShopPhone, ShopGstin, LicenseNumber, PharmacistName,
        LegalName, AddressLine1, AddressLine2, City, Pincode, StateCode, Email, GstRegistrationType, Gstin, Pan,
        DrugLicense20B, DrugLicense21B, DrugLicenseExpiry, Fssai, PharmacistRegistrationNo,
        BankName, BankAccountName, BankAccountNumber, BankIfsc, BankBranch, UpiId
    };

    public static IReadOnlyList<string> SaleKeys { get; } = new[]
    {
        NewSaleIsCredit, DefaultDueDays, DefaultDoctorId, DefaultDoctorName, ExpiryCutoffMonths, NearExpiryMonths,
        RoundOffMode, AllowNegativeStock, AddSchemeWrtSaleQty, BarcodeQtyInPacks, DefaultDiscountPercent,
        MinDiscountPercent, MaxDiscountPercent, PatientReminders, RequireDoctorForScheduleH, RequirePrescriptionReference
    };

    public static IReadOnlyList<string> InvoiceKeys { get; } =
        InvoiceField.All.Select(f => InvoiceFieldKey(f.Key))
            .Concat(new[] { InvoiceTermsText, InvoiceFooterNote, InvoiceContactNumber })
            .ToArray();

    public static IReadOnlyList<string> GeneralKeys { get; } = new[]
    {
        DefaultGstRate, NearExpiryDays, BackupRetentionDays, AutoBackupEnabled
    };
}
