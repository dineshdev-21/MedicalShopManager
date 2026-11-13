using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Customers with the Billing (migration 10) fields. Balances are derived in SQL from source rows: opening balance
/// + unpaid balance of active sales (lesson 7). Customers are never hard-deleted (ledgers point at them).
/// </summary>
public sealed class BillingCustomerRepository : IBillingCustomerRepository
{
    private const string SelectWithMetrics = @"
        SELECT c.*,
            c.OpeningBalancePaisa + COALESCE((SELECT SUM(s.BalanceDuePaisa) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1), 0) AS OutstandingPaisa,
            (SELECT COUNT(*) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS InvoiceCount,
            (SELECT MAX(s.InvoiceDate) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS LastSaleDate
        FROM Customers c";

    private readonly DatabaseManager _db;

    public BillingCustomerRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<BillingCustomer>> SearchAsync(string? text, int limit = 25)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectWithMetrics + @"
            WHERE c.IsActive = 1 AND (@Like IS NULL
                OR c.Name LIKE @Like ESCAPE '\' OR c.Phone LIKE @Like ESCAPE '\' OR c.Gstin LIKE @Like ESCAPE '\')
            ORDER BY CASE WHEN @Prefix IS NOT NULL AND c.Name LIKE @Prefix ESCAPE '\' THEN 0 ELSE 1 END, c.Name COLLATE NOCASE
            LIMIT @Limit");
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        cmd.Add("@Like", trimmed is null ? null : BillingSql.Like(trimmed));
        cmd.Add("@Prefix", trimmed is null ? null : BillingSql.Like(trimmed)[1..]);
        cmd.Add("@Limit", Math.Clamp(limit, 1, 500));
        return await ReadListAsync(cmd);
    }

    public async Task<BillingCustomer?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectWithMetrics + " WHERE c.Id = @Id");
        cmd.Add("@Id", id);
        return (await ReadListAsync(cmd)).FirstOrDefault();
    }

    public async Task<BillingCustomer?> FindByPhoneAsync(string phone)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectWithMetrics + " WHERE c.Phone = @Phone AND c.IsActive = 1 ORDER BY c.Id LIMIT 1");
        cmd.Add("@Phone", phone.Trim());
        return (await ReadListAsync(cmd)).FirstOrDefault();
    }

    public async Task<long> InsertAsync(BillingCustomer customer)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            INSERT INTO Customers (Name, Phone, Email, Address, DoctorName, IsActive, CreatedAt, UpdatedAt,
                CustomerType, Gender, DateOfBirth, Gstin, Pan, IsCompositionDealer, DefaultDiscountPercent, ReferenceNo,
                OpeningBalancePaisa, OpeningBalanceDate, CreditDays)
            VALUES (@Name, @Phone, @Email, @Address, @DoctorName, @IsActive, @Now, @Now,
                @CustomerType, @Gender, @DateOfBirth, @Gstin, @Pan, @IsCompositionDealer, @DefaultDiscountPercent, @ReferenceNo,
                @OpeningBalancePaisa, @OpeningBalanceDate, @CreditDays);
            SELECT last_insert_rowid();");
        AddParameters(cmd, customer);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(BillingCustomer customer)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            UPDATE Customers SET Name = @Name, Phone = @Phone, Email = @Email, Address = @Address, DoctorName = @DoctorName,
                IsActive = @IsActive, UpdatedAt = @Now, CustomerType = @CustomerType, Gender = @Gender, DateOfBirth = @DateOfBirth,
                Gstin = @Gstin, Pan = @Pan, IsCompositionDealer = @IsCompositionDealer,
                DefaultDiscountPercent = @DefaultDiscountPercent, ReferenceNo = @ReferenceNo,
                OpeningBalancePaisa = @OpeningBalancePaisa, OpeningBalanceDate = @OpeningBalanceDate, CreditDays = @CreditDays
            WHERE Id = @Id");
        AddParameters(cmd, customer);
        cmd.Add("@Id", customer.Id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeactivateAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE Customers SET IsActive = 0, UpdatedAt = @Now WHERE Id = @Id");
        cmd.Add("@Id", id);
        cmd.AddDateTime("@Now", DateTime.UtcNow);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static void AddParameters(SqliteCommand cmd, BillingCustomer c)
    {
        cmd.Add("@Name", c.Name);
        cmd.Add("@Phone", c.Phone);
        cmd.Add("@Email", c.Email);
        cmd.Add("@Address", c.Address);
        cmd.Add("@DoctorName", c.DoctorName);
        cmd.Add("@IsActive", c.IsActive ? 1 : 0);
        cmd.AddDateTime("@Now", DateTime.UtcNow);
        cmd.Add("@CustomerType", (int)c.Kind);
        cmd.Add("@Gender", (int)c.Gender);
        cmd.AddDate("@DateOfBirth", c.DateOfBirth);
        cmd.Add("@Gstin", c.Gstin);
        cmd.Add("@Pan", c.Pan);
        cmd.Add("@IsCompositionDealer", c.IsCompositionDealer ? 1 : 0);
        cmd.Add("@DefaultDiscountPercent", c.DefaultDiscountPercent);
        cmd.Add("@ReferenceNo", c.ReferenceNo);
        cmd.Add("@OpeningBalancePaisa", c.OpeningBalancePaisa);
        cmd.AddDate("@OpeningBalanceDate", c.OpeningBalanceDate);
        cmd.Add("@CreditDays", c.CreditDays);
    }

    private static async Task<IReadOnlyList<BillingCustomer>> ReadListAsync(SqliteCommand cmd)
    {
        var list = new List<BillingCustomer>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new BillingCustomer
            {
                Id = r.Long("Id"),
                Kind = (CustomerKind)r.Int("CustomerType", (int)CustomerKind.Individual),
                Name = r.Str("Name") ?? string.Empty,
                Phone = r.Str("Phone"),
                Email = r.Str("Email"),
                Address = r.Str("Address"),
                DoctorName = r.Str("DoctorName"),
                Gender = (PersonGender)r.Int("Gender"),
                DateOfBirth = r.DateOrNull("DateOfBirth"),
                Gstin = r.Str("Gstin"),
                Pan = r.Str("Pan"),
                IsCompositionDealer = r.Bool("IsCompositionDealer"),
                DefaultDiscountPercent = r.Dec("DefaultDiscountPercent"),
                ReferenceNo = r.Str("ReferenceNo"),
                OpeningBalancePaisa = r.Long("OpeningBalancePaisa"),
                OpeningBalanceDate = r.DateOrNull("OpeningBalanceDate"),
                CreditDays = r.Int("CreditDays"),
                IsActive = r.Bool("IsActive", true),
                CreatedAt = r.Date("CreatedAt"),
                UpdatedAt = r.Date("UpdatedAt"),
                OutstandingBalancePaisa = r.Long("OutstandingPaisa"),
                InvoiceCount = r.Int("InvoiceCount"),
                LastSaleDate = r.DateOrNull("LastSaleDate")
            });
        }
        return list;
    }
}
