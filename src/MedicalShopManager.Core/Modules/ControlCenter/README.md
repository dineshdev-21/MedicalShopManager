# Control Center: settings other modules read

Resolve these Core interfaces from DI (registered in `App/Modules/ControlCenterModule.cs`). Do not read the keys
directly: the typed service applies defaults, legacy fall-backs and validation.

| Need | Call |
|---|---|
| Shop name, address, state code (IGST vs CGST + SGST), GSTIN, registration type (Tax Invoice vs Bill of Supply), DL 20B/21B, FSSAI, pharmacist, bank, UPI | `IControlCenterSettingsService.GetBusinessProfileAsync()` → `BusinessProfile` (`StateCode`, `EffectiveGstin`, `SaleDocumentTitle`, `GstRegistrationType.ChargesGst()`) |
| Sale defaults and rules (credit default, due days, default doctor, expiry cut-off, near-expiry window, round-off, negative stock, scheme on sale qty, barcode in packs, discount bounds, Rx rules) | `GetSaleSettingsAsync()` → `SaleSettings`, applied with `SaleSettingsRules` (`IsBatchSaleable`, `IsNearExpiry`, `DueDate`, `ApplyBillDiscount`, `RequiresDoctor`, `CanSellQuantity`) |
| Invoice print toggles, terms, footer, invoice contact number | `GetInvoicePrintSettingsAsync()` → `InvoicePrintSettings.IsEnabled(InvoiceField.ShowHsn)` … |
| Logo / signature bytes for print | `GetBrandingImageAsync(BrandingImageKind.Logo)` |
| UPI QR payload for a bill | `UpiPaymentLink.Build(profile.UpiId, profile.ShopName, netPayablePaisa, invoiceNumber)` |
| Default paper size of a template | `IPrintService.GetPaperSizeAsync(template)` (key `Print.PaperSize.<Template.Name>`, written by Print Layout) |
| Default GST rate, near-expiry days, backup retention | `GetGeneralSettingsAsync()` |
| May the user do X? | `IUserPermissionService.IsCurrentUserPermittedAsync("sale_delete")`, keys in `PermissionCatalog` |
| React to a change | `IControlCenterSettingsService.SettingsChanged` (area enum) |

## Keys (table `AppSettings`, seeded by migration 19)

All keys are constants in `ControlCenterSettingKeys`. Booleans are `1`/`0`, numbers invariant culture, dates
`yyyy-MM-dd`, enums their integer value (paper sizes by name).

- Pre-6.2 keys still written for the shared print header: `ShopName`, `ShopAddress` (composed), `ShopPhone`,
  `ShopGstin` (blank when unregistered), `LicenseNumber` (composed `20B: …, 21B: …`), `PharmacistName`,
  `DefaultGstRate`, `NearExpiryDays`, `BackupRetentionDays`, `AutoBackupEnabled`.
- `ControlCenter.Business.*`: LegalName, AddressLine1, AddressLine2, City, Pincode, StateCode, Email,
  GstRegistrationType (1 regular, 2 composition, 3 unregistered), Gstin, Pan, DrugLicense20B, DrugLicense21B,
  DrugLicenseExpiry, Fssai, PharmacistRegistrationNo, BankName, BankAccountName, BankAccountNumber, BankIfsc,
  BankBranch, UpiId.
- `ControlCenter.Sale.*`: NewSaleIsCredit, DefaultDueDays, DefaultDoctorId, DefaultDoctorName, ExpiryCutoffMonths,
  NearExpiryMonths (3/6/9/12), RoundOffMode (0 none, 1 rupee, 2 fifty paise), AllowNegativeStock (default 0),
  AddSchemeWrtSaleQty, BarcodeQtyInPacks, DefaultDiscountPercent, MinDiscountPercent, MaxDiscountPercent,
  PatientReminders, RequireDoctorForScheduleH, RequirePrescriptionReference.
- `ControlCenter.Invoice.<Field>` for every `InvoiceField` toggle, plus `TermsText`, `FooterNote`, `ContactNumber`.
- `Print.PaperSize.<TemplateName>`: `A4`, `A5`, `Thermal80mm`, `Thermal58mm`.

Tables: `ControlCenterUserPermissions(UserId, PermissionKey, IsPermitted)` (only differences from the role default)
and `ControlCenterAssets(AssetKey, ContentType, Data)` (logo and signature, inside the database so backups include them).
