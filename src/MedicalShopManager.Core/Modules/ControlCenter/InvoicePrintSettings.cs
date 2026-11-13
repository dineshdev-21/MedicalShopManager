namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>Section of the invoice a print toggle belongs to (groups the Print Layout screen).</summary>
public enum InvoiceFieldGroup
{
    Header = 1,
    Party = 2,
    Items = 3,
    Totals = 4,
    Footer = 5
}

/// <summary>One invoice print toggle: setting key suffix, label, group and default.</summary>
public sealed record InvoiceFieldDefinition(string Key, string Label, InvoiceFieldGroup Group, bool DefaultValue, string? Hint = null);

/// <summary>
/// Invoice print toggles (Localwell <c>invoice_settings</c>, "≈60 boolean print toggles" plus logo, UPI id
/// <c>business_bank_detail_upi</c>, bank details and <c>invoice_contact_number</c>). The bundle exposes the group,
/// not every key, so this list keeps the toggles that standard Indian pharmacy invoices print and that the sale
/// invoice / thermal receipt layouts in spec 02 show. Stored as "1"/"0" under <c>ControlCenter.Invoice.&lt;Key&gt;</c>.
/// </summary>
public static class InvoiceField
{
    public const string ShowLogo = nameof(ShowLogo);
    public const string ShowGstin = nameof(ShowGstin);
    public const string ShowDrugLicense = nameof(ShowDrugLicense);
    public const string ShowFssai = nameof(ShowFssai);
    public const string ShowEmail = nameof(ShowEmail);
    public const string ShowPan = nameof(ShowPan);

    public const string ShowPatientAddress = nameof(ShowPatientAddress);
    public const string ShowPatientAgeGender = nameof(ShowPatientAgeGender);
    public const string ShowDoctor = nameof(ShowDoctor);
    public const string ShowCustomerGstin = nameof(ShowCustomerGstin);
    public const string ShowPreviousBalance = nameof(ShowPreviousBalance);
    public const string ShowPlaceOfSupply = nameof(ShowPlaceOfSupply);

    public const string ShowManufacturer = nameof(ShowManufacturer);
    public const string ShowComposition = nameof(ShowComposition);
    public const string ShowSchedule = nameof(ShowSchedule);
    public const string ShowHsn = nameof(ShowHsn);
    public const string ShowBatch = nameof(ShowBatch);
    public const string ShowExpiry = nameof(ShowExpiry);
    public const string ShowPacking = nameof(ShowPacking);
    public const string ShowRackLocation = nameof(ShowRackLocation);
    public const string ShowMrp = nameof(ShowMrp);
    public const string ShowDiscount = nameof(ShowDiscount);
    public const string ShowGstRate = nameof(ShowGstRate);
    public const string QtyInPacks = nameof(QtyInPacks);

    public const string ShowGstSummary = nameof(ShowGstSummary);
    public const string ShowAmountInWords = nameof(ShowAmountInWords);
    public const string ShowSavings = nameof(ShowSavings);
    public const string ShowRoundOff = nameof(ShowRoundOff);
    public const string ShowPaymentMode = nameof(ShowPaymentMode);

    public const string ShowUpiQr = nameof(ShowUpiQr);
    public const string ShowBankDetails = nameof(ShowBankDetails);
    public const string ShowSignature = nameof(ShowSignature);
    public const string ShowPharmacist = nameof(ShowPharmacist);
    public const string ShowTerms = nameof(ShowTerms);
    public const string ShowBillingUser = nameof(ShowBillingUser);
    public const string PrintCopyLabels = nameof(PrintCopyLabels);

    public static IReadOnlyList<InvoiceFieldDefinition> All { get; } = new InvoiceFieldDefinition[]
    {
        new(ShowLogo, "Shop logo", InvoiceFieldGroup.Header, true),
        new(ShowGstin, "Shop GSTIN", InvoiceFieldGroup.Header, true),
        new(ShowDrugLicense, "Drug licence numbers (20B / 21B)", InvoiceFieldGroup.Header, true),
        new(ShowFssai, "FSSAI number", InvoiceFieldGroup.Header, false),
        new(ShowEmail, "Shop email", InvoiceFieldGroup.Header, false),
        new(ShowPan, "Shop PAN", InvoiceFieldGroup.Header, false),

        new(ShowPatientAddress, "Patient / customer address", InvoiceFieldGroup.Party, true),
        new(ShowPatientAgeGender, "Patient age and gender", InvoiceFieldGroup.Party, false),
        new(ShowDoctor, "Doctor name and registration", InvoiceFieldGroup.Party, true),
        new(ShowCustomerGstin, "Customer GSTIN (B2B)", InvoiceFieldGroup.Party, true),
        new(ShowPreviousBalance, "Customer previous balance", InvoiceFieldGroup.Party, false),
        new(ShowPlaceOfSupply, "Place of supply (state)", InvoiceFieldGroup.Party, false),

        new(ShowManufacturer, "Manufacturer / company", InvoiceFieldGroup.Items, true),
        new(ShowComposition, "Composition (salt)", InvoiceFieldGroup.Items, false),
        new(ShowSchedule, "Drug schedule (H / H1 / X)", InvoiceFieldGroup.Items, true),
        new(ShowHsn, "HSN code", InvoiceFieldGroup.Items, true),
        new(ShowBatch, "Batch number", InvoiceFieldGroup.Items, true),
        new(ShowExpiry, "Expiry (MM/YY)", InvoiceFieldGroup.Items, true),
        new(ShowPacking, "Packing (1 x 15)", InvoiceFieldGroup.Items, true),
        new(ShowRackLocation, "Rack location", InvoiceFieldGroup.Items, false),
        new(ShowMrp, "MRP", InvoiceFieldGroup.Items, true),
        new(ShowDiscount, "Discount %", InvoiceFieldGroup.Items, true),
        new(ShowGstRate, "GST %", InvoiceFieldGroup.Items, true),
        new(QtyInPacks, "Quantity as packs + loose (3 + 5)", InvoiceFieldGroup.Items, false, "Localwell invoice_ret_is_qty_in_packs"),

        new(ShowGstSummary, "GST rate-wise tax summary", InvoiceFieldGroup.Totals, true),
        new(ShowAmountInWords, "Amount in words", InvoiceFieldGroup.Totals, true),
        new(ShowSavings, "Customer savings vs MRP", InvoiceFieldGroup.Totals, true),
        new(ShowRoundOff, "Round-off line", InvoiceFieldGroup.Totals, true),
        new(ShowPaymentMode, "Payment mode and amount paid", InvoiceFieldGroup.Totals, true),

        new(ShowUpiQr, "UPI payment QR", InvoiceFieldGroup.Footer, false, "Needs a UPI ID in the business profile"),
        new(ShowBankDetails, "Bank account details", InvoiceFieldGroup.Footer, false, "Needs bank details in the business profile"),
        new(ShowSignature, "Authorised signature image", InvoiceFieldGroup.Footer, true),
        new(ShowPharmacist, "Pharmacist name and registration", InvoiceFieldGroup.Footer, true),
        new(ShowTerms, "Terms and conditions", InvoiceFieldGroup.Footer, true),
        new(ShowBillingUser, "Billed by (user)", InvoiceFieldGroup.Footer, false),
        new(PrintCopyLabels, "ORIGINAL / DUPLICATE copy label", InvoiceFieldGroup.Footer, false)
    };

    public static InvoiceFieldDefinition? Find(string key) => All.FirstOrDefault(f => f.Key == key);
}

/// <summary>
/// The invoice print toggles plus the free-text parts of the footer. Unknown keys are ignored and missing keys fall
/// back to <see cref="InvoiceFieldDefinition.DefaultValue"/>, so adding a toggle never breaks an old database.
/// </summary>
public sealed record InvoicePrintSettings
{
    public const int MaxTermsLength = 500;
    public const int MaxFooterLength = 200;

    public const string DefaultTerms =
        "Goods once sold will not be taken back or exchanged without the bill. Medicines are not returnable after expiry. Subject to local jurisdiction.";

    public IReadOnlyDictionary<string, bool> Fields { get; init; } =
        InvoiceField.All.ToDictionary(f => f.Key, f => f.DefaultValue);

    public string TermsText { get; init; } = DefaultTerms;
    public string FooterNote { get; init; } = "Get well soon.";

    /// <summary>Phone printed on invoices instead of the shop phone (Localwell <c>invoice_contact_number</c>); blank = shop phone.</summary>
    public string ContactNumber { get; init; } = string.Empty;

    public bool IsEnabled(string key) =>
        Fields.TryGetValue(key, out var value) ? value : InvoiceField.Find(key)?.DefaultValue ?? false;

    /// <summary>Returns a copy with one toggle changed (unknown keys are rejected).</summary>
    public InvoicePrintSettings With(string key, bool enabled)
    {
        if (InvoiceField.Find(key) == null) throw new ArgumentException($"Unknown invoice field '{key}'.", nameof(key));
        var copy = new Dictionary<string, bool>(Fields) { [key] = enabled };
        return this with { Fields = copy };
    }

    public bool ShowLogo => IsEnabled(InvoiceField.ShowLogo);
    public bool ShowGstin => IsEnabled(InvoiceField.ShowGstin);
    public bool ShowDrugLicense => IsEnabled(InvoiceField.ShowDrugLicense);
    public bool ShowHsn => IsEnabled(InvoiceField.ShowHsn);
    public bool ShowBatch => IsEnabled(InvoiceField.ShowBatch);
    public bool ShowExpiry => IsEnabled(InvoiceField.ShowExpiry);
    public bool ShowMrp => IsEnabled(InvoiceField.ShowMrp);
    public bool QtyInPacks => IsEnabled(InvoiceField.QtyInPacks);
    public bool ShowUpiQr => IsEnabled(InvoiceField.ShowUpiQr);
    public bool ShowBankDetails => IsEnabled(InvoiceField.ShowBankDetails);
    public bool ShowSignature => IsEnabled(InvoiceField.ShowSignature);

    public static IReadOnlyList<SettingsError> Validate(InvoicePrintSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var errors = new List<SettingsError>();
        if (s.TermsText.Length > MaxTermsLength)
            errors.Add(new(nameof(TermsText), $"Terms and conditions must be at most {MaxTermsLength} characters."));
        if (s.FooterNote.Length > MaxFooterLength)
            errors.Add(new(nameof(FooterNote), $"Footer note must be at most {MaxFooterLength} characters."));
        if (s.ContactNumber.Trim().Length > 0 && !IndianIdentifiers.IsValidPhone(s.ContactNumber))
            errors.Add(new(nameof(ContactNumber), "Invoice contact number must be a 10-digit mobile or a landline with STD code."));
        foreach (var key in s.Fields.Keys.Where(k => InvoiceField.Find(k) == null))
            errors.Add(new(key, $"Unknown invoice field '{key}'."));
        return errors;
    }
}
