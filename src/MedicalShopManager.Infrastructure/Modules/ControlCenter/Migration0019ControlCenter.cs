using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>
/// v19 — Control Center: per-user permission overrides, branding images (logo / signature, inside the database so
/// backups include them) and the ControlCenter.* settings with their defaults. Existing shops keep what they had:
/// the business-profile keys are seeded from the pre-6.2 ShopAddress / ShopGstin / LicenseNumber values.
/// Defaults are literal here (not taken from the C# records) so this migration never changes once released.
/// </summary>
public sealed class Migration0019ControlCenter : IMigration
{
    public int Version => 19;
    public string Description => "Control Center: user permissions, branding images, business/sale/invoice settings";

    private static readonly (string Key, string Value, string Description)[] Defaults =
    {
        ("ControlCenter.Business.LegalName", "", "Legal / trade name on GST records"),
        ("ControlCenter.Business.AddressLine2", "", "Address line 2"),
        ("ControlCenter.Business.City", "", "City / town"),
        ("ControlCenter.Business.Pincode", "", "PIN code"),
        ("ControlCenter.Business.Email", "", "Shop email"),
        ("ControlCenter.Business.Pan", "", "Business PAN"),
        ("ControlCenter.Business.DrugLicense21B", "", "Drug licence Form 21B"),
        ("ControlCenter.Business.DrugLicenseExpiry", "", "Drug licence expiry (yyyy-MM-dd)"),
        ("ControlCenter.Business.Fssai", "", "FSSAI licence number"),
        ("ControlCenter.Business.PharmacistRegistrationNo", "", "Pharmacist registration number"),
        ("ControlCenter.Business.BankName", "", "Bank name"),
        ("ControlCenter.Business.BankAccountName", "", "Bank account holder name"),
        ("ControlCenter.Business.BankAccountNumber", "", "Bank account number"),
        ("ControlCenter.Business.BankIfsc", "", "Bank IFSC"),
        ("ControlCenter.Business.BankBranch", "", "Bank branch"),
        ("ControlCenter.Business.UpiId", "", "UPI ID for the invoice payment QR"),

        ("ControlCenter.Sale.NewSaleIsCredit", "0", "New bills open as credit"),
        ("ControlCenter.Sale.DefaultDueDays", "0", "Default credit days for the due date"),
        ("ControlCenter.Sale.DefaultDoctorId", "", "Default doctor id on new bills"),
        ("ControlCenter.Sale.DefaultDoctorName", "", "Default doctor name on new bills"),
        ("ControlCenter.Sale.ExpiryCutoffMonths", "0", "Batches expiring within N months are not sold"),
        ("ControlCenter.Sale.NearExpiryMonths", "6", "Near-expiry highlight window in months"),
        ("ControlCenter.Sale.RoundOffMode", "1", "Bill round-off: 0 none, 1 nearest rupee, 2 nearest 50 paise"),
        ("ControlCenter.Sale.AllowNegativeStock", "0", "Allow billing more than stock on hand"),
        ("ControlCenter.Sale.AddSchemeWrtSaleQty", "0", "Compute schemes on the sold quantity"),
        ("ControlCenter.Sale.BarcodeQtyInPacks", "0", "Barcode scan adds packs instead of units"),
        ("ControlCenter.Sale.DefaultDiscountPercent", "0", "Default line discount %"),
        ("ControlCenter.Sale.MinDiscountPercent", "0", "Minimum bill discount %"),
        ("ControlCenter.Sale.MaxDiscountPercent", "100", "Maximum bill discount %"),
        ("ControlCenter.Sale.PatientReminders", "1", "Create refill follow-ups for chronic medicines"),
        ("ControlCenter.Sale.RequireDoctorForScheduleH", "0", "Require a doctor for Schedule H items"),
        ("ControlCenter.Sale.RequirePrescriptionReference", "0", "Require an Rx reference for prescription items"),

        ("ControlCenter.Invoice.ShowLogo", "1", "Invoice: shop logo"),
        ("ControlCenter.Invoice.ShowGstin", "1", "Invoice: shop GSTIN"),
        ("ControlCenter.Invoice.ShowDrugLicense", "1", "Invoice: drug licence numbers"),
        ("ControlCenter.Invoice.ShowFssai", "0", "Invoice: FSSAI number"),
        ("ControlCenter.Invoice.ShowEmail", "0", "Invoice: shop email"),
        ("ControlCenter.Invoice.ShowPan", "0", "Invoice: shop PAN"),
        ("ControlCenter.Invoice.ShowPatientAddress", "1", "Invoice: patient address"),
        ("ControlCenter.Invoice.ShowPatientAgeGender", "0", "Invoice: patient age and gender"),
        ("ControlCenter.Invoice.ShowDoctor", "1", "Invoice: doctor"),
        ("ControlCenter.Invoice.ShowCustomerGstin", "1", "Invoice: customer GSTIN"),
        ("ControlCenter.Invoice.ShowPreviousBalance", "0", "Invoice: previous balance"),
        ("ControlCenter.Invoice.ShowPlaceOfSupply", "0", "Invoice: place of supply"),
        ("ControlCenter.Invoice.ShowManufacturer", "1", "Invoice: manufacturer"),
        ("ControlCenter.Invoice.ShowComposition", "0", "Invoice: composition"),
        ("ControlCenter.Invoice.ShowSchedule", "1", "Invoice: drug schedule"),
        ("ControlCenter.Invoice.ShowHsn", "1", "Invoice: HSN code"),
        ("ControlCenter.Invoice.ShowBatch", "1", "Invoice: batch"),
        ("ControlCenter.Invoice.ShowExpiry", "1", "Invoice: expiry"),
        ("ControlCenter.Invoice.ShowPacking", "1", "Invoice: packing"),
        ("ControlCenter.Invoice.ShowRackLocation", "0", "Invoice: rack location"),
        ("ControlCenter.Invoice.ShowMrp", "1", "Invoice: MRP"),
        ("ControlCenter.Invoice.ShowDiscount", "1", "Invoice: discount %"),
        ("ControlCenter.Invoice.ShowGstRate", "1", "Invoice: GST %"),
        ("ControlCenter.Invoice.QtyInPacks", "0", "Invoice: quantity as packs + loose"),
        ("ControlCenter.Invoice.ShowGstSummary", "1", "Invoice: GST summary"),
        ("ControlCenter.Invoice.ShowAmountInWords", "1", "Invoice: amount in words"),
        ("ControlCenter.Invoice.ShowSavings", "1", "Invoice: savings"),
        ("ControlCenter.Invoice.ShowRoundOff", "1", "Invoice: round-off"),
        ("ControlCenter.Invoice.ShowPaymentMode", "1", "Invoice: payment mode"),
        ("ControlCenter.Invoice.ShowUpiQr", "0", "Invoice: UPI QR"),
        ("ControlCenter.Invoice.ShowBankDetails", "0", "Invoice: bank details"),
        ("ControlCenter.Invoice.ShowSignature", "1", "Invoice: signature"),
        ("ControlCenter.Invoice.ShowPharmacist", "1", "Invoice: pharmacist"),
        ("ControlCenter.Invoice.ShowTerms", "1", "Invoice: terms"),
        ("ControlCenter.Invoice.ShowBillingUser", "0", "Invoice: billed by"),
        ("ControlCenter.Invoice.PrintCopyLabels", "0", "Invoice: ORIGINAL / DUPLICATE label"),
        ("ControlCenter.Invoice.TermsText",
            "Goods once sold will not be taken back or exchanged without the bill. Medicines are not returnable after expiry. Subject to local jurisdiction.",
            "Invoice terms and conditions"),
        ("ControlCenter.Invoice.FooterNote", "Get well soon.", "Invoice footer note"),
        ("ControlCenter.Invoice.ContactNumber", "", "Phone printed on invoices (blank = shop phone)")
    };

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS ControlCenterUserPermissions (
                UserId          INTEGER NOT NULL REFERENCES Users(Id),
                PermissionKey   TEXT NOT NULL,
                IsPermitted     INTEGER NOT NULL CHECK (IsPermitted IN (0, 1)),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (UserId, PermissionKey)
            );
            CREATE TABLE IF NOT EXISTS ControlCenterAssets (
                AssetKey        TEXT PRIMARY KEY,
                ContentType     TEXT NOT NULL,
                Data            BLOB NOT NULL,
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
            );");

        foreach (var (key, value, description) in Defaults)
        {
            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES (@Key, @Value, @Desc);",
                ("@Key", key), ("@Value", value), ("@Desc", description));
        }

        // Carry the pre-6.2 single-field profile into the structured keys.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('ControlCenter.Business.AddressLine1',
                    COALESCE((SELECT Value FROM AppSettings WHERE Key = 'ShopAddress'), ''), 'Address line 1');
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('ControlCenter.Business.DrugLicense20B',
                    COALESCE((SELECT Value FROM AppSettings WHERE Key = 'LicenseNumber'), ''), 'Drug licence Form 20B');
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('ControlCenter.Business.Gstin',
                    UPPER(TRIM(COALESCE((SELECT Value FROM AppSettings WHERE Key = 'ShopGstin'), ''))), 'GSTIN as entered');
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('ControlCenter.Business.GstRegistrationType',
                    CASE WHEN LENGTH(TRIM(COALESCE((SELECT Value FROM AppSettings WHERE Key = 'ShopGstin'), ''))) = 15
                         THEN '1' ELSE '3' END,
                    'GST registration: 1 regular, 2 composition, 3 unregistered');
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('ControlCenter.Business.StateCode',
                    CASE WHEN LENGTH(TRIM(COALESCE((SELECT Value FROM AppSettings WHERE Key = 'ShopGstin'), ''))) = 15
                         THEN SUBSTR(TRIM((SELECT Value FROM AppSettings WHERE Key = 'ShopGstin')), 1, 2) ELSE '' END,
                    'GST state code of the shop');");
    }
}
