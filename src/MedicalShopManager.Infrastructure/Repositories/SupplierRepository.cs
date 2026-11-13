using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class SupplierRepository : ISupplierRepository
{
    private readonly Database.DatabaseManager _db;
    private readonly ILogger<SupplierRepository> _logger;

    /// <summary>
    /// Supplier row plus amount owed: opening balance + unpaid balances of confirmed purchases − open debit notes
    /// (same expression as the Purchases module's DistributorRepository, so every screen shows one figure).
    /// </summary>
    private static readonly string SelectSql = $@"
        SELECT s.*, {Modules.Purchases.DistributorRepository.OutstandingSql} AS OutstandingBalancePaisa
        FROM Suppliers s";

    public SupplierRepository(Database.DatabaseManager db, ILogger<SupplierRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Supplier?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE s.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSupplier(reader) : null;
    }

    public async Task<IReadOnlyList<Supplier>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY s.Name";
        var list = new List<Supplier>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapSupplier(reader));
        return list;
    }

    public async Task<IReadOnlyList<Supplier>> SearchAsync(string query)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE s.Name LIKE @Q OR s.ContactPerson LIKE @Q OR s.Phone LIKE @Q OR s.Gstin LIKE @Q OR s.DrugLicenseNumber LIKE @Q ORDER BY s.Name";
        cmd.Parameters.AddWithValue("@Q", $"%{query}%");
        var list = new List<Supplier>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapSupplier(reader));
        return list;
    }

    public async Task<IReadOnlyList<Supplier>> GetActiveAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE s.IsActive = 1 ORDER BY s.Name";
        var list = new List<Supplier>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapSupplier(reader));
        return list;
    }

    public async Task<Supplier?> GetByGstinAsync(string gstin)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE s.Gstin = @Gstin";
        cmd.Parameters.AddWithValue("@Gstin", gstin);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSupplier(reader) : null;
    }

    public async Task<long> GetTotalOutstandingBalanceAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(SUM(OutstandingBalancePaisa), 0) FROM ({SelectSql} WHERE s.IsActive = 1)";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<long> InsertAsync(Supplier entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Suppliers (Name, ContactPerson, Phone, Email, Address, Gstin, DrugLicenseNumber, IsActive, OpeningBalancePaisa, CreatedAt, UpdatedAt)
            VALUES (@Name, @ContactPerson, @Phone, @Email, @Address, @Gstin, @DrugLicenseNumber, @IsActive, @OpeningBalancePaisa, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();";
        AddSupplierParams(cmd, entity);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(Supplier entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Suppliers SET Name=@Name, ContactPerson=@ContactPerson, Phone=@Phone, Email=@Email,
            Address=@Address, Gstin=@Gstin, DrugLicenseNumber=@DrugLicenseNumber, IsActive=@IsActive, OpeningBalancePaisa=@OpeningBalancePaisa, UpdatedAt=@UpdatedAt
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        AddSupplierParams(cmd, entity);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Suppliers SET IsActive = 0, UpdatedAt = @U WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@U", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Suppliers WHERE IsActive = 1";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static void AddSupplierParams(SqliteCommand cmd, Supplier s)
    {
        cmd.Parameters.AddWithValue("@Name", s.Name);
        cmd.Parameters.AddWithValue("@ContactPerson", (object?)s.ContactPerson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Phone", (object?)s.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Email", (object?)s.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Address", (object?)s.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Gstin", (object?)s.Gstin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DrugLicenseNumber", (object?)s.DrugLicenseNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsActive", s.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@OpeningBalancePaisa", s.OpeningBalancePaisa);
        cmd.Parameters.AddWithValue("@CreatedAt", s.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", s.UpdatedAt.ToString("o"));
    }

    private static Supplier MapSupplier(SqliteDataReader r)
    {
        return new Supplier
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            Name = r.GetString(r.GetOrdinal("Name")),
            ContactPerson = r.IsDBNull(r.GetOrdinal("ContactPerson")) ? null : r.GetString(r.GetOrdinal("ContactPerson")),
            Phone = r.IsDBNull(r.GetOrdinal("Phone")) ? null : r.GetString(r.GetOrdinal("Phone")),
            Email = r.IsDBNull(r.GetOrdinal("Email")) ? null : r.GetString(r.GetOrdinal("Email")),
            Address = r.IsDBNull(r.GetOrdinal("Address")) ? null : r.GetString(r.GetOrdinal("Address")),
            Gstin = r.IsDBNull(r.GetOrdinal("Gstin")) ? null : r.GetString(r.GetOrdinal("Gstin")),
            DrugLicenseNumber = r.IsDBNull(r.GetOrdinal("DrugLicenseNumber")) ? null : r.GetString(r.GetOrdinal("DrugLicenseNumber")),
            IsActive = r.GetInt32(r.GetOrdinal("IsActive")) == 1,
            OpeningBalancePaisa = r.GetInt64(r.GetOrdinal("OpeningBalancePaisa")),
            OutstandingBalancePaisa = r.GetInt64(r.GetOrdinal("OutstandingBalancePaisa")),
            CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("UpdatedAt")))
        };
    }
}
