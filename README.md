# Medical Shop Manager (MSM)

A professional, offline-first Medical Shop Management System tailored specifically for Indian pharmacies and chemists.

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-blue)
![Framework](https://img.shields.io/badge/.NET-8.0%20LTS-purple)
![UI](https://img.shields.io/badge/UI-WPF%20%2F%20Fluent%20Design-0078D4)
![Database](https://img.shields.io/badge/Database-SQLite%20(WAL%20Mode)-003B57)
![Excel](https://img.shields.io/badge/Excel-ClosedXML%20(14%20Sheets)-217346)

---

## 🌟 Key Highlights

- **100% Offline-First**: Zero cloud dependencies. All data resides locally in the user's Documents folder (`Documents/MedicalShopManager/Data/MedicalShop.db`) using high-performance SQLite in WAL (Write-Ahead Logging) mode.
- **Paisa-Exact Monetary Arithmetic**: Eliminates floating-point rounding errors by storing all prices, taxes, and balances as integer `long` Paisa (1/100 INR).
- **Automated 14-Sheet Excel Mirror**: Powered by ClosedXML, maintaining a live, human-readable `.xlsx` workbook with formatted currency, dates, frozen headers, and formula injection protection.
- **FEFO Batch Allocation**: Enforces First Expiry, First Out dispensing, guaranteeing customers receive safe stock and preventing expired drug sales.
- **Indian GST Compliance**: Automated splitting into CGST + SGST for intrastate sales or IGST for interstate transactions across standard Indian tax slabs (0%, 5%, 12%, 18%, 28%).
- **Hot SQLite Backups**: Point-in-time snapshots created using non-blocking SQLite `VACUUM INTO` and compressed into verified ZIP archives.
- **Role-Based Access Control**: Secure BCrypt-hashed password storage, permission gates (Admin, Pharmacist, Billing Operator, Inventory Manager), and audit logging.

---

## 📂 Project Architecture

```
MedicalShopManager/
├── MedicalShopManager.sln              # Visual Studio solution file
├── src/
│   ├── MedicalShopManager.Core/         # Domain models, enums, repository & service interfaces
│   │   ├── Enums/                       # Enums: UserRole, PaymentMethod, PaymentStatus, ScheduleType, etc.
│   │   ├── Models/                      # Medicine, MedicineBatch, Sale, Purchase, Supplier, Customer, etc.
│   │   └── Interfaces/                  # IRepository<T>, Repositories, Services
│   │
│   ├── MedicalShopManager.Infrastructure/ # Data access, SQLite migrations, ClosedXML sync & services
│   │   ├── Database/DatabaseManager.cs  # SQLite lifecycle, 17+ tables, indexes, triggers, migrations
│   │   ├── Repositories/               # 14 Repository implementations
│   │   └── Services/                   # BillingService, InventoryService, MedicineService,
│   │                                    # PurchaseService, ReportService, ExcelSyncService,
│   │                                    # AuthService, AuditService, BackupService
│   │
│   └── MedicalShopManager.App/          # Windows 11 Fluent WPF application
│       ├── App.xaml / App.xaml.cs       # DI container setup, logging, startup initialization
│       ├── MainWindow.xaml / .cs        # Main sidebar navigation shell
│       ├── Resources/Theme.xaml         # Windows 11 Fluent design system and styling tokens
│       ├── Services/NavigationService.cs# Event-driven page navigation service
│       ├── ViewModels/                  # MVVM ViewModels (CommunityToolkit.Mvvm)
│       └── Views/                       # UserControls for all 14 screens
│
├── tests/
│   ├── MedicalShopManager.UnitTests/    # FEFO tests, GST tests, billing transaction tests, purchase tests
│   └── MedicalShopManager.IntegrationTests/ # SQLite schema migration tests, ClosedXML tests
│
└── tasks/
    ├── todo.md                          # Feature development tracker
    └── lessons.md                       # Architectural decisions and lessons learned
```

---

## 🚀 How to Build and Run (Windows)

### Prerequisites
- Windows 10 (version 1809+) or Windows 11 (64-bit)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. Build the Solution
```cmd
dotnet build MedicalShopManager.sln
```

### 2. Run the Unit & Integration Tests
```cmd
dotnet test MedicalShopManager.sln
```

### 3. Run the Application
```cmd
dotnet run --project src/MedicalShopManager.App/MedicalShopManager.App.csproj
```

### 4. Build Standalone .EXE (One-Click)

**Option A — Windows Double-Click (Batch):**
Simply double-click `build-exe.bat` in the project root. It will:
- Validate your .NET 8 SDK
- Run test validation
- Compile and publish `dist\publish\MedicalShopManager.exe` (self-contained, ReadyToRun, single file)
- Automatically open the output folder in File Explorer

**Option B — PowerShell:**
```powershell
.\build-exe.ps1
```
This generates `dist\publish\MedicalShopManager.exe` and a zipped release package `dist\MedicalShopManager-v1.0.0-win-x64.zip`.

**Option C — GitHub Actions (Build from macOS / Linux):**
If you are developing on a Mac or Linux machine without Windows:
1. Push this repository to GitHub.
2. In the GitHub repository, open the **Actions** tab.
3. Select **Build Windows Executable** and click **Run workflow**.
4. Once completed, download the `MedicalShopManager-Windows-Binaries` artifact containing both the standalone `.exe` and the `MedicalShopManager-Setup-v1.0.0.exe` installer.

### 5. Windows Setup Installer (Optional)
If you wish to produce a setup wizard installer (`MedicalShopManager-Setup-v1.0.0.exe`) with Desktop shortcuts and an uninstaller:
1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php).
2. Right-click `installer\MedicalShopManager.iss` and click **Compile**, or run:
   ```cmd
   iscc installer\MedicalShopManager.iss
   ```
3. The setup executable will be generated inside the `dist\` folder.

---

## 🔐 Default Credentials

On first launch, the system automatically creates the default administrator account:
- **Username**: `admin`
- **Password**: `admin123`

*(Please change this password in the User Management screen after initial setup).*

---

## 📄 License
Commercial Pharmacy Desktop Solution. Developed for Indian pharmacies.
