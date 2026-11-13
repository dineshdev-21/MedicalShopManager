using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly Database.DatabaseManager _db;

    /// <summary>Customer row plus unpaid credit-sale dues.</summary>
    private const string SelectSql = @"
        SELECT c.*, COALESCE((SELECT SUM(sa.BalanceDuePaisa) FROM Sales sa WHERE sa.CustomerId = c.Id), 0) AS OutstandingBalancePaisa
        FROM Customers c";

    public CustomerRepository(Database.DatabaseManager db) => _db = db;

    public async Task<Customer?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE c.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapCustomer(reader) : null;
    }

    public async Task<IReadOnlyList<Customer>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY c.Name";
        var list = new List<Customer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapCustomer(reader));
        return list;
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(string query)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE c.Name LIKE @Q OR c.Phone LIKE @Q ORDER BY c.Name LIMIT 50";
        cmd.Parameters.AddWithValue("@Q", $"%{query}%");
        var list = new List<Customer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapCustomer(reader));
        return list;
    }

    public async Task<Customer?> GetByPhoneAsync(string phone)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE c.Phone = @Phone";
        cmd.Parameters.AddWithValue("@Phone", phone);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapCustomer(reader) : null;
    }

    public async Task<long> InsertAsync(Customer entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Customers (Name, Phone, Email, Address, DoctorName, IsActive, CreatedAt, UpdatedAt) 
            VALUES (@Name, @Phone, @Email, @Address, @DoctorName, @IsActive, @CreatedAt, @UpdatedAt); SELECT last_insert_rowid();";
        AddCustomerParams(cmd, entity);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(Customer entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE Customers SET Name=@Name, Phone=@Phone, Email=@Email, Address=@Address,
            DoctorName=@DoctorName, IsActive=@IsActive, UpdatedAt=@UpdatedAt WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        AddCustomerParams(cmd, entity);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Customers WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Customers";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static void AddCustomerParams(SqliteCommand cmd, Customer c)
    {
        cmd.Parameters.AddWithValue("@Name", c.Name);
        cmd.Parameters.AddWithValue("@Phone", (object?)c.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Email", (object?)c.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Address", (object?)c.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DoctorName", (object?)c.DoctorName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsActive", c.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@UpdatedAt", c.UpdatedAt.ToString("o"));
    }

    private static Customer MapCustomer(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Name = r.GetString(r.GetOrdinal("Name")),
        Phone = r.IsDBNull(r.GetOrdinal("Phone")) ? null : r.GetString(r.GetOrdinal("Phone")),
        Email = r.IsDBNull(r.GetOrdinal("Email")) ? null : r.GetString(r.GetOrdinal("Email")),
        Address = r.IsDBNull(r.GetOrdinal("Address")) ? null : r.GetString(r.GetOrdinal("Address")),
        DoctorName = r.IsDBNull(r.GetOrdinal("DoctorName")) ? null : r.GetString(r.GetOrdinal("DoctorName")),
        IsActive = r.GetInt32(r.GetOrdinal("IsActive")) == 1,
        OutstandingBalancePaisa = r.GetInt64(r.GetOrdinal("OutstandingBalancePaisa")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("UpdatedAt")))
    };
}
