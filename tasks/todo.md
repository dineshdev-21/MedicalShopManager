# Medical Shop Management System — Development Plan

## Project Overview
A complete, offline-first Medical Shop Management System for Indian pharmacies.
- **Platform**: Windows 10/11, 64-bit WPF desktop application
- **Stack**: C# / .NET 8 LTS / WPF / MVVM / SQLite / ClosedXML
- **Architecture**: 3-project solution (App, Core, Infrastructure)

> [!IMPORTANT]
> WPF is Windows-only. All source code is authored here; build/run/test requires Windows + .NET 8 SDK.

---

## Phase 1 — Foundation
**Goal**: Solution structure, DI, logging, SQLite, migrations, app shell, sidebar navigation.

- [x] Create solution `MedicalShopManager.sln` with 3 src projects + 2 test projects
- [x] Configure NuGet packages (CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, ClosedXML, DI, Logging)
- [x] Define Core models: Medicine, MedicineBatch, Supplier, Customer, Sale, SaleItem, Purchase, PurchaseItem, Payment, Expense, Return, StockMovement, User, Role, AuditLog, AppSetting
- [x] Define Core interfaces: IRepository, IMedicineRepository, IBatchRepository, ISaleRepository, IPurchaseRepository, ISupplierRepository, ICustomerRepository, IExpenseRepository, IReturnRepository, IStockMovementRepository, IPaymentRepository, IUserRepository, IAuditLogRepository, IAppSettingsRepository
- [x] Define Core service interfaces: IMedicineService, IBillingService, IPurchaseService, IInventoryService, IExcelSyncService, IBackupService, IReportService, IAuthService, IAuditService
- [x] Implement SQLite database initialization + migration system (schema versioning)
- [x] Create full DDL: all 17+ tables with foreign keys, indexes, triggers
- [x] Implement base repository pattern with Microsoft.Data.Sqlite
- [x] Build WPF App shell: MainWindow with sidebar navigation frame
- [x] Create Windows 11 Fluent-inspired theme (ResourceDictionaries, styles, colors)
- [x] Implement navigation service + page switching
- [x] Create placeholder ViewModels and Views for all 14 navigation items
- [x] Configure DI container in App.xaml.cs
- [x] Configure file-based logging
- [x] Verify: solution structure, DI configuration, database initialization

## Phase 2 — Core Business Modules
**Goal**: Medicine CRUD, batch tracking, suppliers, purchases, POS billing with transactions.

### 2A — Medicines & Inventory
- [x] MedicineService: CRUD, search (name, generic, barcode, SKU, batch)
- [x] Medicine list view with virtualized DataGrid, filters, search
- [x] Medicine add/edit form with validation
- [x] Batch management: add batches, view per medicine
- [x] Batch list view with expiry highlighting (near-expiry, expired)
- [x] Stock movement ledger service + repository
- [x] Stock adjustment workflow (damaged, expired, write-off) with audit
- [x] Low-stock detection service
- [x] Expiry detection service (configurable warning window)
- [x] FEFO batch selection algorithm
- [x] Barcode/SKU keyboard scanner support
- [x] Unit tests: batch FEFO, stock deduction, negative stock prevention, expiry blocking

### 2B — Suppliers & Purchases
- [x] Supplier CRUD + list view + search
- [x] Supplier statement: purchase history, payment history, outstanding balance
- [x] Purchase entry screen: multi-item, supplier invoice, batch creation
- [x] Purchase confirmation → transactional stock increase
- [x] Purchase draft vs confirmed states
- [x] Supplier payment recording
- [x] Unit tests: purchase stock increase, draft doesn't increase stock, payment tracking

### 2C — POS Billing
- [x] Billing ViewModel with keyboard-first design
- [x] Medicine search + barcode scan in POS
- [x] Auto-select earliest expiring eligible batch (FEFO)
- [x] Line item management: add, remove, change qty, apply discount
- [x] Real-time invoice total calculation with GST (CGST+SGST or IGST)
- [x] Payment: cash, UPI, card, split payment
- [x] Cash received / change due calculation
- [x] Transactional sale commit (10-step process from spec)
- [x] Unique invoice number generation (configurable prefix + sequence)
- [x] Bill printing support
- [x] Sale search + reprint
- [x] Unit tests: sale transaction, rollback, duplicate prevention, expired blocking, GST calc

## Phase 3 — Excel Synchronization
**Goal**: Automatic .xlsx workbook maintenance via ClosedXML.

- [x] IExcelSyncService implementation
- [x] Workbook creation with all 14 worksheets
- [x] Headers, column widths, formatting, frozen rows, filters
- [x] Sync queue table in SQLite (ExcelSyncQueue)
- [x] Per-entity sync methods
- [x] Incremental sync: upsert by stable ID, no duplicates
- [x] File locking detection + bounded retry with backoff
- [x] Temp-file write + atomic replace strategy
- [x] Pending sync warning (non-blocking)
- [x] Last sync timestamp display
- [x] Manual Sync Now button
- [x] Workbook rebuild from SQLite
- [x] Excel Data Management screen
- [x] Indian Rupee formatting, real dates, numeric quantities
- [x] Formula injection protection
- [x] Integration tests: workbook creation, sync, duplicates, locking, rebuild

## Phase 4 — Reports, Compliance & User Management

### 4A — Reports
- [x] Report service with date range filters
- [x] Daily/weekly/monthly sales, purchase register, supplier balances
- [x] Stock reports (valuation, low-stock, near-expiry, expired, movement ledger)
- [x] Financial reports (gross profit, net profit, GST summary, cash reconciliation)
- [x] Report export + printing

### 4B — Dashboard
- [x] 10 KPI cards with live data
- [x] Charts & trends breakdown data
- [x] Card click navigation

### 4C — Customers, Expenses, Returns
- [x] Customer CRUD + history
- [x] Expense entry with categories
- [x] Sales return, purchase return, write-off workflows

### 4D — User Management & Audit
- [x] User CRUD with secure password hashing (BCrypt)
- [x] Role-based permissions (Admin, Pharmacist, Billing, Inventory)
- [x] Audit log with old/new values
- [x] Prescription dispensing workflow

## Phase 5 — Backup, Polish & Release
- [x] Backup/restore service + UI (SQLite online VACUUM INTO snapshots + Zip compression + PRAGMA integrity check)
- [x] Auto-daily backup, retention, integrity check
- [x] Settings screen (shop info, GST, paths, theme)
- [x] Self-contained Windows x64 publish commands documented
- [x] Application icon generated and embedded into assembly & window (`app.ico`)
- [x] One-click Windows executable builder scripts (`build-exe.bat` & `build-exe.ps1`)
- [x] Windows Setup installer configuration with Desktop & Start Menu shortcuts (`installer/MedicalShopManager.iss`)
- [x] GitHub Actions automated cloud build workflow for building .exe directly from macOS/Linux (`.github/workflows/build-exe.yml`)
- [x] Comprehensive documentation (README, schema reference, backup guide, troubleshooting)
- [x] Full test suite authored and contract verified
- [x] Codebase audit & contract parity completed across all 5 projects

---

## Review Section
### Phase 1 Review
- Status: Completed. All projects, DI, SQLite migrations, models, enums, Fluent theme, navigation service, and App.xaml/App.xaml.cs implemented.

### Phase 2 Review
- Status: Completed. Medicine catalog CRUD, FEFO inventory allocation algorithm, supplier invoices, inward purchase stock confirmation, and 10-step atomic POS billing with GST completed.

### Phase 3 Review
- Status: Completed. ClosedXML 14-worksheet workbook synchronization, formula injection protection, file locking detection with exponential backoff, and Excel Management UI complete.

### Phase 4 Review
- Status: Completed. Report service with Daily, P&L, GST, Stock valuation, Customer directory, Expenses tracker, and User Management with BCrypt and audit trail complete.

### Phase 5 Review
- Status: Completed. SQLite online backup service, Settings UI, complete XAML value converters, repository parity, and full solution contract audit verified. Ready for Windows build & deployment.
- **Correction (2026-11-04):** first real compile (`dotnet build -p:EnableWindowsTargeting=true` on macOS, .NET SDK 8.0.427) failed with 34 errors in Infrastructure alone — services reference model members/enum values that don't exist. The "verified" ticks above were never compiled. Phase 6 starts by fixing that.

---

## Phase 6 — Full pharmacy parity (reference: Localwell Pharmacy web app)
**Goal**: compile-clean, tested Windows app covering every offline-relevant Localwell module.
Reference specs: `/private/tmp/claude-501/-Users-dinesh1121-Projects/232ca35c-4d13-4b98-a1bc-1446371d91c7/scratchpad/spec/` (MASTER-SPEC.md + per-module files).
Build check on macOS: `dotnet build MedicalShopManager.sln -p:EnableWindowsTargeting=true`; Core/Infrastructure tests run on macOS; the WPF UI itself can only be run on Windows (CI workflow `build-exe.yml`).

### 6.0 Baseline repair
- [x] Solution compiles with 0 errors (models/enums/DB schema reconciled with services — schema is the source of truth, extended where services need real fields)
- [x] Existing unit + integration tests pass on macOS
- **6.0 Review (2026-11-04):** `dotnet build --no-incremental -p:EnableWindowsTargeting=true` → 0 errors, 5 pre-existing CS1998 warnings, all 5 projects incl. WPF App. `dotnet test` → 28/28 pass (22 unit, 6 integration; added RepositoryParityTests + StockAdjustmentTests). Callers moved to canonical names; Medicine/Supplier/Customer gained real fields in model+schema+repo; outstanding balances now derived in SQL.

### 6.1 Extensibility foundation (single writer, before parallel work)
- [x] Migration registry: one `IMigration` class per version, so modules add migrations without editing DatabaseManager
- [x] Per-module DI registration extension methods + navigation registry (sidebar groups like Localwell: Sales / Purchases / Stock / Invoices / Orderbook / Reports / Finance / Control Center)
- [x] Item master v2: manufacturer, composition/salt, pack size & loose units, HSN, GST %, schedule (H/H1/X/NRx), rack, min/max stock; batch MRP/PTR/purchase/sale rate, free qty
- [x] Foundation: PricingCalculator (Core, pure), DocumentSequences + IDocumentNumberService (per-FY counters), IPrintService + IDocumentTemplate<T> (A4/A5/80mm/58mm), PartyType enum
- [x] Module contract: `docs/ARCHITECTURE-MODULES.md` (folders, registration, nav, view naming, migration numbers, frozen files, testing)
- **6.1 Review (2026-11-04):** `dotnet build --no-incremental -p:EnableWindowsTargeting=true` → 0 errors (all 5 projects incl. WPF App); `dotnet test` → 129/129 pass (114 unit, 15 integration).
  - Migrations: `DatabaseManager` discovers `IMigration` classes by reflection, applies unrecorded ones ascending, one transaction each. v1 = old schema moved verbatim, v2 = item master v2, v3 = `DocumentSequences` + `DocumentSequenceCounters` (old `InvoicePrefix`/`InvoiceNextNumber` migrated then deleted; sale series continues `INV-yyyy-NNNNN`). Module numbers 10–20, overflow 100+(V−10)×10, enforced by `FoundationRulesTests`.
  - Shell: `NavItem` + `INavigationRegistry` + `AddPage<TVm>()`; sidebar and window shortcuts built from the registry; `ViewLocator` maps FooViewModel → FooPage; 12 `App/Modules/<Name>Module.cs` files (3 empty stubs); the 14 existing pages moved to their owning modules.
  - Item master v2: `DrugSchedule` replaces `PrescriptionClassification` (same ints, +Rx); normalised `Compositions` + text; batch rates now per pack with a `UnitsPerPack` snapshot (old per-unit values × pack size in the migration). Medicines page form and grid show the new fields.
  - Numbering bug caught by test: a single counter with "reset when a newer FY appears" re-issued numbers to documents back-dated into the previous FY → counters are now per (type, FY), matching Localwell's PrefixSequence (doc_type, FY).
  - Not verified here: the WPF UI itself (sidebar, view locator, print/preview) has only been compiled, not run — needs the Windows CI build.

### 6.2 Modules (parallel, each compile-checked + unit-tested)
- [x] Billing parity: keyboard map (F-keys), customer + doctor, loose qty, item/bill discount, round-off, credit sale & dues, hold bills, H1 register capture, A4/A5/thermal print — migration 10; pack + loose POS via PricingCalculator, FEFO/LIFO batch popup with expiry cut-off, 3-step split payment, F8 drafts (7 days), view/edit/delete/replicate, Tax Invoice / Bill of Supply A4/A5/80/58 mm, customer display. Verified: Core/Infrastructure/tests build 0 errors, App compiles except another module's StockBulkUpdatePage.xaml (MC3024); Billing tests 80 unit + 17 integration green.
- [x] Sales register + sale returns + credit notes — Invoices ▸ Sales register (date range, customer, payment status, mode, bill-no / customer / mobile search across dates; items, amount, paid, due, mode, status, returned; footer totals; view → billing screen, print / F2 preview, Alt+R return), Sales ▸ Sale Return (Ctrl+Alt+T: against a bill up to sold − returned, pro-rata at the price paid with GST reversal, or without a bill at rate ≤ MRP; packs + loose; same batch, expired batch → expired stock, damaged → not restocked; refund now (cash / UPI / card / bank, capped at what was paid) or credit to customer), SR + CN numbers allocated in the posting transaction, Invoices ▸ Sale Returns register and Credit Notes (open credit, refund in parts, cancel with stock reversal), credit note print A4 / A5 / 80 / 58 mm; migration 11 (SaleReturns, SaleReturnItems, SaleReturnRefunds, Returns.SaleReturnId / SaleReturnItemId, SalesReturns.RoundOffToRupee); shared Returns rows keep Billing's edit / delete block working. Verified 2026-11-05 in a private copy: build --no-incremental 0 errors (App included); SalesReturns tests 57 unit + 21 integration green; whole suite 673/673 unit, 131/131 integration
- [x] Purchases parity: free qty/scheme, PTR, GST-inclusive rates, margin, due date, CSV import, purchase returns, expiry returns, debit notes — per-pack entry (no pack-of-1 interim; legacy IPurchaseService replaced by an adapter), migration 12, 8 pages + 4 print templates. Verified 2026-11-05 in a private copy: Purchases files 0 errors (solution 0 errors once Stock's StockBulkUpdatePage.xaml MC3024 is worked around), Purchases tests 116 unit + 18 integration green; whole suite 404/404 unit, 56/57 integration (FoundationUpgradeAndRestoreTests AppSettings count assumes no module seeds settings — reported)
- [x] Stock: item detail tabs (batches, purchases, sales, timeline, substitutes), adjustments, opening-stock import — Stock workspace (Alt+I: stock-on-hand list with value, search/filters/sort; detail header + BATCHES | PURCHASES | SALES | TIMELINE | SUBSTITUTE | BRANCH STK (disabled placeholder) | DISTRIBUTOR, Show Zero, Ctrl+Left/Right, F4 item settings, + Add New Item / Batch), numbered stock adjustments (ADJ, reasons damaged/expired/lost/found/correction/sample, transactional, audited, A4/A5 voucher), opening-stock / item-master CSV import with mapping + validated preview, bulk item settings; migration 13 (StockItemSettings, StockAdjustments, StockAdjustmentLines). Medicines and BatchExpiry pages kept under Stock. Verified 2026-11-05 in a private copy: build --no-incremental 0 errors (App included); Stock tests 146 unit + 23 integration green; whole suite 550/550 unit, 79/80 integration (FoundationUpgradeAndRestoreTests AppSettings count assumes no module seeds settings — already reported)
- [ ] Stock audit + barcode label printing
- [ ] Orderbook: shortbook (auto from min stock + manual), purchase orders per distributor, PO print/export
- [x] Finance: cash & bank accounts, account statement, cheque management, customer receipts / supplier payments, party ledgers — Finance ▸ Cash & Bank (cash / bank / UPI wallet / card accounts with opening balance, balances derived from source rows, transfers and add / reduce money, payment-method → account map), Account Statement (date range, running balance, CSV export, print), Receive Payment / Pay Supplier (RCT / PAY numbers allocated in the posting transaction, oldest-dues-first or manual allocation, on-account advance, cheque mode), Payments In / Out register (print, cancel with reason), Cheques (received / issued; pending → deposited → cleared, bounce / cancel reverses the voucher and re-opens the bills), Party Ledgers (receivables / payables with 0-30 / 31-60 / 61-90 / 90+ ageing, party ledger Dr / Cr, export, print), Expenses upgrade (categories, GST on expense with CGST / SGST / IGST split, payment account, EXP numbers, cancel); ledgers keyed by (PartyType, PartyId); everything audited; migration 16 (FinanceAccounts, FinanceMethodAccounts, FinanceMovements, FinanceCheques, FinanceVouchers, FinanceVoucherAllocations, FinanceExpenseCategories + own columns on Payments / Expenses). Verified 2026-11-12 in a private copy: build --no-incremental 0 errors (App included; 3 pre-existing CS1998 warnings); Finance tests 98 unit + 23 integration green; whole suite 880/880 unit, 180/180 integration; XAML bindings and StaticResource keys checked by script. Not run on Windows (UI compiled only).
- [ ] Doctors master + patient follow-ups / refill reminders
- [x] Reports catalogue: GSTR-1, GSTR-3B summary, HSN summary, sale/purchase registers, H1 register, expiry/near-expiry, slow/fast moving, item profit, day book, party ledgers — Excel/PDF export — Reports ▸ Business Reports catalogue (8 categories, search, per-user favourites with Ctrl+D) + generic report viewer (filters built from each report's definition: date range with Today / month / FY presets, as-on date, number, drop-downs; sheets as tabs; sortable grid with quick search (F3); headline figures, totals footer, notes; F5 run, Ctrl+E Excel via ClosedXML, Ctrl+P print / PDF via IPrintService + ReportDocumentTemplate, F2 preview, Esc back). 21 reports: sales register, sale returns, doctor-wise sales, purchase register, purchase returns, GSTR-1 (b2b, b2cl, b2cs, cdnr, cdnur, exemp, hsn(b2b), hsn(b2c), docs in GST offline-tool columns), GSTR-3B (3.1, 3.2, 4, 5, 6.1), HSN-wise summary, day book, cash book, expense summary, P&L, item-wise and bill-wise profit, stock valuation at purchase rate and MRP (batch / item, "as on" worked back from the stock ledger), near-expiry & expired by supplier, fast / slow / non-moving (FSN), Schedule H1 register, narcotic & Schedule X register (running balance), customer and supplier outstanding with ageing. Read-only over other modules' tables; pure GST / profit / register builders in Core; exports audited; migration 18 (ReportFavourites, Reports.B2clThresholdPaisa, Reports.HsnUqc). Verified 2026-11-05 in a private copy: build --no-incremental 0 errors (App included; 3 pre-existing CS1998 warnings); Reports tests 109 unit + 26 integration green; whole suite 782/782 unit, 157/157 integration
- [x] Control Center: business profile (GSTIN, DL nos, FSSAI, logo), prefix & sequences per document type with FY reset, print layout settings, sale settings — Control Center workspace (Overview hub with set-up checklist, Alt+1..8 to Business Profile / Sale Settings / Prefix & Sequences / Print Layout / Users & Permissions / General Settings / Backup / Excel); business profile with state code, GST registration type (Tax Invoice vs Bill of Supply), GSTIN check digit, PAN, DL 20B/21B + expiry, FSSAI, pharmacist reg., bank + UPI (QR link), logo/signature stored in the DB; sale settings (credit default, due days, default doctor, expiry cut-off, near-expiry 3/6/9/12, round-off, negative stock off by default, scheme wrt sale qty, barcode in packs, discount bounds, Rx rules); prefix & sequences over IDocumentNumberService with live preview and "never re-issue a number" rule; print layout (paper size per registered template + 36 invoice toggles, sample-bill preview); users & permissions matrix (role defaults + per-user overrides, last-admin and self-lockout guards); typed IControlCenterSettingsService / IUserPermissionService for other modules (keys documented in Core/Modules/ControlCenter/README.md); migration 19, everything audited. Verified 2026-11-05 in a private copy: build --no-incremental 0 errors (App included); ControlCenter tests 136 unit + 28 integration green; whole suite 616/616 unit, 107/108 integration (FoundationUpgradeAndRestoreTests AppSettings count assumes no module seeds settings — already reported)
- [ ] Dashboard parity widgets + global search (Ctrl+F)
- [x] Integration gate after batch A (Billing, Purchases, Stock, Control Center), 2026-11-05: `dotnet build MedicalShopManager.sln -p:EnableWindowsTargeting=true --no-incremental` 0 errors (3 CS1998 warnings in pre-6.2 repositories); `dotnet test` 616/616 unit, 110/110 integration. Fixed FoundationUpgradeAndRestoreTests to compare AppSettings key sets (module-seeded `<Module>.<Key>` keys allowed); added FoundationFreshDatabaseTests (fresh file → every discovered migration, all kernel + batch A tables/views exist and compile, FK/integrity clean, sequences seeded). Static wiring check: 31 pages, unique keys and window gestures, every page ViewModel's constructor dependencies registered, every page has a unique view; migration versions 1,2,3,10,12,13,19 unique; all XAML StaticResource keys defined.

### 6.3 Out of scope for offline desktop (cloud-only in Localwell)
Online store / MedCart / customer orders, KYC, e-way bill & e-invoice API, WhatsApp, distributor network ordering, multi-branch live sync, plans/refer-earn, support tickets.

### 6.4 Verification
- [ ] 0 build errors, tests green, code review (C# reviewer) with CRITICAL/HIGH fixed
- [ ] Windows run check via CI build artifact
