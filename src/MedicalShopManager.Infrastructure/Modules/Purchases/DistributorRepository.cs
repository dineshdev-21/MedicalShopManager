using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Database;
using static MedicalShopManager.Infrastructure.Modules.Purchases.PurchaseSql;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Distributor master over the Suppliers table. Balances are derived from source rows on every read:
/// opening + unpaid balances of confirmed purchases − open debit notes (net of refunds received).
/// </summary>
public sealed class DistributorRepository : IDistributorRepository
{
    private readonly DatabaseManager _db;

    /// <summary>SQL expression for the amount owed to supplier <c>s</c>; shared with the statement and SupplierRepository.</summary>
    internal static readonly string OutstandingSql = $@"
        s.OpeningBalancePaisa
        + COALESCE((SELECT SUM(p.BalancePaisa) FROM Purchases p
                    WHERE p.SupplierId = s.Id AND p.Status = {(int)PurchaseStatus.Confirmed}), 0)
        - COALESCE((SELECT SUM(r.NetAmountPaisa - r.RefundReceivedPaisa) FROM PurchaseReturns r
                    WHERE r.SupplierId = s.Id AND r.IsCancelled = 0), 0)";

    private static readonly string SelectSql = $@"
        SELECT s.*,
            {OutstandingSql} AS OutstandingBalancePaisa,
            (SELECT MAX(p.PurchaseDate) FROM Purchases p WHERE p.SupplierId = s.Id AND p.Status = {(int)PurchaseStatus.Confirmed}) AS LastPurchaseDate,
            COALESCE((SELECT SUM(p.BalancePaisa) FROM Purchases p WHERE p.SupplierId = s.Id AND p.Status = {(int)PurchaseStatus.Confirmed}
                      AND p.BalancePaisa > 0 AND p.DueDate IS NOT NULL AND p.DueDate < @Today), 0) AS OverduePaisa
        FROM Suppliers s";

    public DistributorRepository(DatabaseManager db) => _db = db;

    public async Task<Distributor?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, SelectSql + " WHERE s.Id = @Id", ("@Id", id), ("@Today", Date(DateTime.Today)));
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<Distributor>> SearchAsync(string? query, bool includeInactive = false)
    {
        var where = new List<string>();
        var parameters = new List<(string, object?)> { ("@Today", Date(DateTime.Today)) };
        if (!includeInactive) where.Add("s.IsActive = 1");
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add(@"(s.Name LIKE @Q ESCAPE '\' OR s.ContactPerson LIKE @Q ESCAPE '\' OR s.Phone LIKE @Q ESCAPE '\'
                        OR s.Gstin LIKE @Q ESCAPE '\' OR s.DrugLicenseNumber LIKE @Q ESCAPE '\')");
            parameters.Add(("@Q", Like(query)));
        }
        var sql = SelectSql + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY s.Name COLLATE NOCASE";

        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, sql, parameters.ToArray());
        var list = new List<Distributor>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(Map(reader));
        return list;
    }

    public async Task<long> InsertAsync(Distributor d)
    {
        using var conn = _db.CreateNewConnection();
        var now = DateTime.UtcNow;
        var id = await ScalarAsync(conn, null, @"
            INSERT INTO Suppliers (Name, ContactPerson, Phone, Email, Address, StateCode, Pincode, Gstin, DrugLicenseNumber,
                CreditDays, IsActive, OpeningBalancePaisa, CreatedAt, UpdatedAt)
            VALUES (@Name, @ContactPerson, @Phone, @Email, @Address, @StateCode, @Pincode, @Gstin, @DrugLicenseNumber,
                @CreditDays, @IsActive, @OpeningBalancePaisa, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();", Params(d, now, now));
        return Convert.ToInt64(id);
    }

    public async Task<bool> UpdateAsync(Distributor d)
    {
        using var conn = _db.CreateNewConnection();
        var parameters = Params(d, d.CreatedAt, DateTime.UtcNow).Append(("@Id", d.Id)).ToArray();
        return await ExecAsync(conn, null, @"
            UPDATE Suppliers SET Name = @Name, ContactPerson = @ContactPerson, Phone = @Phone, Email = @Email,
                Address = @Address, StateCode = @StateCode, Pincode = @Pincode, Gstin = @Gstin,
                DrugLicenseNumber = @DrugLicenseNumber, CreditDays = @CreditDays, IsActive = @IsActive,
                OpeningBalancePaisa = @OpeningBalancePaisa, UpdatedAt = @UpdatedAt
            WHERE Id = @Id", parameters) > 0;
    }

    public async Task<bool> SetActiveAsync(long id, bool isActive)
    {
        using var conn = _db.CreateNewConnection();
        return await ExecAsync(conn, null, "UPDATE Suppliers SET IsActive = @A, UpdatedAt = @U WHERE Id = @Id",
            ("@A", isActive ? 1 : 0), ("@U", Stamp(DateTime.UtcNow)), ("@Id", id)) > 0;
    }

    public async Task<bool> GstinExistsAsync(string gstin, long excludeId = 0)
    {
        using var conn = _db.CreateNewConnection();
        var count = await ScalarAsync(conn, null,
            "SELECT COUNT(*) FROM Suppliers WHERE UPPER(TRIM(Gstin)) = UPPER(TRIM(@G)) AND Id <> @Id",
            ("@G", gstin), ("@Id", excludeId));
        return Convert.ToInt64(count) > 0;
    }

    public async Task<IReadOnlyList<SupplierLedgerEntry>> GetLedgerEntriesAsync(long supplierId)
    {
        using var conn = _db.CreateNewConnection();
        var entries = new List<SupplierLedgerEntry>();

        // Purchases (credit) and payments recorded at bill time without a Payments row (pre-6.2 drafts).
        using (var cmd = Command(conn, null, $@"
            SELECT p.Id, p.PurchaseNumber, p.SupplierInvoiceNumber, p.PurchaseDate, p.GrandTotalPaisa, p.AmountPaidPaisa,
                COALESCE((SELECT SUM(pay.AmountPaisa) FROM Payments pay WHERE pay.TransactionId = p.Id
                          AND pay.PaymentType IN ({(int)PaymentType.SupplierPayment}, {(int)PaymentType.PurchasePayment})), 0) AS RecordedPaisa
            FROM Purchases p WHERE p.SupplierId = @S AND p.Status = {(int)PurchaseStatus.Confirmed}", ("@S", supplierId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var number = r.StrOrNull("PurchaseNumber") ?? $"#{r.Long("Id")}";
                var date = r.DateValue("PurchaseDate");
                entries.Add(new SupplierLedgerEntry(date, SupplierStatementEntryType.Purchase, number,
                    $"Purchase bill {r.Str("SupplierInvoiceNumber")}", 0, r.Long("GrandTotalPaisa"), 1));
                var unrecorded = r.Long("AmountPaidPaisa") - r.Long("RecordedPaisa");
                if (unrecorded > 0)
                    entries.Add(new SupplierLedgerEntry(date, SupplierStatementEntryType.Payment, number, "Paid with bill", unrecorded, 0, 2));
            }
        }

        using (var cmd = Command(conn, null, $@"
            SELECT pay.PaymentDate, pay.AmountPaisa, pay.PaymentMethod, pay.ReferenceNumber, p.PurchaseNumber, p.SupplierInvoiceNumber, p.Id
            FROM Payments pay JOIN Purchases p ON p.Id = pay.TransactionId
            WHERE p.SupplierId = @S AND p.Status = {(int)PurchaseStatus.Confirmed}
              AND pay.PaymentType IN ({(int)PaymentType.SupplierPayment}, {(int)PaymentType.PurchasePayment})", ("@S", supplierId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var method = (PaymentMethod)r.Int("PaymentMethod");
                var reference = r.StrOrNull("ReferenceNumber");
                entries.Add(new SupplierLedgerEntry(r.DateValue("PaymentDate"), SupplierStatementEntryType.Payment,
                    r.StrOrNull("PurchaseNumber") ?? $"#{r.Long("Id")}",
                    $"Payment ({method}) for bill {r.Str("SupplierInvoiceNumber")}" + (reference is null ? "" : $" Ref {reference}"),
                    r.Long("AmountPaisa"), 0, 2));
            }
        }

        using (var cmd = Command(conn, null, @"
            SELECT ReturnNumber, ReturnDate, Kind, NetAmountPaisa, RefundReceivedPaisa FROM PurchaseReturns
            WHERE SupplierId = @S AND IsCancelled = 0", ("@S", supplierId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var number = r.Str("ReturnNumber");
                var date = r.DateValue("ReturnDate");
                entries.Add(new SupplierLedgerEntry(date, SupplierStatementEntryType.DebitNote, number,
                    ((PurchaseReturnKind)r.Int("Kind")).ToLabel(), r.Long("NetAmountPaisa"), 0, 3));
                if (r.Long("RefundReceivedPaisa") > 0)
                    entries.Add(new SupplierLedgerEntry(date, SupplierStatementEntryType.RefundReceived, number,
                        "Refund received against debit note", 0, r.Long("RefundReceivedPaisa"), 4));
            }
        }

        return entries;
    }

    private static (string, object?)[] Params(Distributor d, DateTime createdAt, DateTime updatedAt) => new (string, object?)[]
    {
        ("@Name", d.Name.Trim()),
        ("@ContactPerson", OrNull(d.ContactPerson)),
        ("@Phone", OrNull(d.Phone)),
        ("@Email", OrNull(d.Email)),
        ("@Address", OrNull(d.Address)),
        ("@StateCode", OrNull(d.StateCode)),
        ("@Pincode", OrNull(d.Pincode)),
        ("@Gstin", string.IsNullOrWhiteSpace(d.Gstin) ? DBNull.Value : d.Gstin.Trim().ToUpperInvariant()),
        ("@DrugLicenseNumber", OrNull(d.DrugLicenseNumber)),
        ("@CreditDays", d.CreditDays),
        ("@IsActive", d.IsActive ? 1 : 0),
        ("@OpeningBalancePaisa", d.OpeningBalancePaisa),
        ("@CreatedAt", Stamp(createdAt)),
        ("@UpdatedAt", Stamp(updatedAt))
    };

    private static Distributor Map(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        Name = r.Str("Name"),
        ContactPerson = r.StrOrNull("ContactPerson"),
        Phone = r.StrOrNull("Phone"),
        Email = r.StrOrNull("Email"),
        Address = r.StrOrNull("Address"),
        StateCode = r.StrOrNull("StateCode"),
        Pincode = r.StrOrNull("Pincode"),
        Gstin = r.StrOrNull("Gstin"),
        DrugLicenseNumber = r.StrOrNull("DrugLicenseNumber"),
        CreditDays = r.Int("CreditDays"),
        IsActive = r.Bool("IsActive"),
        OpeningBalancePaisa = r.Long("OpeningBalancePaisa"),
        OutstandingBalancePaisa = r.Long("OutstandingBalancePaisa"),
        LastPurchaseDate = r.DateOrNullValue("LastPurchaseDate"),
        OverduePaisa = r.Long("OverduePaisa"),
        CreatedAt = r.DateValue("CreatedAt"),
        UpdatedAt = r.DateValue("UpdatedAt")
    };
}
