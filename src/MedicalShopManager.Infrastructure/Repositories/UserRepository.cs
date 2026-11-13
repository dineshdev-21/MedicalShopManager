using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly Database.DatabaseManager _db;
    public UserRepository(Database.DatabaseManager db) => _db = db;

    public async Task<User?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapUser(reader) : null;
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users WHERE Username = @Username COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@Username", username);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapUser(reader) : null;
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users ORDER BY DisplayName";
        var list = new List<User>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapUser(reader));
        return list;
    }

    public async Task<IReadOnlyList<User>> GetByRoleAsync(UserRole role)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users WHERE Role = @Role ORDER BY DisplayName";
        cmd.Parameters.AddWithValue("@Role", (int)role);
        var list = new List<User>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapUser(reader));
        return list;
    }

    public async Task<long> InsertAsync(User entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Users (Username, DisplayName, PasswordHash, Role, IsActive, CreatedAt, UpdatedAt)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role, @IsActive, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@Username", entity.Username);
        cmd.Parameters.AddWithValue("@DisplayName", entity.DisplayName);
        cmd.Parameters.AddWithValue("@PasswordHash", entity.PasswordHash);
        cmd.Parameters.AddWithValue("@Role", (int)entity.Role);
        cmd.Parameters.AddWithValue("@IsActive", entity.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", entity.UpdatedAt.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(User entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE Users SET DisplayName=@DisplayName, Role=@Role, IsActive=@IsActive, UpdatedAt=@UpdatedAt WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        cmd.Parameters.AddWithValue("@DisplayName", entity.DisplayName);
        cmd.Parameters.AddWithValue("@Role", (int)entity.Role);
        cmd.Parameters.AddWithValue("@IsActive", entity.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@UpdatedAt", entity.UpdatedAt.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdatePasswordAsync(long userId, string newPasswordHash)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET PasswordHash=@Hash, UpdatedAt=@U WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", userId);
        cmd.Parameters.AddWithValue("@Hash", newPasswordHash);
        cmd.Parameters.AddWithValue("@U", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateLastLoginAsync(long userId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET LastLoginAt=@L WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", userId);
        cmd.Parameters.AddWithValue("@L", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET IsActive=0, UpdatedAt=@U WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@U", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users WHERE IsActive = 1";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static User MapUser(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Username = r.GetString(r.GetOrdinal("Username")),
        DisplayName = r.GetString(r.GetOrdinal("DisplayName")),
        PasswordHash = r.GetString(r.GetOrdinal("PasswordHash")),
        Role = (UserRole)r.GetInt32(r.GetOrdinal("Role")),
        IsActive = r.GetInt32(r.GetOrdinal("IsActive")) == 1,
        LastLoginAt = r.IsDBNull(r.GetOrdinal("LastLoginAt")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("LastLoginAt"))),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("UpdatedAt")))
    };
}
