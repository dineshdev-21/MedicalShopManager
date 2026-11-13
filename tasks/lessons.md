# Lessons Learned

_Updated after corrections and discoveries during development._

### 1. WPF Project Assets and MSBuild
- Always ensure assets specified in `.csproj` (e.g. `<ApplicationIcon>`) actually exist in the filesystem, otherwise MSBuild fails on Windows during resource compilation.

### 2. Exact Currency Representation in Medical Shop Systems
- Always use integer `long` for currency stored as Paisa (1/100 of a Rupee). Floating-point `double` or `float` introduces rounding inaccuracies that create GST ledger discrepancies with Indian tax compliance. Convert to `decimal` exclusively for UI display and reporting.

### 3. ClosedXML Spreadsheet Formula Injection Protection
- In open Excel workbooks populated from user inputs, sanitize all text cells starting with `=`, `+`, `-`, or `@` by prepending a single quote `'` to neutralize executable spreadsheet macros and CSV/XLSX injection.

### 4. Non-Blocking SQLite Hot Backups
- Avoid copying active SQLite `.db` files directly when WAL mode is active (`-wal` and `-shm` files exist). Instead, issue `VACUUM INTO 'target.db'` over an open connection to produce a consistent, self-contained snapshot, verify with `PRAGMA integrity_check`, and compress into ZIP.

### 5. WPF Visibility and Value Converters
- WPF `Visibility` is a three-state enum (`Visible`, `Hidden`, `Collapsed`). Binding booleans, strings, or counts directly to `Visibility` without a registered `IValueConverter` (or using `Converter={x:Null}`) causes binding failures in WPF. Always register dedicated, strongly-typed converters in `App.xaml` (`BoolToVisibility`, `StringNotEmptyToVisibility`, `NullToVisibility`, `CountToVisibility`).

### 6. Contract and Model Parity Across Solution Layers
- Ensure exact alignment between interface definitions, entity models, database schemas, and service callers. Provide convenient overloads (e.g. `IExcelSyncQueueRepository.EnqueueAsync(SyncEntityType, long?)`) and alias properties (`StockMovement.Quantity`, `Notes`, `Reference`) so all service callers and tests remain decoupled yet contract-compliant.

### 7. "Verified" Means It Compiled and the Tests Ran
- Phases 1–5 were ticked "verified" after reading code, yet the first real `dotnet build` failed with 34+ errors (services and ViewModels used member names that never existed). Never tick a phase without pasting the result of `dotnet build MedicalShopManager.sln -p:EnableWindowsTargeting=true` (0 errors) and `dotnet test` from the same session; the WPF App compiles on macOS with that flag.
- Use `--no-incremental` for the final check, and grep XAML `{Binding}` names against ViewModels — WPF binding errors never fail the build.
- When a caller and the model disagree, fix the caller to the model's canonical name instead of adding alias properties (this supersedes the alias advice in lesson 6). Derive balances (supplier/customer outstanding) in SQL from source rows rather than keeping a running total that can drift.

### 8. Sequences That Reset Per Financial Year Need Per-Year Counters
- A single "next number" that resets on the first document of a new FY re-issues numbers to documents back-dated into the old FY (`SR/2627/00002` twice). Key counters by (document type, FY start year) and allocate inside the posting transaction (`DocumentSequenceAllocator.AllocateAsync`), so a rollback never consumes a number.

### 9. Per-Pack Rates, Unit Quantities
- Store MRP/PTR/purchase/sale rates per pack and quantities in units; compute amount = round(rate × units / unitsPerPack) once per line. Per-unit paisa prices cannot represent ₹35.50 for a 15's strip exactly. Snapshot the pack size on the batch so editing the item never re-prices old stock.

### 10. Restore Into a Live WAL Database Through SQLite, Never by File Copy
- Copying a backup file over the open `.db` leaves the old `-wal` beside it; SQLite can replay those frames onto the restored file, and a file-copy "safety copy" misses commits not yet checkpointed. Restore with the online backup API (`source.BackupDatabase(liveConnection)`, atomic), take the safety copy with `VACUUM INTO`, then run `DatabaseManager.InitializeAsync()` so an older backup is migrated immediately. Refuse backups whose `SchemaMigrations` holds versions this build does not know.

### 11. Tests Must Survive Other Modules' Migrations
- A test that fakes migration v13 or asserts the applied set is `{1, 2, 3}` breaks the day Stock adds v13. Fake migrations use 900–999 and expectations come from `MigrationCatalog.Discover()`. Verified by adding a throwaway Orderbook v15 slice: two such tests failed until fixed.
