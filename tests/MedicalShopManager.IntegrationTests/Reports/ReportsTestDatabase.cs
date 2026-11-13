using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Reports;
using MedicalShopManager.Infrastructure.Repositories;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Reports;

/// <summary>
/// A temp SQLite database with every migration applied and a small October-2026 scenario seeded with plain SQL in the
/// shape the Billing / SalesReturns / Purchases / Finance / Stock modules write it (so these tests do not depend on
/// their services). Shop state: Karnataka (29). All money in paisa.
/// <list type="bullet">
/// <item>Items: Dolo 650 (15's, GST 12, HSN 30049099), Azithral 500 (5's, Schedule H1, HSN 30042019), Codeine syrup
/// (Schedule X, HSN 30049011), Plain bandage (GST 0, HSN 30051090, never sold).</item>
/// <item>Sales: INV-1 to a registered clinic (₹112), INV-2 walk-in H1 with patient and doctor (₹224), INV-3 inter-state
/// credit sale to an unregistered customer above the B2CL limit (₹1,12,000), INV-4 walk-in Schedule X (₹112),
/// INV-5 cancelled.</item>
/// <item>Return SR-1 / CN-1 of 3 Dolo units against INV-1, refunded in cash.</item>
/// <item>Purchases PUR-1 (intra, ₹760, ₹500 due) and PUR-2 (inter-state, paid); debit note DN-1 (₹56) against PUR-1.</item>
/// <item>Expenses: rent ₹10,000 cash (legacy category), electricity ₹1,180 incl. GST by UPI.</item>
/// </list>
/// </summary>
public sealed class ReportsTestDatabase : IAsyncLifetime
{
    public static readonly DateTime From = new(2026, 10, 1);
    public static readonly DateTime To = new(2026, 10, 31);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"MSM_Reports_{Guid.NewGuid():N}.db");

    public DatabaseManager Db { get; private set; } = null!;
    public long Dolo, Azithral, Codeine, Bandage;
    public long BatchDolo, BatchAzithral, BatchCodeine, BatchBandage;
    public long Clinic, Ravi, DistributorKa, DistributorMh;
    public long Sale1, Sale2, Sale3, Sale4, Sale5, SaleItem1;
    public long Purchase1, Purchase2;

    public bool Seed { get; init; } = true;

    public async Task InitializeAsync()
    {
        Db = new DatabaseManager(_path, NullLogger<DatabaseManager>.Instance);
        await Db.InitializeAsync();
        if (Seed) await SeedAsync();
    }

    public Task DisposeAsync()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<IReportProvider> Providers() => new IReportProvider[]
    {
        new SalesReportsProvider(Db), new PurchaseReportsProvider(Db), new GstReportsProvider(Db, new AppSettingsRepository(Db)),
        new AccountReportsProvider(Db), new ProfitReportsProvider(Db), new StockReportsProvider(Db), new ComplianceReportsProvider(Db),
        new PartyReportsProvider(Db)
    };

    public ReportRunner Runner() => new(Providers());

    public Task<ReportResult> RunAsync(string id, DateTime? from = null, DateTime? to = null, DateTime? asOf = null, int? number = null,
        params (string Key, string Value)[] choices) =>
        Runner().RunAsync(id, new ReportParameters
        {
            From = from ?? From, To = to ?? To, AsOf = asOf ?? new DateTime(2026, 11, 5), Number = number,
            Choices = choices.ToDictionary(c => c.Key, c => c.Value)
        });

    public async Task<long> ExecAsync(string sql, params (string, object?)[] args)
    {
        using var conn = Db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql + "; SELECT last_insert_rowid();";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private Task<long> Medicine(string name, int pack, decimal gst, string hsn, int schedule) => ExecAsync(@"
        INSERT INTO Medicines (Name, Manufacturer, UnitsPerPack, GstRatePercent, HsnCode, Schedule, Status) VALUES (@N, 'Micro Labs', @P, @G, @H, @S, 0)",
        ("@N", name), ("@P", pack), ("@G", (double)gst), ("@H", hsn), ("@S", schedule));

    private Task<long> Batch(long medicine, string number, string expiry, long? supplier, long remaining, long rate, long mrp, int pack, decimal gst) => ExecAsync(@"
        INSERT INTO MedicineBatches (MedicineId, BatchNumber, ExpiryDate, PurchaseDate, SupplierId, QuantityReceived, QuantityRemaining,
                                     PurchaseRatePaisa, MrpPaisa, UnitsPerPack, GstRatePercent)
        VALUES (@M, @B, @E, '2026-09-01', @S, @Q, @Q, @R, @Mrp, @P, @G)",
        ("@M", medicine), ("@B", number), ("@E", expiry), ("@S", supplier), ("@Q", remaining), ("@R", rate), ("@Mrp", mrp), ("@P", pack), ("@G", (double)gst));

    private Task<long> Sale(string number, string at, long? customer, string? name, string? gstin, bool inter, long total, long paid, int method,
        int status = 1, string? patient = null, string? doctor = null, string? regNo = null) => ExecAsync(@"
        INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, CustomerName, CustomerGstin, IsInterState, WithGst, GrandTotalPaisa,
                           AmountPaidPaisa, BalanceDuePaisa, PaymentMethod, Status, PatientName, DoctorName, DoctorRegNo)
        VALUES (@No, @At, @C, @Name, @G, @I, 1, @T, @Paid, @T - @Paid, @M, @St, @Pat, @Doc, @Reg)",
        ("@No", number), ("@At", at), ("@C", customer), ("@Name", name), ("@G", gstin), ("@I", inter ? 1 : 0), ("@T", total), ("@Paid", paid),
        ("@M", method), ("@St", status), ("@Pat", patient), ("@Doc", doctor), ("@Reg", regNo));

    private Task<long> SaleItem(long sale, long medicine, string name, long batch, string batchNo, string hsn, long units, long taxable,
        long cgst, long sgst, long igst, long cogs, int pack, int schedule) => ExecAsync(@"
        INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate, HsnCode, Quantity, UnitPricePaisa,
                               TaxableValuePaisa, GstRatePercent, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa, TaxAmountPaisa,
                               CogsPaisa, LineTotalPaisa, UnitsPerPack, Schedule, Manufacturer)
        VALUES (@S, @M, @N, @B, @BNo, '2027-12-31', @H, @Q, 0, @Tx, 12, @C, @Sg, @I, @C + @Sg + @I, @Cogs, @Tx + @C + @Sg + @I, @P, @Sch, 'Micro Labs')",
        ("@S", sale), ("@M", medicine), ("@N", name), ("@B", batch), ("@BNo", batchNo), ("@H", hsn), ("@Q", units), ("@Tx", taxable),
        ("@C", cgst), ("@Sg", sgst), ("@I", igst), ("@Cogs", cogs), ("@P", pack), ("@Sch", schedule));

    private async Task SeedAsync()
    {
        await ExecAsync("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('ControlCenter.Business.StateCode', '29')");

        DistributorKa = await ExecAsync("INSERT INTO Suppliers (Name, Phone, Gstin, StateCode, Address) VALUES ('Pharma Distributors', '9845000001', '29ABCDE1234F1Z5', '29', 'Bengaluru')");
        DistributorMh = await ExecAsync("INSERT INTO Suppliers (Name, Gstin, StateCode, Address) VALUES ('MH Traders', '27ABCDE1234F1Z5', '27', 'Pune')");
        Clinic = await ExecAsync("INSERT INTO Customers (Name, Gstin) VALUES ('Apollo Clinic', '29AAACA1111A1Z1')");
        Ravi = await ExecAsync("INSERT INTO Customers (Name, Phone, OpeningBalancePaisa, OpeningBalanceDate) VALUES ('Ravi Kumar', '9900000000', 50000, '2026-09-01')");
        var doctor = await ExecAsync("INSERT INTO Doctors (Name, RegistrationNo, Specialization, Address) VALUES ('Dr. Rao', 'KMC123', 'Physician', 'Jayanagar')");

        Dolo = await Medicine("Dolo 650", 15, 12, "30049099", 0);
        Azithral = await Medicine("Azithral 500", 5, 12, "30042019", 2);
        Codeine = await Medicine("Codeine syrup", 1, 12, "30049011", 3);
        Bandage = await Medicine("Plain bandage", 1, 0, "30051090", 0);
        BatchDolo = await Batch(Dolo, "D1", "2027-12-31", DistributorKa, 150, 9000, 15000, 15, 12);
        BatchAzithral = await Batch(Azithral, "A1", "2026-12-31", DistributorKa, 10, 8000, 12000, 5, 12);
        BatchCodeine = await Batch(Codeine, "C1", "2026-10-31", DistributorMh, 4, 5000, 9000, 1, 12);
        BatchBandage = await Batch(Bandage, "P1", "2028-01-31", null, 20, 1000, 2000, 1, 0);

        Sale1 = await Sale("INV-1", "2026-10-05 10:00:00", Clinic, "Apollo Clinic", "29AAACA1111A1Z1", false, 11200, 11200, 0);
        SaleItem1 = await SaleItem(Sale1, Dolo, "Dolo 650", BatchDolo, "D1", "30049099", 30, 10000, 600, 600, 0, 6000, 15, 0);
        Sale2 = await Sale("INV-2", "2026-10-06 11:00:00", null, null, null, false, 22400, 22400, 1, patient: "Sita", doctor: "Dr. Rao", regNo: "KMC123");
        await ExecAsync("UPDATE Sales SET DoctorId = @D, PatientAddress = 'Basavanagudi' WHERE Id = @S", ("@D", doctor), ("@S", Sale2));
        await SaleItem(Sale2, Azithral, "Azithral 500", BatchAzithral, "A1", "30042019", 5, 20000, 1200, 1200, 0, 8000, 5, 2);
        Sale3 = await Sale("INV-3", "2026-10-07 12:00:00", Ravi, "Ravi Kumar", null, true, 11_200_000, 0, 4);
        await SaleItem(Sale3, Dolo, "Dolo 650", BatchDolo, "D1", "30049099", 600, 10_000_000, 0, 0, 1_200_000, 6_000_000, 15, 0);
        Sale4 = await Sale("INV-4", "2026-10-08 13:00:00", null, "Ram", null, false, 11200, 11200, 0, patient: "Ram", doctor: "Dr. Iyer");
        await SaleItem(Sale4, Codeine, "Codeine syrup", BatchCodeine, "C1", "30049011", 2, 10000, 600, 600, 0, 10000, 1, 3);
        Sale5 = await Sale("INV-5", "2026-10-09 14:00:00", null, null, null, false, 5000, 5000, 0, status: 2);
        await SaleItem(Sale5, Dolo, "Dolo 650", BatchDolo, "D1", "30049099", 5, 4464, 268, 268, 0, 1000, 15, 0);

        var ret = await ExecAsync(@"
            INSERT INTO SaleReturns (ReturnNumber, CreditNoteNumber, ReturnDate, SaleId, SaleInvoiceNumber, CustomerId, CustomerName, CustomerGstin,
                                     RefundMode, TaxablePaisa, CgstPaisa, SgstPaisa, TaxPaisa, NetAmountPaisa, Status)
            VALUES ('SR-1', 'CN-1', '2026-10-10', @S, 'INV-1', @C, 'Apollo Clinic', '29AAACA1111A1Z1', 1, 1000, 60, 60, 120, 1120, 1)",
            ("@S", Sale1), ("@C", Clinic));
        await ExecAsync(@"
            INSERT INTO SaleReturnItems (SaleReturnId, SaleItemId, MedicineId, MedicineName, HsnCode, BatchId, BatchNumber, UnitsPerPack, Units,
                                         GstRatePercent, TaxablePaisa, CgstPaisa, SgstPaisa, TaxPaisa, LineTotalPaisa)
            VALUES (@R, @SI, @M, 'Dolo 650', '30049099', @B, 'D1', 15, 3, 12, 1000, 60, 60, 120, 1120)",
            ("@R", ret), ("@SI", SaleItem1), ("@M", Dolo), ("@B", BatchDolo));
        await ExecAsync("INSERT INTO SaleReturnRefunds (SaleReturnId, RefundDate, Method, AmountPaisa) VALUES (@R, '2026-10-10', 0, 1120)", ("@R", ret));

        Purchase1 = await ExecAsync(@"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, PurchaseNumber, Status, TaxablePaisa, CgstPaisa,
                                   SgstPaisa, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa, DueDate)
            VALUES ('PD-101', @S, 'Pharma Distributors', '2026-10-02', 'PUR-1', 1, 70000, 3000, 3000, 76000, 26000, 50000, '2026-11-01')",
            ("@S", DistributorKa));
        await ExecAsync(@"
            INSERT INTO PurchaseItems (PurchaseId, MedicineId, MedicineName, BatchNumber, ExpiryDate, Quantity, PurchasePricePaisa, GstRatePercent,
                                       TaxablePaisa, CgstPaisa, SgstPaisa, HsnCode, UnitsPerPack)
            VALUES (@P, @M, 'Dolo 650', 'D1', '2027-12-31', 75, 0, 12, 50000, 3000, 3000, '30049099', 15),
                   (@P, @B, 'Plain bandage', 'P1', '2028-01-31', 20, 0, 0, 20000, 0, 0, '30051090', 1)",
            ("@P", Purchase1), ("@M", Dolo), ("@B", Bandage));
        Purchase2 = await ExecAsync(@"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, PurchaseNumber, Status, IsInterState, TaxablePaisa,
                                   IgstPaisa, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa)
            VALUES ('MH-55', @S, 'MH Traders', '2026-10-03', 'PUR-2', 1, 1, 20000, 2400, 22400, 22400, 0)",
            ("@S", DistributorMh));
        await ExecAsync(@"
            INSERT INTO PurchaseItems (PurchaseId, MedicineId, MedicineName, BatchNumber, ExpiryDate, Quantity, PurchasePricePaisa, GstRatePercent,
                                       TaxablePaisa, IgstPaisa, HsnCode)
            VALUES (@P, @M, 'Codeine syrup', 'C1', '2026-10-31', 6, 0, 12, 20000, 2400, '30049011')",
            ("@P", Purchase2), ("@M", Codeine));
        await ExecAsync("INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, Status, GrandTotalPaisa, BalancePaisa) VALUES ('DRAFT', @S, 'Pharma Distributors', '2026-10-04', 0, 99999, 99999)",
            ("@S", DistributorKa));

        var debit = await ExecAsync(@"
            INSERT INTO PurchaseReturns (ReturnNumber, ReturnDate, Kind, SupplierId, SupplierName, PurchaseId, Reason, TaxablePaisa, CgstPaisa, SgstPaisa,
                                         TaxAmountPaisa, NetAmountPaisa)
            VALUES ('DN-1', '2026-10-15', 1, @S, 'Pharma Distributors', @P, 3, 5000, 300, 300, 600, 5600)",
            ("@S", DistributorKa), ("@P", Purchase1));
        await ExecAsync(@"
            INSERT INTO PurchaseReturnItems (PurchaseReturnId, MedicineId, MedicineName, HsnCode, BatchId, BatchNumber, ExpiryDate, UnitsPerPack, Quantity,
                                             GstRatePercent, TaxablePaisa, TaxPaisa, CgstPaisa, SgstPaisa, LineTotalPaisa)
            VALUES (@R, @M, 'Dolo 650', '30049099', @B, 'D1', '2027-12-31', 15, 15, 12, 5000, 600, 300, 300, 5600)",
            ("@R", debit), ("@M", Dolo), ("@B", BatchDolo));

        await ExecAsync("INSERT INTO Expenses (ExpenseDate, Category, Description, AmountPaisa, PaymentMethod, PayeeName) VALUES ('2026-10-20', 0, 'October rent', 1000000, 0, 'Landlord')");
        await ExecAsync("INSERT OR IGNORE INTO FinanceExpenseCategories (Name) VALUES ('Electricity')");
        await ExecAsync(@"
            INSERT INTO Expenses (ExpenseDate, Category, CategoryId, AmountPaisa, PaymentMethod, TaxablePaisa, CgstPaisa, SgstPaisa, PayeeName)
            VALUES ('2026-10-21', 2, (SELECT Id FROM FinanceExpenseCategories WHERE Name = 'Electricity'), 118000, 1, 100000, 9000, 9000, 'BESCOM')");
        await ExecAsync("INSERT INTO Expenses (ExpenseDate, Category, AmountPaisa, PaymentMethod, IsCancelled) VALUES ('2026-10-22', 0, 777700, 0, 1)");

        foreach (var (date, type, method, amount, transaction, reference) in new (string, int, int, long, long, string)[]
                 {
                     ("2026-10-05", 0, 0, 11200, Sale1, "INV-1"), ("2026-10-06", 0, 1, 22400, Sale2, "INV-2"), ("2026-10-08", 0, 0, 11200, Sale4, "INV-4"),
                     ("2026-10-02", 2, 0, 26000, Purchase1, "PUR-1"), ("2026-10-03", 2, 3, 22400, Purchase2, "PUR-2"), ("2026-10-10", 3, 0, 1120, ret, "SR-1"),
                     ("2026-09-30", 0, 0, 5000, Sale1, "OLD")
                 })
            await ExecAsync(@"INSERT INTO Payments (PaymentDate, PaymentType, PaymentMethod, AmountPaisa, TransactionId, TransactionReference)
                              VALUES (@D, @T, @M, @A, @Tr, @R)", ("@D", date), ("@T", type), ("@M", method), ("@A", amount), ("@Tr", transaction), ("@R", reference));

        // Stock ledger: Codeine received on PUR-2 and supplied on INV-4; a Dolo sale after October (for "as on" valuation).
        await Movement(Codeine, "Codeine syrup", BatchCodeine, "C1", "2026-10-03 09:00:00", 0, 6, 0, $"Purchase:{Purchase2}");
        await Movement(Codeine, "Codeine syrup", BatchCodeine, "C1", "2026-10-08 13:00:00", 1, 0, 2, $"Sale:{Sale4}");
        await Movement(Dolo, "Dolo 650", BatchDolo, "D1", "2026-11-04 10:00:00", 1, 0, 30, "Sale:999");
    }

    private Task<long> Movement(long medicine, string name, long batch, string batchNo, string at, int type, long qtyIn, long qtyOut, string reference) =>
        ExecAsync(@"INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber, MovementType, QuantityIn, QuantityOut, Reference)
                    VALUES (@At, @M, @N, @B, @BNo, @T, @In, @Out, @Ref)",
            ("@At", at), ("@M", medicine), ("@N", name), ("@B", batch), ("@BNo", batchNo), ("@T", type), ("@In", qtyIn), ("@Out", qtyOut), ("@Ref", reference));
}
