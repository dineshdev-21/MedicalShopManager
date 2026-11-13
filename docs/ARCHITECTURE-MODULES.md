# Module Architecture Contract

The 6.2 feature modules (Billing, Sales Returns, Purchases, Stock, Audit & Labels, Orderbook, Finance,
Doctors & Follow-ups, Reports, Control Center, Dashboard & Search) are built **in parallel by separate agents**.
This file is the contract that keeps them from editing the same files. Read it fully before you write code.

Reference specs: `MASTER-SPEC.md`, `00-bundle-technical-inventory.md`, `01`–`12-*.md` in the session scratchpad
`spec/` folder. Lessons: `tasks/lessons.md` (money in paisa, "verified" means built and tested).

---

## 1. Golden rules

1. **Stay in your own files.** Everything you add goes in your module's folders (§2) and your module's
   registration file `src/MedicalShopManager.App/Modules/<Name>Module.cs`.
2. **Never edit a frozen file** (§3). If you think one needs a change, stop and report it; do not work around it
   with a copy.
3. **Money is `long` paisa.** Rates are per **pack**, quantities are **units**. Every money calculation goes
   through `PricingCalculator` (§7).
4. **Schema changes are migrations** with your reserved number (§6). Never edit a released migration.
5. **Numbers come from `DocumentSequences`** (§8), allocated inside the posting transaction.
6. **Build to 0 errors and run the tests** before you report done (§11).

---

## 2. Solution layout and module folders

```
src/MedicalShopManager.Core/            models, enums, interfaces, pure logic (net8.0, no I/O)
    Modules/<Name>/                       ← your models, enums, service/repository interfaces
src/MedicalShopManager.Infrastructure/  SQLite (raw ADO.NET), repositories, services (net8.0)
    Modules/<Name>/                       ← your repositories, services, migrations
src/MedicalShopManager.App/             WPF + MVVM (CommunityToolkit.Mvvm), net8.0-windows
    Modules/<Name>Module.cs               ← your DI + navigation registration (one file per module)
    Modules/<Name>/ViewModels/            ← your page ViewModels
    Modules/<Name>/Views/                 ← your pages (FooPage.xaml for FooViewModel)
    Modules/<Name>/Printing/              ← your IDocumentTemplate<T> implementations
tests/MedicalShopManager.UnitTests/        your unit tests: <Name>*Tests.cs
tests/MedicalShopManager.IntegrationTests/ your SQLite tests: <Name>*Tests.cs
```

Namespaces follow folders: `MedicalShopManager.Core.Modules.Billing`,
`MedicalShopManager.Infrastructure.Modules.Billing`, `MedicalShopManager.App.Modules.Billing.ViewModels`,
`MedicalShopManager.App.Modules.Billing.Views`.

Code written before 6.2 (`App/ViewModels`, `App/Views`, `Infrastructure/Services`, `Infrastructure/Repositories`,
`Core/Models`, …) stays where it is. A module **owns** the existing files listed for it below and may edit them;
new code goes in the module folders.

| Module (folder) | Scope (todo 6.2) | Migration no. | Overflow | Owns existing pages / files | Nav group(s) |
|---|---|---|---|---|---|
| Billing | POS, customers, hold bills, H1 capture, sale invoice print | 10 | 100–109 | NewBill (`BillingViewModel/Page`), Customers (`CustomersViewModel/Page`), `BillingService`, `SaleRepository`, `Modules/Billing/Printing/SaleInvoiceTemplate.cs` | Sales |
| SalesReturns | sales register, sale returns, credit notes | 11 | 110–119 | SalesHistory (`SalesHistoryViewModel/Page`) | Invoices, Sales |
| Purchases | purchase entry, distributors, purchase/expiry returns, debit notes, CSV import | 12 | 120–129 | Purchases, Suppliers (`PurchasesViewModel/Page`, `SuppliersViewModel/Page`), `PurchaseService`, `PurchaseRepository`, `SupplierRepository` | Purchases, Invoices |
| Stock | item detail tabs, adjustments, opening stock | 13 | 130–139 | Medicines, BatchExpiry (`MedicinesViewModel/Page`, `BatchExpiryViewModel/Page`), `InventoryService` | Stock |
| AuditLabels | stock audit, barcode labels | 14 | 140–149 | — | Stock |
| Orderbook | shortbook, purchase orders | 15 | 150–159 | — | Orderbook |
| Finance | expenses, cash & bank, cheques, receipts/payments, party ledgers | 16 | 160–169 | Expenses (`ExpensesViewModel/Page`), `PaymentRepository`, `ExpenseRepository` | Finance |
| DoctorsFollowups | doctors master, patient follow-ups / refill reminders | 17 | 170–179 | — | Sales (doctors), Dashboard (follow-ups) |
| Reports | reports catalogue, exports | 18 | 180–189 | Reports (`ReportsViewModel/Page`), `ReportService` | Reports |
| ControlCenter | business profile, prefix & sequences, print & sale settings, users | 19 | 190–199 | Settings, UserManagement (`SettingsViewModel/Page`, `UserManagementViewModel/Page`) | Control Center |
| DashboardSearch | dashboard widgets, global search (Ctrl+F), shortcuts panel (F9) | 20 | 200–209 | Dashboard (`DashboardViewModel/Page`) | Dashboard |
| Core (shared kernel) | database, shared services, System pages | 1–9 | — | Backup, ExcelManagement, everything not listed above | System |

**One type per file, so ownership is per file.** Every pre-6.2 ViewModel, repository and Core interface lives in
a file named after the class (`App/ViewModels/CustomersViewModel.cs`, `Infrastructure/Repositories/PaymentRepository.cs`,
`Core/Interfaces/IPaymentRepository.cs`, report records in `Core/Interfaces/ReportModels.cs`, owned by Reports). "Owns
`X`" in the table means the file `X.cs` (and `X`'s interface file `IX.cs`); no file is shared between two modules. To
restructure an owned ViewModel, you may move its file into `App/Modules/<Name>/ViewModels/` (same class name; the
locator finds the view either way).

---

## 3. Frozen files (do not edit)

| File | Extend it by |
|---|---|
| `Infrastructure/Database/DatabaseManager.cs` | a new `IMigration` class (§6) |
| `Infrastructure/Database/Migrations/*` (incl. `IMigration`, `MigrationSql`, `MigrationCatalog`, `Migration0001-0003`) | your own migration in `Infrastructure/Modules/<Name>/` |
| `App/App.xaml.cs`, `App/App.xaml` | your `App/Modules/<Name>Module.cs` |
| `App/MainWindow.xaml(.cs)`, `App/ViewModels/MainViewModel.cs` | `services.AddPage<…>()` in your module file (§5) |
| `App/Navigation/*` (`NavItem`, `NavGroup`, `INavigationRegistry`, `ViewLocator`, `AddPage`) | — |
| `App/Services/NavigationService.cs` | `INavigationAware` on your ViewModel |
| `App/Modules/CoreModule.cs` | `services.Replace(...)` in your module file (§4) |
| `App/Printing/*` (`IPrintService`, `IDocumentTemplate<T>`, `PaperLayout`, `DocumentBuilder`) | your own template class (§9) |
| `Core/Pricing/*` (`PricingCalculator`, `PackQuantity`, records) | report a missing formula; do not fork the maths |
| `Core/Numbering/*`, `Infrastructure/Numbering/*`, `Core/Models/DocumentSequence.cs`, `DocumentType` enum | — |
| `Core/Models/Medicine.cs`, `Core/Models/MedicineBatch.cs`, `MedicineRepository`, `BatchRepository` | a module-owned side table keyed by `MedicineId` / `BatchId` (§6) |
| `Core/Enums/FoundationEnums.cs`, `DrugSchedule` + `DrugScheduleExtensions` | your own enums in `Core/Modules/<Name>/` |
| `Core/Interfaces/IRepository.cs` | — |

Shared files that are *not* frozen but are shared (`Core/Enums/Enums.cs`, `Theme.xaml`, `ValueConverters.cs`):
**do not edit them either.** Put new interfaces, enums, styles and converters in your module folders (a module
ResourceDictionary can be merged in your page's `UserControl.Resources`). Core interface files
(`Core/Interfaces/I<Name>.cs`) belong to the module that owns the implementation (§2 table): that module may add
members (additive only; existing members keep their signatures because other modules call them). Interfaces of
Core-owned services (`IAuthService`, `IAuditService`, `IAppSettingsRepository`, …) are not edited by modules.

---

## 4. Registration (`App/Modules/<Name>Module.cs`)

Every module exposes one extension method, already called from `App.xaml.cs` in this order:
Core → DashboardSearch → Billing → SalesReturns → Purchases → Stock → AuditLabels → Orderbook → Finance →
DoctorsFollowups → Reports → ControlCenter.

```csharp
public static IServiceCollection AddBillingModule(this IServiceCollection services, INavigationRegistry navigation)
{
    services.AddPage<BillingViewModel>(navigation, "NewBill", "New Bill / POS", NavGroup.Sales, "", 10, keyGesture: "Ctrl+Alt+S");
    services.AddSingleton<IHeldBillRepository, HeldBillRepository>();   // your repositories / services
    services.AddSingleton<IDocumentTemplate<Sale>, SaleInvoiceTemplate>(); // your print templates
    return services;
}
```

- **Lifetimes:** repositories and services `AddSingleton` (stateless, they open a connection per call), page
  ViewModels transient (via `AddPage`), dialogs/child ViewModels `AddTransient`.
- **Replacing a shared service** registered by `CoreModule` (e.g. a rewritten `IBillingService`):
  `services.Replace(ServiceDescriptor.Singleton<IBillingService, NewBillingService>());`
  (`using Microsoft.Extensions.DependencyInjection.Extensions;`). Only replace services your module owns (§2).
- Never register an interface another module owns. A second `Add…` for the same interface silently wins.
- Cross-module use goes through **Core interfaces** resolved from DI, never through another module's concrete
  class or ViewModel.

---

## 5. Navigation, pages and views

- A page is a `NavItem(Key, Title, Group, IconGlyph, Order, ViewModelType, KeyGesture?, ShowInSidebar)`.
  Register with `services.AddPage<TViewModel>(navigation, key, title, group, glyph, order, keyGesture, showInSidebar)`.
  The registry throws at startup on a duplicate key or duplicate shortcut.
- **Keys** are stable PascalCase strings, unique across the app. Keys in use: `Dashboard` (home page),
  `NewBill`, `Customers`, `SalesHistory`, `Purchases`, `Suppliers`, `Medicines`, `BatchExpiry`, `Expenses`,
  `Reports`, `Settings`, `UserManagement`, `Backup`, `ExcelManagement`; added by batch A: `SaleDrafts`,
  `AddCustomer` (Billing); `PurchaseEntry`, `PurchaseImport`, `PurchaseReturn`, `PurchaseExpiryReturn`,
  `PurchaseSupplierStatement`, `PurchaseDebitNotes` (Purchases); `Stock`, `StockAdjustments`, `StockImport`,
  `StockBulkUpdate` (Stock); `ControlCenterHome`, `BusinessProfile`, `SaleSettings`, `PrefixSequences`,
  `PrintLayout` (Control Center). Prefix new keys with your area if a name could clash (`StockAudit`,
  `OrderbookCart`, …).
- **Groups** (sidebar order = enum order): Dashboard, Sales, Purchases, Stock, Invoices, Orderbook, Reports,
  Finance, ControlCenter, System. Use `Order` steps of 10 so neighbours can slot in.
- **Icons:** Segoe MDL2 Assets glyphs, e.g. `""`.
- **Entry / detail screens** that are not sidebar items: `showInSidebar: false`, open them with
  `INavigationService.NavigateTo("SaleReturnEntry", parameter)`. Implement `INavigationAware.OnNavigatedTo(object?)`
  to receive the parameter (called before `LoadAsync`).
- **Views are found by name** (`ViewLocator`): `FooViewModel` → `FooPage` (preferred) or `FooView`, a
  `UserControl` in any namespace ending `.Views` (`Window` classes are never picked). View class names must be
  unique app-wide, so prefix module-specific names (`StockAuditPage`, not `AuditPage`). Failure modes, none of
  which crash the shell: an ambiguous name stops startup with a message naming both classes; a page with no view
  shows an error text in its place (and is logged at startup); a page whose ViewModel cannot be created (missing
  DI registration) or whose `LoadAsync` throws is replaced by `PageErrorViewModel`, which shows the error. No DataTemplate registration
  anywhere. To host a child ViewModel inside your page use
  `<ContentControl Content="{Binding Child}" ContentTemplateSelector="{StaticResource ViewLocator}"/>`.
- Page ViewModels derive from `App.ViewModels.BaseViewModel` (`LoadAsync`, `SetStatus`, `SetError`, `IsLoading`).
- **Window shortcuts** from the spec (MASTER-SPEC §5.1) and who owns them:

  | Keys | Action | Owner |
  |---|---|---|
  | Ctrl+Alt+S | New sale (`NewBill`, registered) | Billing |
  | Ctrl+Alt+P | Purchases (registered) | Purchases |
  | Ctrl+Alt+D | Suppliers / distributors (registered) | Purchases |
  | Alt+I | Items (registered) | Stock |
  | Ctrl+Alt+T | New sale return | SalesReturns |
  | Ctrl+Alt+R | New purchase return | Purchases |
  | Ctrl+Alt+C | Add customer | Billing |
  | F8 | Drafts / held bills | Billing |
  | Ctrl+F, F9 | Global search, shortcuts panel | DashboardSearch |

  Page-level keys (Alt+S save, F2, F4, Esc …) are `InputBindings` on your page's root, not window-level.
  Do not register a window-level gesture another module owns.

---

## 6. Migrations

- One class per schema change implementing `IMigration { int Version; string Description; Task ApplyAsync(SqliteConnection, SqliteTransaction); }`
  with a public parameterless constructor, in `Infrastructure/Modules/<Name>/`, named `Migration<Version:0000><What>`
  (e.g. `Migration0010BillingHeldBills`). `DatabaseManager` discovers it by reflection; nothing else to register.
- **Reserved numbers:**

  | Version | Owner |
  |---|---|
  | 1 | initial schema (`Migration0001InitialSchema`) |
  | 2 | item master v2 (`Migration0002ItemMasterV2`) |
  | 3 | foundation tables: `DocumentSequences`, `DocumentSequenceCounters` (`Migration0003FoundationTables`) |
  | 4–9 | reserved for foundation fixes (shared kernel only) |
  | 10 billing · 11 sales-returns · 12 purchases · 13 stock · 14 audit-labels · 15 orderbook · 16 finance · 17 doctors-followups · 18 reports · 19 control-center · 20 dashboard-search | the module |
  | 100 + (V − 10) × 10 … +9 | the module with number V, if it needs more than one (billing 100–109 … dashboard-search 200–209) |

  `FoundationRulesTests.Migrations_UseOnlyTheirReservedNumbers` fails the build's tests if a migration in
  `Infrastructure.Modules.<Name>` uses another module's number, and `MigrationCatalog.Discover` throws at startup
  on duplicate versions.
- **Pending = not recorded in `SchemaMigrations`**, applied in ascending version order, each in its own
  transaction that also records it. A failing migration rolls back and stops startup.
- Use `MigrationSql.ExecuteAsync/ScalarAsync/AddColumnIfMissingAsync/RenameColumnIfExistsAsync` (every command
  bound to the migration's transaction). Never commit, roll back, or change PRAGMAs inside a migration.
- **Settings:** a module that needs its own setting seeds it in its migration with
  `INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES ('<Module>.<Key>', …)` (e.g.
  `Billing.AllowNegativeStock`) and reads/writes it through `IAppSettingsRepository`. Never update or delete a key
  you did not create; Control Center owns the pre-6.2 keys and `Print.PaperSize.*`.
- Your migration may depend only on foundation versions 1–3 and your own earlier versions. Modules apply in
  numeric order, but a module merged later can have a lower number than one already applied.
- **Table ownership:** create your own tables (prefix them if the name could clash). You may `ALTER TABLE … ADD
  COLUMN` only on tables your module owns (§2): Billing owns `Sales`/`SaleItems`/`Customers`, Purchases owns
  `Purchases`/`PurchaseItems`/`Suppliers`, Finance owns `Payments`/`Expenses`, SalesReturns and Purchases share
  `Returns` (each adds only its own columns). **`Medicines` and `MedicineBatches` are frozen:** put extra item
  data in a side table such as `StockItemSettings(MedicineId PRIMARY KEY REFERENCES Medicines(Id), …)`.
- Store money as `INTEGER` paisa, dates as `TEXT 'yyyy-MM-dd'` (timestamps ISO-8601), enums as `INTEGER`,
  percentages as `REAL` read into `decimal`. Add indexes for every foreign key and date you filter on.
- Never edit a migration that has been released. Write a new one.
- Test-only `IMigration` classes (declared in test projects) use versions **900–999**, outside every reserved range,
  and tests compare against `MigrationCatalog.Discover()` instead of a hard-coded version list, so they keep
  passing as other modules add migrations.
- Upgrade guarantee: `FoundationUpgradeAndRestoreTests` builds a database from
  `tests/…/Fixtures/v1-original-database.sql` (a dump of a file written by the original v1 code) and upgrades it.
  If your migration transforms existing rows, add the rows you need to a test on top of that fixture rather than
  editing it. The test checks AppSettings by key set, not row count: every v1 key except `InvoicePrefix` /
  `InvoiceNextNumber` must survive, and every added key must be namespaced (`<Module>.<Key>`).
- Fresh-install guarantee: `FoundationFreshDatabaseTests` initialises an empty file with every discovered migration
  and asserts the tables/views of the shared kernel and merged modules exist and compile. A module that adds
  tables appends them to its `ExpectedTables` list.

---

## 7. Money, quantities and `PricingCalculator`

`Core/Pricing/PricingCalculator` is the only place that does money arithmetic. It is pure and fully tested
(`PricingCalculatorTests`, including the Localwell golden vector 1 pack + 5 loose of 15's @ ₹188.40 = ₹251.20, and
₹226.08 at 10% off).

**Units and storage**
- Money: `long` paisa everywhere in code and SQLite. Convert to `decimal` rupees only for display (`/ 100m`).
- Rates (MRP, PTR, purchase rate, sale rate): **per pack** of `UnitsPerPack` units. Quantities: **units**.
  `PackQuantity(Packs, Loose).ToUnits(unitsPerPack)` for entry; `PackQuantity.Format(units, unitsPerPack)` →
  `"3 + 5"` for display.
- A batch carries its own `UnitsPerPack` snapshot (the basis of its rates), so changing the item's pack size never
  re-prices existing stock. Use the batch's value when pricing a batch.
- Percentages are `decimal` percent (12 = 12%).

**Conventions (all stated in the class header)**
- Round once, to whole paisa, `MidpointRounding.AwayFromZero` (`RoundPaisa`). Amount = round(rate × units / pack),
  rounded on the line total, never on a unit price.
- **Sale side is GST-inclusive** (MRP basis; standard Indian retail). taxable = round(total × 100 / (100 + GST)),
  tax = total − taxable, so taxable + tax always equals what the customer pays. (Deliberate deviation from the
  spec's `round2(taxable × gst)`, which can drift a paisa from the bill total.)
- **Purchase side is GST-exclusive** (spec 04): tax = round(taxable × GST / 100), total = taxable + tax.
- CGST = round(tax / 2), SGST = tax − CGST (odd paisa to CGST); inter-state → IGST. `IsInterStateSupply(businessStateCode, partyGstin)`:
  only a party GSTIN with a different state code makes it inter-state; walk-in sales are intra-state.
- **Line discount** applies to the per-pack rate (`RateAfterDiscount`); Localwell computes it relative to MRP,
  so pass MRP as the rate and derive the % with `DiscountPercentFromRate(mrp, rate)` when the user edits the rate.
- **Bill discount:** as a % → `CombineBillDiscount(line%, bill%, min, max)` adds it to each line's own % and clamps
  (spec 02, Localwell behaviour). As a value → `DistributeAmount(amount, lineTotals)` (largest remainder, exact)
  and pass each share as `SaleLineInput.BillDiscountSharePaisa`.
- **Round-off:** `SummarizeBill(lines, RoundOffMode.NearestRupee, otherCharges, adjustment)` gives
  `NetPayablePaisa` and `RoundOffPaisa` (store both). `RoundOff(amount, mode)` for anything else.
- **Margin basis:** selling price **excluding GST**: `MarginPercent(saleRateInclGst, gst, costExGst)` =
  (saleEx − cost) / saleEx × 100 (spec 02 MARGIN %, verified on screen). Cost = the batch's net purchase rate.
- **Net purchase rate:** `CalculatePurchaseLine(...)` → `NetPurchaseRatePerPackPaisa` = taxable ÷ (paid + free)
  per pack, ex-GST (spec 04). Store it on the batch/purchase line for stock value and profit.
- Item rules: never sell above MRP (`SavingsPaisa < 0` means you are), clamp discounts to
  `Medicine.MaxDiscountPercent` with `ClampDiscount`.

Example (sale line):
```csharp
var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
{
    RatePerPackPaisa = batch.SaleRatePaisa, MrpPerPackPaisa = batch.MrpPaisa, UnitsPerPack = batch.UnitsPerPack,
    Units = new PackQuantity(packs, loose).ToUnits(batch.UnitsPerPack),
    DiscountPercent = PricingCalculator.ClampDiscount(requested, 0, medicine.MaxDiscountPercent),
    GstPercent = batch.GstRatePercent, IsInterState = interState, CostPerPackExGstPaisa = netPurchaseRate
});
var totals = PricingCalculator.SummarizeBill(lines, RoundOffMode.NearestRupee);
```

If a formula you need is missing, report it rather than writing arithmetic in your module.

---

## 8. Document numbering

- `DocumentType` (Core/Enums): Sale, SaleReturn, CreditNote, Purchase, PurchaseReturn, DebitNote, PurchaseOrder,
  Receipt (payment in), Payment (payment out), Expense, StockAdjustment, StockAudit. Every type has a seeded row.
  Values are persisted; append new ones only via a foundation change.
- Format = template with tokens `{PREFIX}`, `{SEQ}` (zero-padded), `{FY}` (2026-27), `{FY2}` (2627), `{YYYY}`.
  New types default to `{PREFIX}/{FY2}/{SEQ}`, padding 5, reset every financial year (e.g. `SR/2627/00001`).
  Sale keeps the pre-6.2 series `INV-2026-00042`, migrated from the old `InvoicePrefix`/`InvoiceNextNumber`
  settings (now deleted), with no FY reset.
- Counters are per **(DocumentType, Period)**: Period = FY start year when the sequence resets each April, else 0.
  A back-dated document takes the next number of its own financial year, so numbers never repeat.
- **Allocate inside your posting transaction:**
  ```csharp
  using var tx = conn.BeginTransaction();
  doc.Number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.SaleReturn, doc.Date);
  // insert the document + lines + stock movements on the same tx …
  tx.Commit();   // rollback = the number is not consumed
  ```
  `SaleRepository.InsertWithItemsAsync` is the reference implementation.
- `IDocumentNumberService.PeekNextAsync` shows the would-be number on an unsaved screen (never store it).
  `AllocateNextAsync` only when the document insert is not in a transaction you control.
- Control Center edits prefix/template/padding/reset/next number through `UpdateSequenceAsync`, which validates
  (`{SEQ}` required; `{FY}`/`{FY2}` required when resetting; ≤ 16 characters, the GST invoice-number limit).
- Keep a `UNIQUE` constraint on your document-number column.
- Control Center: switching `ResetEachFinancialYear` or lowering the next number can re-issue a number that already
  exists (different counter period, same formatted text). The `UNIQUE` constraint then rejects the posting, so the
  sequence editor must show the last issued number and refuse a next number at or below it.

---

## 9. Printing

- `IPrintService` (App/Printing): `PrintAsync(template, model, options?)`, `PreviewAsync(...)`,
  `GetPaperSizeAsync(template)`. Paper sizes: `A4`, `A5`, `Thermal80mm`, `Thermal58mm` (`PaperSize` in Core).
- Each module writes its layouts as `IDocumentTemplate<TModel>` classes in `App/Modules/<Name>/Printing/` and
  registers them: `services.AddSingleton<IDocumentTemplate<CreditNote>, CreditNoteTemplate>();`. A template only
  builds a `FlowDocument` (use `DocumentBuilder.Text/Table/AddRow/Rule/Money`); page size, margins, single column
  and the printer dialog are applied by the service. Branch on `context.IsThermal` for narrow layouts.
- `PrintContext` provides `PaperSize`, `Shop` (name, address, phone, GSTIN, DL no., pharmacist from AppSettings)
  and an optional copy label (ORIGINAL / DUPLICATE).
- Default paper size: AppSettings key `Print.PaperSize.<Template.Name>` (written by Control Center), else the
  template's `DefaultPaperSize`. The sale invoice (`SaleInvoiceTemplate`, Name `SaleInvoice`, default A5) is the
  reference template; Sales History print and preview use it.
- Printing goes through the Windows print driver (thermal printers need their Windows driver). Raw ESC/POS, PDF
  export and barcode rendering are module concerns, built on top of the same templates.

---

## 10. Parties (customers, suppliers, doctors) in ledgers

Decision: ledgers, payments, allocations and follow-ups reference a party as **`(PartyType PartyType, long? PartyId)`**
(`PartyType`: Customer = 1, Supplier = 2, Doctor = 3, Other = 4 with `PartyId` NULL for expense vendors or cash
adjustments). Only the enum exists today; Finance builds the ledger tables.

- Why: one ledger table can serve every party kind without a nullable FK column per party table, and statements,
  dues and the global search can union parties uniformly. Customers, suppliers and doctors stay separate master
  tables (their fields differ), matching Localwell's `party_type` + `party_id` on payments.
- Rules: store both columns as `INTEGER`, index `(PartyType, PartyId)`; there is no SQL foreign key, so the owning
  service checks the party exists before posting. Never delete a party row that ledger rows point at (use
  `IsActive = 0`). Sign: positive balance = the shop will receive, negative = the shop will pay (MASTER-SPEC §4.10).
  Derive balances from source rows in SQL (lesson 7). Do not keep running totals.

---

## 11. Item master v2 reference (migration 2)

`Medicine`: Name, **Composition** (salt text, formerly GenericName) + **CompositionId**, **Manufacturer**, Brand,
Category, DosageForm, Strength, **PackLabel** ("15's"), **UnitsPerPack**, **AllowLooseSale**, Barcode, Sku,
HsnCode, GstRatePercent, **Schedule** (`DrugSchedule`), **MinStock** (units, was ReorderLevel), MaxStock (units),
**MaxDiscountPercent** (null = no cap), Unit, RackLocation, **DefaultMrpPaisa** and DefaultSellingPricePaisa
(per pack), DefaultSupplierId, Status (`IsActive` is derived from it), Notes.

`MedicineBatch`: BatchNumber, ExpiryDate (month/year entry → last day of month, `ExpiryFromMonthYear`; a batch is
expired from its expiry date onwards, as before), QuantityReceived / QuantityRemaining / FreeUnits (units),
**UnitsPerPack** (rate basis snapshot), **PurchaseRatePaisa**, **PtrPaisa**, **SaleRatePaisa**, **MrpPaisa**
(all per pack), DiscountPercent, GstRatePercent; display helpers `QuantityRemainingDisplay`, `RemainingPacks`,
`RemainingLooseUnits`, `ExpiryLabel`.

- **Composition: normalised `Compositions` table + denormalised text.** Substitutes, refill follow-ups
  (`is_chronic`) and the COMPOSITION search tab all key on a composition id, and case or spacing variants of the
  same salt must match. The text stays on `Medicines` for display and LIKE search without a join;
  `MedicineRepository` derives `CompositionId` from the text on every insert/update (case-insensitive find or
  create), so the two cannot drift. Callers never set `CompositionId`.
- **Schedule:** `DrugSchedule` replaces `PrescriptionClassification` (no duplicate enum). Integers 0–5 are unchanged
  (None/OTC, H, H1, X, NRx = former Narcotic, Other), and `Rx = 6` was added for prescription-only items outside
  H/H1/X. Rules: `RequiresPrescription()`, `RequiresDoctor()`, `RequiresRegisterEntry()` (H1/X/NRx), `ToLabel()`.
- **Interim purchase entry:** `PurchaseItem` prices are still per unit, so `PurchaseService` creates batches with
  `UnitsPerPack = 1`. The Purchases module moves entry to packs (spec 04: QTY in packs, rates per pack ex-GST).
- **Interim POS:** `BillingViewModel` still bills per unit, deriving the unit rate from the per-pack sale rate.
  The Billing module replaces it with `PricingCalculator` + packs/loose entry.
- Validation lives in `MedicineService.Validate` (pack ≥ 1, GST 0–28, max ≥ min stock, discount 0–100,
  sale rate ≤ MRP, HSN 4/6/8 digits).

---

## 12. Testing and verification

- Pure logic (calculations, rules, state machines) → unit tests in `tests/MedicalShopManager.UnitTests`
  (`xunit`, `Moq`, `FluentAssertions`), named `<Module><Thing>Tests.cs`.
- Repositories, migrations, posting transactions → integration tests in `tests/MedicalShopManager.IntegrationTests`
  against a temp SQLite file (`new DatabaseManager(tempPath, NullLogger<DatabaseManager>.Instance)`, then
  `InitializeAsync()`; see `FoundationIntegrationTests`). Each posting needs a test that a failure rolls back the
  stock movements and the document number.
- Use golden vectors from the spec where they exist; state the source in a comment.
- The WPF App targets `net8.0-windows`: it **compiles** on macOS but cannot run there, so keep logic out of
  ViewModels and test it in Core/Infrastructure. After XAML changes, check every `{Binding Name}` against the
  ViewModel (WPF binding errors do not fail the build; lesson 7).
- Commands (prefix with `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1`):
  ```
  dotnet build MedicalShopManager.sln -p:EnableWindowsTargeting=true --no-incremental   # must be 0 errors
  dotnet test  MedicalShopManager.sln -p:EnableWindowsTargeting=true                    # must be all green
  ```
  Paste both results into your report. Do not tick a todo item without them.

### Hand-back checklist

- [ ] Only files in my module folders, my `<Name>Module.cs` and the existing files my module owns (§2) changed.
- [ ] Migrations use my reserved numbers; no released migration edited.
- [ ] All money through `PricingCalculator`, stored as paisa; rates per pack, quantities in units.
- [ ] Document numbers allocated in the posting transaction; `UNIQUE` on the number column.
- [ ] Pages registered with `AddPage`; views named `<X>Page`, unique; shortcuts only from my row in §5.
- [ ] Unit + integration tests added (test-only migrations 900–999, no hard-coded migration list); build 0 errors;
      tests green; XAML bindings checked.
