using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

/// <summary>
/// Pre-6.2 expense access (reports, Excel sync). Finance (migration 16) added numbering, categories, GST and
/// cancellation to the table: these readers skip cancelled expenses and inserts keep the v2 columns consistent.
/// New code uses <c>IFinanceExpenseRepository</c> / <c>IExpenseService</c>.
/// </summary>
public class ExpenseRepository : IExpenseRepository
{
    private readonly Database.DatabaseManager _db;
    public ExpenseRepository(Database.DatabaseManager db) => _db = db;

    public async Task<Expense?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Expenses WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapExpense(reader) : null;
    }

    public async Task<IReadOnlyList<Expense>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Expenses WHERE IsCancelled = 0 ORDER BY ExpenseDate DESC LIMIT 500";
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Expense>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Expenses WHERE IsCancelled = 0 AND ExpenseDate BETWEEN @From AND @To ORDER BY ExpenseDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd"));
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Expense>> GetByCategoryAsync(ExpenseCategory category)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Expenses WHERE IsCancelled = 0 AND Category = @Category ORDER BY ExpenseDate DESC";
        cmd.Parameters.AddWithValue("@Category", (int)category);
        return await ReadList(cmd);
    }

    public async Task<long> GetTotalByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(AmountPaisa), 0) FROM Expenses WHERE IsCancelled = 0 AND ExpenseDate BETWEEN @From AND @To";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<long> InsertAsync(Expense entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Expenses (ExpenseDate, Category, Description, AmountPaisa, PaymentMethod, ReferenceNumber, Notes, CreatedByUserId, CreatedAt,
                CategoryId, TaxablePaisa, Mode)
            VALUES (@ExpenseDate, @Category, @Description, @AmountPaisa, @PaymentMethod, @ReferenceNumber, @Notes, @CreatedByUserId, @CreatedAt,
                @Category + 1, @AmountPaisa, CASE @PaymentMethod WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 ELSE 1 END);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@ExpenseDate", entity.ExpenseDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Category", (int)entity.Category);
        cmd.Parameters.AddWithValue("@Description", (object?)entity.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AmountPaisa", entity.AmountPaisa);
        cmd.Parameters.AddWithValue("@PaymentMethod", (int)entity.PaymentMethod);
        cmd.Parameters.AddWithValue("@ReferenceNumber", (object?)entity.ReferenceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)entity.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", entity.CreatedByUserId.HasValue ? entity.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(Expense entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE Expenses SET ExpenseDate=@ExpenseDate, Category=@Category, Description=@Description,
            AmountPaisa=@AmountPaisa, PaymentMethod=@PaymentMethod, ReferenceNumber=@ReferenceNumber, Notes=@Notes,
            CategoryId=CASE WHEN CategoryId IS NULL OR CategoryId BETWEEN 1 AND 16 THEN @Category + 1 ELSE CategoryId END,
            TaxablePaisa=@AmountPaisa - CgstPaisa - SgstPaisa - IgstPaisa
            WHERE Id=@Id AND IsCancelled = 0";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        cmd.Parameters.AddWithValue("@ExpenseDate", entity.ExpenseDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Category", (int)entity.Category);
        cmd.Parameters.AddWithValue("@Description", (object?)entity.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AmountPaisa", entity.AmountPaisa);
        cmd.Parameters.AddWithValue("@PaymentMethod", (int)entity.PaymentMethod);
        cmd.Parameters.AddWithValue("@ReferenceNumber", (object?)entity.ReferenceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)entity.Notes ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Expenses WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Expenses";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Expense>> ReadList(SqliteCommand cmd)
    {
        var list = new List<Expense>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapExpense(reader));
        return list;
    }

    private static Expense MapExpense(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        ExpenseDate = DateTime.Parse(r.GetString(r.GetOrdinal("ExpenseDate"))),
        Category = (ExpenseCategory)r.GetInt32(r.GetOrdinal("Category")),
        Description = r.IsDBNull(r.GetOrdinal("Description")) ? null : r.GetString(r.GetOrdinal("Description")),
        AmountPaisa = r.GetInt64(r.GetOrdinal("AmountPaisa")),
        PaymentMethod = (PaymentMethod)r.GetInt32(r.GetOrdinal("PaymentMethod")),
        ReferenceNumber = r.IsDBNull(r.GetOrdinal("ReferenceNumber")) ? null : r.GetString(r.GetOrdinal("ReferenceNumber")),
        Notes = r.IsDBNull(r.GetOrdinal("Notes")) ? null : r.GetString(r.GetOrdinal("Notes")),
        CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")))
    };
}
