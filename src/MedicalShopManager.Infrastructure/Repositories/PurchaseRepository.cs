using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class PurchaseRepository : IPurchaseRepository
{
    private readonly Database.DatabaseManager _db;
    private readonly ILogger<PurchaseRepository> _logger;

    public PurchaseRepository(Database.DatabaseManager db, ILogger<PurchaseRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Purchase?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Purchases WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapPurchase(reader) : null;
    }

    public async Task<Purchase?> GetWithItemsAsync(long id)
    {
        var purchase = await GetByIdAsync(id);
        if (purchase == null) return null;

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM PurchaseItems WHERE PurchaseId = @PurchaseId ORDER BY Id";
        cmd.Parameters.AddWithValue("@PurchaseId", id);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            purchase.Items.Add(MapPurchaseItem(reader));
        return purchase;
    }

    public async Task<IReadOnlyList<Purchase>> GetBySupplierAsync(long supplierId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Purchases WHERE SupplierId = @SupplierId ORDER BY PurchaseDate DESC";
        cmd.Parameters.AddWithValue("@SupplierId", supplierId);
        return await ReadPurchaseList(cmd);
    }

    public async Task<IReadOnlyList<Purchase>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Purchases WHERE PurchaseDate BETWEEN @From AND @To ORDER BY PurchaseDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd"));
        return await ReadPurchaseList(cmd);
    }

    public async Task<IReadOnlyList<Purchase>> GetByStatusAsync(PurchaseStatus status)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Purchases WHERE Status = @Status ORDER BY PurchaseDate DESC";
        cmd.Parameters.AddWithValue("@Status", (int)status);
        return await ReadPurchaseList(cmd);
    }

    public async Task<IReadOnlyList<Purchase>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Purchases ORDER BY PurchaseDate DESC LIMIT 500";
        return await ReadPurchaseList(cmd);
    }

    public async Task<long> InsertAsync(Purchase entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate,
                SubtotalPaisa, DiscountAmountPaisa, TaxAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa,
                Status, PaymentStatus, Notes, CreatedByUserId, CreatedAt, UpdatedAt)
            VALUES (@SupplierInvoiceNumber, @SupplierId, @SupplierName, @PurchaseDate,
                @SubtotalPaisa, @DiscountAmountPaisa, @TaxAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalancePaisa,
                @Status, @PaymentStatus, @Notes, @CreatedByUserId, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();";
        AddPurchaseParams(cmd, entity);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<long> InsertWithItemsAsync(Purchase purchase)
    {
        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            using var purchCmd = conn.CreateCommand();
            purchCmd.Transaction = transaction;
            purchCmd.CommandText = @"
                INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate,
                    SubtotalPaisa, DiscountAmountPaisa, TaxAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa,
                    Status, PaymentStatus, Notes, CreatedByUserId, CreatedAt, UpdatedAt)
                VALUES (@SupplierInvoiceNumber, @SupplierId, @SupplierName, @PurchaseDate,
                    @SubtotalPaisa, @DiscountAmountPaisa, @TaxAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalancePaisa,
                    @Status, @PaymentStatus, @Notes, @CreatedByUserId, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();";
            AddPurchaseParams(purchCmd, purchase);
            var purchaseId = Convert.ToInt64(await purchCmd.ExecuteScalarAsync());

            foreach (var item in purchase.Items)
            {
                item.PurchaseId = purchaseId;
                using var itemCmd = conn.CreateCommand();
                itemCmd.Transaction = transaction;
                itemCmd.CommandText = @"
                    INSERT INTO PurchaseItems (PurchaseId, MedicineId, MedicineName, BatchNumber, ExpiryDate,
                        ManufacturingDate, Quantity, FreeQuantity, PurchasePricePaisa, MrpPaisa, SellingPricePaisa,
                        GstRatePercent, GstAmountPaisa, DiscountPercent, DiscountAmountPaisa, LineTotalPaisa)
                    VALUES (@PurchaseId, @MedicineId, @MedicineName, @BatchNumber, @ExpiryDate,
                        @ManufacturingDate, @Quantity, @FreeQuantity, @PurchasePricePaisa, @MrpPaisa, @SellingPricePaisa,
                        @GstRatePercent, @GstAmountPaisa, @DiscountPercent, @DiscountAmountPaisa, @LineTotalPaisa)";
                AddPurchaseItemParams(itemCmd, item);
                await itemCmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
            return purchaseId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<bool> UpdateAsync(Purchase entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Purchases SET SupplierInvoiceNumber=@SupplierInvoiceNumber, SupplierId=@SupplierId,
                SupplierName=@SupplierName, PurchaseDate=@PurchaseDate, SubtotalPaisa=@SubtotalPaisa,
                DiscountAmountPaisa=@DiscountAmountPaisa, TaxAmountPaisa=@TaxAmountPaisa,
                GrandTotalPaisa=@GrandTotalPaisa, AmountPaidPaisa=@AmountPaidPaisa, BalancePaisa=@BalancePaisa,
                Status=@Status, PaymentStatus=@PaymentStatus, Notes=@Notes, UpdatedAt=@UpdatedAt
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        AddPurchaseParams(cmd, entity);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateStatusAsync(long purchaseId, PurchaseStatus status)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Purchases SET Status = @Status, UpdatedAt = @UpdatedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", purchaseId);
        cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id) =>
        throw new NotSupportedException("Purchases cannot be deleted. Use returns instead.");

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Purchases";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Purchase>> ReadPurchaseList(SqliteCommand cmd)
    {
        var list = new List<Purchase>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapPurchase(reader));
        return list;
    }

    private static void AddPurchaseParams(SqliteCommand cmd, Purchase p)
    {
        cmd.Parameters.AddWithValue("@SupplierInvoiceNumber", p.SupplierInvoiceNumber);
        cmd.Parameters.AddWithValue("@SupplierId", p.SupplierId);
        cmd.Parameters.AddWithValue("@SupplierName", p.SupplierName);
        cmd.Parameters.AddWithValue("@PurchaseDate", p.PurchaseDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@SubtotalPaisa", p.SubtotalPaisa);
        cmd.Parameters.AddWithValue("@DiscountAmountPaisa", p.DiscountAmountPaisa);
        cmd.Parameters.AddWithValue("@TaxAmountPaisa", p.TaxAmountPaisa);
        cmd.Parameters.AddWithValue("@GrandTotalPaisa", p.GrandTotalPaisa);
        cmd.Parameters.AddWithValue("@AmountPaidPaisa", p.AmountPaidPaisa);
        cmd.Parameters.AddWithValue("@BalancePaisa", p.BalancePaisa);
        cmd.Parameters.AddWithValue("@Status", (int)p.Status);
        cmd.Parameters.AddWithValue("@PaymentStatus", (int)p.PaymentStatus);
        cmd.Parameters.AddWithValue("@Notes", (object?)p.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", p.CreatedByUserId.HasValue ? p.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", p.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", p.UpdatedAt.ToString("o"));
    }

    internal static void AddPurchaseItemParams(SqliteCommand cmd, PurchaseItem pi)
    {
        cmd.Parameters.AddWithValue("@PurchaseId", pi.PurchaseId);
        cmd.Parameters.AddWithValue("@MedicineId", pi.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", pi.MedicineName);
        cmd.Parameters.AddWithValue("@BatchNumber", pi.BatchNumber);
        cmd.Parameters.AddWithValue("@ExpiryDate", pi.ExpiryDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@ManufacturingDate", pi.ManufacturingDate.HasValue ? pi.ManufacturingDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("@Quantity", pi.Quantity);
        cmd.Parameters.AddWithValue("@FreeQuantity", pi.FreeQuantity);
        cmd.Parameters.AddWithValue("@PurchasePricePaisa", pi.PurchasePricePaisa);
        cmd.Parameters.AddWithValue("@MrpPaisa", pi.MrpPaisa);
        cmd.Parameters.AddWithValue("@SellingPricePaisa", pi.SellingPricePaisa);
        cmd.Parameters.AddWithValue("@GstRatePercent", pi.GstRatePercent);
        cmd.Parameters.AddWithValue("@GstAmountPaisa", pi.GstAmountPaisa);
        cmd.Parameters.AddWithValue("@DiscountPercent", pi.DiscountPercent);
        cmd.Parameters.AddWithValue("@DiscountAmountPaisa", pi.DiscountAmountPaisa);
        cmd.Parameters.AddWithValue("@LineTotalPaisa", pi.LineTotalPaisa);
    }

    private static Purchase MapPurchase(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SupplierInvoiceNumber = r.GetString(r.GetOrdinal("SupplierInvoiceNumber")),
        SupplierId = r.GetInt64(r.GetOrdinal("SupplierId")),
        SupplierName = r.GetString(r.GetOrdinal("SupplierName")),
        PurchaseDate = DateTime.Parse(r.GetString(r.GetOrdinal("PurchaseDate"))),
        SubtotalPaisa = r.GetInt64(r.GetOrdinal("SubtotalPaisa")),
        DiscountAmountPaisa = r.GetInt64(r.GetOrdinal("DiscountAmountPaisa")),
        TaxAmountPaisa = r.GetInt64(r.GetOrdinal("TaxAmountPaisa")),
        GrandTotalPaisa = r.GetInt64(r.GetOrdinal("GrandTotalPaisa")),
        AmountPaidPaisa = r.GetInt64(r.GetOrdinal("AmountPaidPaisa")),
        BalancePaisa = r.GetInt64(r.GetOrdinal("BalancePaisa")),
        Status = (PurchaseStatus)r.GetInt32(r.GetOrdinal("Status")),
        PaymentStatus = (PaymentStatus)r.GetInt32(r.GetOrdinal("PaymentStatus")),
        Notes = r.IsDBNull(r.GetOrdinal("Notes")) ? null : r.GetString(r.GetOrdinal("Notes")),
        CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("UpdatedAt")))
    };

    internal static PurchaseItem MapPurchaseItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        PurchaseId = r.GetInt64(r.GetOrdinal("PurchaseId")),
        MedicineId = r.GetInt64(r.GetOrdinal("MedicineId")),
        MedicineName = r.GetString(r.GetOrdinal("MedicineName")),
        BatchNumber = r.GetString(r.GetOrdinal("BatchNumber")),
        ExpiryDate = DateTime.Parse(r.GetString(r.GetOrdinal("ExpiryDate"))),
        ManufacturingDate = r.IsDBNull(r.GetOrdinal("ManufacturingDate")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("ManufacturingDate"))),
        Quantity = r.GetInt32(r.GetOrdinal("Quantity")),
        FreeQuantity = r.GetInt32(r.GetOrdinal("FreeQuantity")),
        PurchasePricePaisa = r.GetInt64(r.GetOrdinal("PurchasePricePaisa")),
        MrpPaisa = r.GetInt64(r.GetOrdinal("MrpPaisa")),
        SellingPricePaisa = r.GetInt64(r.GetOrdinal("SellingPricePaisa")),
        GstRatePercent = r.GetDecimal(r.GetOrdinal("GstRatePercent")),
        GstAmountPaisa = r.GetInt64(r.GetOrdinal("GstAmountPaisa")),
        DiscountPercent = r.GetDecimal(r.GetOrdinal("DiscountPercent")),
        DiscountAmountPaisa = r.GetInt64(r.GetOrdinal("DiscountAmountPaisa")),
        LineTotalPaisa = r.GetInt64(r.GetOrdinal("LineTotalPaisa"))
    };
}

public class PurchaseItemRepository : IPurchaseItemRepository
{
    private readonly Database.DatabaseManager _db;
    public PurchaseItemRepository(Database.DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<PurchaseItem>> GetByPurchaseIdAsync(long purchaseId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM PurchaseItems WHERE PurchaseId = @PurchaseId ORDER BY Id";
        cmd.Parameters.AddWithValue("@PurchaseId", purchaseId);
        var list = new List<PurchaseItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(PurchaseRepository.MapPurchaseItem(reader));
        return list;
    }

    public async Task InsertAsync(PurchaseItem item)
    {
        await InsertRangeAsync(new[] { item });
    }

    public async Task InsertRangeAsync(IEnumerable<PurchaseItem> items)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        foreach (var item in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO PurchaseItems (PurchaseId, MedicineId, MedicineName, BatchNumber, ExpiryDate, ManufacturingDate,
                    Quantity, FreeQuantity, PurchasePricePaisa, MrpPaisa, SellingPricePaisa, GstRatePercent, GstAmountPaisa,
                    DiscountPercent, DiscountAmountPaisa, LineTotalPaisa)
                VALUES (@PurchaseId, @MedicineId, @MedicineName, @BatchNumber, @ExpiryDate, @ManufacturingDate,
                    @Quantity, @FreeQuantity, @PurchasePricePaisa, @MrpPaisa, @SellingPricePaisa, @GstRatePercent, @GstAmountPaisa,
                    @DiscountPercent, @DiscountAmountPaisa, @LineTotalPaisa);";
            PurchaseRepository.AddPurchaseItemParams(cmd, item);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
