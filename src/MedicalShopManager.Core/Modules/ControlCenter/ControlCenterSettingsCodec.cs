using System.Globalization;
using MedicalShopManager.Core.Enums;
using K = MedicalShopManager.Core.Modules.ControlCenter.ControlCenterSettingKeys;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Converts the typed settings records to and from AppSettings key/value text (pure, so it is unit tested without a
/// database). Reading is forgiving: a missing or unparsable value falls back to the record's default.
/// </summary>
public static class ControlCenterSettingsCodec
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────── primitives ─────────────

    public static string Bool(bool value) => value ? "1" : "0";
    public static string Number(decimal value) => value.ToString("0.####", Inv);
    public static string Number(long value) => value.ToString(Inv);

    private static string Text(IReadOnlyDictionary<string, string> v, string key, string fallback = "") =>
        v.TryGetValue(key, out var s) && s != null ? s : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> v, string key, bool fallback) =>
        v.TryGetValue(key, out var s) ? s?.Trim() switch
        {
            "1" or "true" or "True" or "TRUE" => true,
            "0" or "false" or "False" or "FALSE" => false,
            _ => fallback
        } : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> v, string key, int fallback) =>
        v.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, Inv, out var n) ? n : fallback;

    private static long? ReadLong(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, Inv, out var n) ? n : null;

    private static decimal ReadDecimal(IReadOnlyDictionary<string, string> v, string key, decimal fallback) =>
        v.TryGetValue(key, out var s) && decimal.TryParse(s, NumberStyles.Number, Inv, out var d) ? d : fallback;

    private static DateTime? ReadDate(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;

    private static TEnum ReadEnum<TEnum>(IReadOnlyDictionary<string, string> v, string key, TEnum fallback) where TEnum : struct, Enum
    {
        if (!v.TryGetValue(key, out var s) || !int.TryParse(s, NumberStyles.Integer, Inv, out var n)) return fallback;
        var value = (TEnum)Enum.ToObject(typeof(TEnum), n);
        return Enum.IsDefined(value) ? value : fallback;
    }

    // ───────────── business profile ─────────────

    public static BusinessProfile ReadBusinessProfile(IReadOnlyDictionary<string, string> v)
    {
        var defaults = new BusinessProfile();
        var registration = ReadEnum(v, K.GstRegistrationType, defaults.GstRegistrationType);
        // Before Control Center existed only ShopGstin / ShopAddress / LicenseNumber were stored: fall back to them.
        var gstin = Text(v, K.Gstin);
        if (gstin.Length == 0) gstin = Text(v, K.ShopGstin);
        var line1 = Text(v, K.AddressLine1);
        if (line1.Length == 0 && Text(v, K.AddressLine2).Length == 0 && Text(v, K.City).Length == 0)
            line1 = Text(v, K.ShopAddress);
        var dl20 = Text(v, K.DrugLicense20B);
        if (dl20.Length == 0 && Text(v, K.DrugLicense21B).Length == 0)
            dl20 = Text(v, K.LicenseNumber);

        return new BusinessProfile
        {
            ShopName = Text(v, K.ShopName, defaults.ShopName),
            LegalName = Text(v, K.LegalName),
            AddressLine1 = line1,
            AddressLine2 = Text(v, K.AddressLine2),
            City = Text(v, K.City),
            Pincode = Text(v, K.Pincode),
            StateCode = Text(v, K.StateCode),
            Phone = Text(v, K.ShopPhone),
            Email = Text(v, K.Email),
            GstRegistrationType = registration,
            Gstin = gstin,
            Pan = Text(v, K.Pan),
            DrugLicense20B = dl20,
            DrugLicense21B = Text(v, K.DrugLicense21B),
            DrugLicenseExpiry = ReadDate(v, K.DrugLicenseExpiry),
            Fssai = Text(v, K.Fssai),
            PharmacistName = Text(v, K.PharmacistName),
            PharmacistRegistrationNo = Text(v, K.PharmacistRegistrationNo),
            BankName = Text(v, K.BankName),
            BankAccountName = Text(v, K.BankAccountName),
            BankAccountNumber = Text(v, K.BankAccountNumber),
            BankIfsc = Text(v, K.BankIfsc),
            BankBranch = Text(v, K.BankBranch),
            UpiId = Text(v, K.UpiId)
        };
    }

    /// <summary>Key/value pairs for a (normalised, validated) profile, including the composed pre-6.2 keys.</summary>
    public static IReadOnlyDictionary<string, string> WriteBusinessProfile(BusinessProfile p) => new Dictionary<string, string>
    {
        [K.ShopName] = p.ShopName,
        [K.ShopAddress] = p.FullAddress,
        [K.ShopPhone] = p.Phone,
        [K.ShopGstin] = p.EffectiveGstin,
        [K.LicenseNumber] = p.DrugLicenseDisplay,
        [K.PharmacistName] = p.PharmacistName,
        [K.LegalName] = p.LegalName,
        [K.AddressLine1] = p.AddressLine1,
        [K.AddressLine2] = p.AddressLine2,
        [K.City] = p.City,
        [K.Pincode] = p.Pincode,
        [K.StateCode] = p.StateCode,
        [K.Email] = p.Email,
        [K.GstRegistrationType] = Number((long)p.GstRegistrationType),
        [K.Gstin] = p.Gstin,
        [K.Pan] = p.Pan,
        [K.DrugLicense20B] = p.DrugLicense20B,
        [K.DrugLicense21B] = p.DrugLicense21B,
        [K.DrugLicenseExpiry] = p.DrugLicenseExpiry?.ToString("yyyy-MM-dd", Inv) ?? string.Empty,
        [K.Fssai] = p.Fssai,
        [K.PharmacistRegistrationNo] = p.PharmacistRegistrationNo,
        [K.BankName] = p.BankName,
        [K.BankAccountName] = p.BankAccountName,
        [K.BankAccountNumber] = p.BankAccountNumber,
        [K.BankIfsc] = p.BankIfsc,
        [K.BankBranch] = p.BankBranch,
        [K.UpiId] = p.UpiId
    };

    // ───────────── sale settings ─────────────

    public static SaleSettings ReadSaleSettings(IReadOnlyDictionary<string, string> v)
    {
        var d = new SaleSettings();
        var doctorId = ReadLong(v, K.DefaultDoctorId);
        return new SaleSettings
        {
            NewSaleIsCredit = ReadBool(v, K.NewSaleIsCredit, d.NewSaleIsCredit),
            DefaultDueDays = ReadInt(v, K.DefaultDueDays, d.DefaultDueDays),
            DefaultDoctorId = doctorId is > 0 ? doctorId : null,
            DefaultDoctorName = Text(v, K.DefaultDoctorName),
            ExpiryCutoffMonths = ReadInt(v, K.ExpiryCutoffMonths, d.ExpiryCutoffMonths),
            NearExpiryMonths = ReadInt(v, K.NearExpiryMonths, d.NearExpiryMonths),
            RoundOffMode = ReadEnum(v, K.RoundOffMode, d.RoundOffMode),
            AllowNegativeStock = ReadBool(v, K.AllowNegativeStock, d.AllowNegativeStock),
            AddSchemeWrtSaleQty = ReadBool(v, K.AddSchemeWrtSaleQty, d.AddSchemeWrtSaleQty),
            BarcodeQtyInPacks = ReadBool(v, K.BarcodeQtyInPacks, d.BarcodeQtyInPacks),
            DefaultDiscountPercent = ReadDecimal(v, K.DefaultDiscountPercent, d.DefaultDiscountPercent),
            MinDiscountPercent = ReadDecimal(v, K.MinDiscountPercent, d.MinDiscountPercent),
            MaxDiscountPercent = ReadDecimal(v, K.MaxDiscountPercent, d.MaxDiscountPercent),
            PatientReminders = ReadBool(v, K.PatientReminders, d.PatientReminders),
            RequireDoctorForScheduleH = ReadBool(v, K.RequireDoctorForScheduleH, d.RequireDoctorForScheduleH),
            RequirePrescriptionReference = ReadBool(v, K.RequirePrescriptionReference, d.RequirePrescriptionReference)
        };
    }

    public static IReadOnlyDictionary<string, string> WriteSaleSettings(SaleSettings s) => new Dictionary<string, string>
    {
        [K.NewSaleIsCredit] = Bool(s.NewSaleIsCredit),
        [K.DefaultDueDays] = Number(s.DefaultDueDays),
        [K.DefaultDoctorId] = s.DefaultDoctorId is { } id ? Number(id) : string.Empty,
        [K.DefaultDoctorName] = s.DefaultDoctorName.Trim(),
        [K.ExpiryCutoffMonths] = Number(s.ExpiryCutoffMonths),
        [K.NearExpiryMonths] = Number(s.NearExpiryMonths),
        [K.RoundOffMode] = Number((long)s.RoundOffMode),
        [K.AllowNegativeStock] = Bool(s.AllowNegativeStock),
        [K.AddSchemeWrtSaleQty] = Bool(s.AddSchemeWrtSaleQty),
        [K.BarcodeQtyInPacks] = Bool(s.BarcodeQtyInPacks),
        [K.DefaultDiscountPercent] = Number(s.DefaultDiscountPercent),
        [K.MinDiscountPercent] = Number(s.MinDiscountPercent),
        [K.MaxDiscountPercent] = Number(s.MaxDiscountPercent),
        [K.PatientReminders] = Bool(s.PatientReminders),
        [K.RequireDoctorForScheduleH] = Bool(s.RequireDoctorForScheduleH),
        [K.RequirePrescriptionReference] = Bool(s.RequirePrescriptionReference)
    };

    // ───────────── invoice print settings ─────────────

    public static InvoicePrintSettings ReadInvoicePrintSettings(IReadOnlyDictionary<string, string> v)
    {
        var d = new InvoicePrintSettings();
        return new InvoicePrintSettings
        {
            Fields = InvoiceField.All.ToDictionary(f => f.Key, f => ReadBool(v, K.InvoiceFieldKey(f.Key), f.DefaultValue)),
            TermsText = Text(v, K.InvoiceTermsText, d.TermsText),
            FooterNote = Text(v, K.InvoiceFooterNote, d.FooterNote),
            ContactNumber = Text(v, K.InvoiceContactNumber)
        };
    }

    public static IReadOnlyDictionary<string, string> WriteInvoicePrintSettings(InvoicePrintSettings s)
    {
        var values = InvoiceField.All.ToDictionary(f => K.InvoiceFieldKey(f.Key), f => Bool(s.IsEnabled(f.Key)));
        values[K.InvoiceTermsText] = s.TermsText.Trim();
        values[K.InvoiceFooterNote] = s.FooterNote.Trim();
        values[K.InvoiceContactNumber] = s.ContactNumber.Trim();
        return values;
    }

    // ───────────── general settings ─────────────

    public static GeneralSettings ReadGeneralSettings(IReadOnlyDictionary<string, string> v)
    {
        var d = new GeneralSettings();
        return new GeneralSettings
        {
            DefaultGstRatePercent = ReadDecimal(v, K.DefaultGstRate, d.DefaultGstRatePercent),
            NearExpiryDays = ReadInt(v, K.NearExpiryDays, d.NearExpiryDays),
            BackupRetentionDays = ReadInt(v, K.BackupRetentionDays, d.BackupRetentionDays),
            AutoBackupEnabled = ReadBool(v, K.AutoBackupEnabled, d.AutoBackupEnabled)
        };
    }

    public static IReadOnlyDictionary<string, string> WriteGeneralSettings(GeneralSettings s) => new Dictionary<string, string>
    {
        [K.DefaultGstRate] = Number(s.DefaultGstRatePercent),
        [K.NearExpiryDays] = Number(s.NearExpiryDays),
        [K.BackupRetentionDays] = Number(s.BackupRetentionDays),
        [K.AutoBackupEnabled] = Bool(s.AutoBackupEnabled)
    };

    // ───────────── paper sizes ─────────────

    public static PaperSize? ReadPaperSize(string? value) =>
        Enum.TryParse<PaperSize>(value, ignoreCase: true, out var size) && Enum.IsDefined(size) ? size : null;

    /// <summary>The print service parses the value by enum name, so it is stored as "A5", "Thermal80mm", …</summary>
    public static string WritePaperSize(PaperSize size) => size.ToString();

    /// <summary>Template names become part of a settings key; only plain identifiers are accepted.</summary>
    public static bool IsValidTemplateName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');
}
