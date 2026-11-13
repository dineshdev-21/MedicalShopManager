using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class PaymentRepository : IPaymentRepository
{
    private readonly Database.DatabaseManager _db;
    public PaymentRepository(Database.DatabaseManager db) => _db = db;

    public async Task<Payment?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapPayment(reader) : null;
    }

    public async Task<IReadOnlyList<Payment>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments ORDER BY PaymentDate DESC LIMIT 500";
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Payment>> GetByTransactionAsync(long transactionId, PaymentType type)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments WHERE TransactionId = @TxnId AND PaymentType = @Type ORDER BY PaymentDate";
        cmd.Parameters.AddWithValue("@TxnId", transactionId);
        cmd.Parameters.AddWithValue("@Type", (int)type);
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Payment>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments WHERE PaymentDate BETWEEN @From AND @To ORDER BY PaymentDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd 23:59:59"));
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Payment>> GetByMethodAsync(PaymentMethod method)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments WHERE PaymentMethod = @Method ORDER BY PaymentDate DESC LIMIT 500";
        cmd.Parameters.AddWithValue("@Method", (int)method);
        return await ReadList(cmd);
    }

    public async Task<long> InsertAsync(Payment entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod,
                AmountPaisa, ReferenceNumber, Notes, CreatedByUserId, CreatedAt)
            VALUES (@TransactionId, @TransactionReference, @PaymentDate, @PaymentType, @PaymentMethod,
                @AmountPaisa, @ReferenceNumber, @Notes, @CreatedByUserId, @CreatedAt);
            SELECT last_insert_rowid();";
        AddParams(cmd, entity);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public Task<bool> UpdateAsync(Payment entity) => throw new NotSupportedException("Payments are immutable.");
    public Task<bool> DeleteAsync(long id) => throw new NotSupportedException("Payments cannot be deleted.");

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Payments";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Payment>> ReadList(SqliteCommand cmd)
    {
        var list = new List<Payment>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapPayment(reader));
        return list;
    }

    private static void AddParams(SqliteCommand cmd, Payment p)
    {
        cmd.Parameters.AddWithValue("@TransactionId", p.TransactionId.HasValue ? p.TransactionId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@TransactionReference", (object?)p.TransactionReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PaymentDate", p.PaymentDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@PaymentType", (int)p.PaymentType);
        cmd.Parameters.AddWithValue("@PaymentMethod", (int)p.PaymentMethod);
        cmd.Parameters.AddWithValue("@AmountPaisa", p.AmountPaisa);
        cmd.Parameters.AddWithValue("@ReferenceNumber", (object?)p.ReferenceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)p.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", p.CreatedByUserId.HasValue ? p.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", p.CreatedAt.ToString("o"));
    }

    private static Payment MapPayment(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        TransactionId = r.IsDBNull(r.GetOrdinal("TransactionId")) ? null : r.GetInt64(r.GetOrdinal("TransactionId")),
        TransactionReference = r.IsDBNull(r.GetOrdinal("TransactionReference")) ? null : r.GetString(r.GetOrdinal("TransactionReference")),
        PaymentDate = DateTime.Parse(r.GetString(r.GetOrdinal("PaymentDate"))),
        PaymentType = (PaymentType)r.GetInt32(r.GetOrdinal("PaymentType")),
        PaymentMethod = (PaymentMethod)r.GetInt32(r.GetOrdinal("PaymentMethod")),
        AmountPaisa = r.GetInt64(r.GetOrdinal("AmountPaisa")),
        ReferenceNumber = r.IsDBNull(r.GetOrdinal("ReferenceNumber")) ? null : r.GetString(r.GetOrdinal("ReferenceNumber")),
        Notes = r.IsDBNull(r.GetOrdinal("Notes")) ? null : r.GetString(r.GetOrdinal("Notes")),
        CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")))
    };
}
