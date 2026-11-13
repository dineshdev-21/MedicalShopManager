using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Numbering;

namespace MedicalShopManager.Infrastructure.Repositories;

public class SaleRepository : ISaleRepository
{
    private readonly Database.DatabaseManager _db;
    private readonly ILogger<SaleRepository> _logger;

    public SaleRepository(Database.DatabaseManager db, ILogger<SaleRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Sale?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sales WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSale(reader) : null;
    }

    public async Task<Sale?> GetWithItemsAsync(long id)
    {
        var sale = await GetByIdAsync(id);
        if (sale == null) return null;

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SaleItems WHERE SaleId = @SaleId ORDER BY Id";
        cmd.Parameters.AddWithValue("@SaleId", id);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sale.Items.Add(MapSaleItem(reader));
        return sale;
    }

    public async Task<Sale?> GetByInvoiceNumberAsync(string invoiceNumber)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sales WHERE InvoiceNumber = @InvoiceNumber";
        cmd.Parameters.AddWithValue("@InvoiceNumber", invoiceNumber);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSale(reader) : null;
    }

    public async Task<IReadOnlyList<Sale>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sales WHERE InvoiceDate BETWEEN @From AND @To ORDER BY InvoiceDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd 23:59:59"));
        return await ReadSalesList(cmd);
    }

    public async Task<IReadOnlyList<Sale>> GetByCustomerAsync(long customerId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sales WHERE CustomerId = @CustomerId ORDER BY InvoiceDate DESC";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        return await ReadSalesList(cmd);
    }

    public async Task<IReadOnlyList<Sale>> SearchAsync(string query)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT * FROM Sales 
            WHERE InvoiceNumber LIKE @Q OR CustomerName LIKE @Q OR CustomerPhone LIKE @Q
            ORDER BY InvoiceDate DESC LIMIT 100";
        cmd.Parameters.AddWithValue("@Q", $"%{query}%");
        return await ReadSalesList(cmd);
    }

    public async Task<IReadOnlyList<Sale>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sales ORDER BY InvoiceDate DESC LIMIT 500";
        return await ReadSalesList(cmd);
    }

    public async Task<long> InsertAsync(Sale entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, CustomerName, CustomerPhone,
                SubtotalPaisa, DiscountAmountPaisa, TaxableAmountPaisa, CgstAmountPaisa, SgstAmountPaisa,
                IgstAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalanceDuePaisa, PaymentMethod,
                PaymentStatus, CashReceivedPaisa, ChangeReturnedPaisa, Notes, CreatedByUserId, PrescriptionRef, CreatedAt)
            VALUES (@InvoiceNumber, @InvoiceDate, @CustomerId, @CustomerName, @CustomerPhone,
                @SubtotalPaisa, @DiscountAmountPaisa, @TaxableAmountPaisa, @CgstAmountPaisa, @SgstAmountPaisa,
                @IgstAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalanceDuePaisa, @PaymentMethod,
                @PaymentStatus, @CashReceivedPaisa, @ChangeReturnedPaisa, @Notes, @CreatedByUserId, @PrescriptionRef, @CreatedAt);
            SELECT last_insert_rowid();";
        AddSaleParams(cmd, entity);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Posts a sale in one transaction: number, sale, items, stock deduction and stock ledger. A blank invoice
    /// number is allocated from the Sale document sequence inside the same transaction, so a rolled-back sale
    /// never consumes a number (and the number is cleared again so a retry allocates afresh). Stock is deducted
    /// with a guarded decrement, so two tills can never oversell a batch.
    /// </summary>
    public async Task<long> InsertWithItemsAsync(Sale sale)
    {
        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();
        var allocatedNumber = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sale.InvoiceNumber))
            {
                sale.InvoiceNumber = await DocumentSequenceAllocator.AllocateAsync(
                    conn, transaction, DocumentType.Sale, sale.InvoiceDate);
                allocatedNumber = true;
            }

            // Insert sale
            using var saleCmd = conn.CreateCommand();
            saleCmd.Transaction = transaction;
            saleCmd.CommandText = @"
                INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, CustomerName, CustomerPhone,
                    SubtotalPaisa, DiscountAmountPaisa, TaxableAmountPaisa, CgstAmountPaisa, SgstAmountPaisa,
                    IgstAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalanceDuePaisa, PaymentMethod,
                    PaymentStatus, CashReceivedPaisa, ChangeReturnedPaisa, Notes, CreatedByUserId, PrescriptionRef, CreatedAt)
                VALUES (@InvoiceNumber, @InvoiceDate, @CustomerId, @CustomerName, @CustomerPhone,
                    @SubtotalPaisa, @DiscountAmountPaisa, @TaxableAmountPaisa, @CgstAmountPaisa, @SgstAmountPaisa,
                    @IgstAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalanceDuePaisa, @PaymentMethod,
                    @PaymentStatus, @CashReceivedPaisa, @ChangeReturnedPaisa, @Notes, @CreatedByUserId, @PrescriptionRef, @CreatedAt);
                SELECT last_insert_rowid();";
            AddSaleParams(saleCmd, sale);
            var saleId = Convert.ToInt64(await saleCmd.ExecuteScalarAsync());

            // Insert items
            foreach (var item in sale.Items)
            {
                item.SaleId = saleId;
                using var itemCmd = conn.CreateCommand();
                itemCmd.Transaction = transaction;
                itemCmd.CommandText = @"
                    INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate,
                        HsnCode, Quantity, UnitPricePaisa, DiscountPercent, DiscountAmountPaisa, TaxableValuePaisa,
                        GstRatePercent, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa, TaxAmountPaisa,
                        CogsPaisa, LineTotalPaisa)
                    VALUES (@SaleId, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @ExpiryDate,
                        @HsnCode, @Quantity, @UnitPricePaisa, @DiscountPercent, @DiscountAmountPaisa, @TaxableValuePaisa,
                        @GstRatePercent, @CgstAmountPaisa, @SgstAmountPaisa, @IgstAmountPaisa, @TaxAmountPaisa,
                        @CogsPaisa, @LineTotalPaisa)";
                AddSaleItemParams(itemCmd, item);
                await itemCmd.ExecuteNonQueryAsync();

                var balance = await DeductStockAsync(conn, transaction, item);
                await InsertSaleMovementAsync(conn, transaction, sale, saleId, item, balance);
            }

            transaction.Commit();
            return saleId;
        }
        catch
        {
            transaction.Rollback();
            if (allocatedNumber) sale.InvoiceNumber = string.Empty;
            throw;
        }
    }

    /// <summary>Takes the item's units off its batch only if enough remain; returns the new balance.</summary>
    private static async Task<int> DeductStockAsync(SqliteConnection conn, SqliteTransaction transaction, SaleItem item)
    {
        if (item.Quantity <= 0)
            throw new InvalidOperationException($"Invalid quantity {item.Quantity} for item {item.MedicineName}");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining - @Qty, UpdatedAt = @UpdatedAt
            WHERE Id = @BatchId AND QuantityRemaining >= @Qty
            RETURNING QuantityRemaining;";
        cmd.Parameters.AddWithValue("@Qty", item.Quantity);
        cmd.Parameters.AddWithValue("@BatchId", item.BatchId);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        var balance = await cmd.ExecuteScalarAsync();
        if (balance is null or DBNull)
            throw new InvalidOperationException(
                $"Insufficient stock for {item.MedicineName} (Batch: {item.BatchNumber}). Requested: {item.Quantity}");
        return Convert.ToInt32(balance);
    }

    private static async Task InsertSaleMovementAsync(SqliteConnection conn, SqliteTransaction transaction,
        Sale sale, long saleId, SaleItem item, int balanceAfter)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber,
                MovementType, QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@MovementDate, @MedicineId, @MedicineName, @BatchId, @BatchNumber,
                @MovementType, 0, @QuantityOut, @BalanceAfter, @Reference, @Reason, @CreatedByUserId, @CreatedAt);";
        cmd.Parameters.AddWithValue("@MovementDate", sale.InvoiceDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@MedicineId", item.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", item.MedicineName);
        cmd.Parameters.AddWithValue("@BatchId", item.BatchId);
        cmd.Parameters.AddWithValue("@BatchNumber", item.BatchNumber);
        cmd.Parameters.AddWithValue("@MovementType", (int)MovementType.SaleDispatch);
        cmd.Parameters.AddWithValue("@QuantityOut", item.Quantity);
        cmd.Parameters.AddWithValue("@BalanceAfter", balanceAfter);
        cmd.Parameters.AddWithValue("@Reference", $"Sale:{saleId}");
        cmd.Parameters.AddWithValue("@Reason", $"Sale Invoice {sale.InvoiceNumber}");
        cmd.Parameters.AddWithValue("@CreatedByUserId", sale.CreatedByUserId.HasValue ? sale.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> UpdateAsync(Sale entity) => throw new NotSupportedException("Sales cannot be modified after creation.");

    public async Task<bool> DeleteAsync(long id) => throw new NotSupportedException("Sales cannot be deleted. Use returns instead.");

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Sales";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Sale>> ReadSalesList(SqliteCommand cmd)
    {
        var list = new List<Sale>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapSale(reader));
        return list;
    }

    private static void AddSaleParams(SqliteCommand cmd, Sale s)
    {
        cmd.Parameters.AddWithValue("@InvoiceNumber", s.InvoiceNumber);
        cmd.Parameters.AddWithValue("@InvoiceDate", s.InvoiceDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@CustomerId", s.CustomerId.HasValue ? s.CustomerId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerName", (object?)s.CustomerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerPhone", (object?)s.CustomerPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SubtotalPaisa", s.SubtotalPaisa);
        cmd.Parameters.AddWithValue("@DiscountAmountPaisa", s.DiscountAmountPaisa);
        cmd.Parameters.AddWithValue("@TaxableAmountPaisa", s.TaxableAmountPaisa);
        cmd.Parameters.AddWithValue("@CgstAmountPaisa", s.CgstAmountPaisa);
        cmd.Parameters.AddWithValue("@SgstAmountPaisa", s.SgstAmountPaisa);
        cmd.Parameters.AddWithValue("@IgstAmountPaisa", s.IgstAmountPaisa);
        cmd.Parameters.AddWithValue("@GrandTotalPaisa", s.GrandTotalPaisa);
        cmd.Parameters.AddWithValue("@AmountPaidPaisa", s.AmountPaidPaisa);
        cmd.Parameters.AddWithValue("@BalanceDuePaisa", s.BalanceDuePaisa);
        cmd.Parameters.AddWithValue("@PaymentMethod", (int)s.PaymentMethod);
        cmd.Parameters.AddWithValue("@PaymentStatus", (int)s.PaymentStatus);
        cmd.Parameters.AddWithValue("@CashReceivedPaisa", s.CashReceivedPaisa);
        cmd.Parameters.AddWithValue("@ChangeReturnedPaisa", s.ChangeReturnedPaisa);
        cmd.Parameters.AddWithValue("@Notes", (object?)s.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", s.CreatedByUserId.HasValue ? s.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@PrescriptionRef", (object?)s.PrescriptionRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", s.CreatedAt.ToString("o"));
    }

    internal static void AddSaleItemParams(SqliteCommand cmd, SaleItem si)
    {
        cmd.Parameters.AddWithValue("@SaleId", si.SaleId);
        cmd.Parameters.AddWithValue("@MedicineId", si.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", si.MedicineName);
        cmd.Parameters.AddWithValue("@BatchId", si.BatchId);
        cmd.Parameters.AddWithValue("@BatchNumber", si.BatchNumber);
        cmd.Parameters.AddWithValue("@ExpiryDate", si.ExpiryDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@HsnCode", (object?)si.HsnCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Quantity", si.Quantity);
        cmd.Parameters.AddWithValue("@UnitPricePaisa", si.UnitPricePaisa);
        cmd.Parameters.AddWithValue("@DiscountPercent", si.DiscountPercent);
        cmd.Parameters.AddWithValue("@DiscountAmountPaisa", si.DiscountAmountPaisa);
        cmd.Parameters.AddWithValue("@TaxableValuePaisa", si.TaxableValuePaisa);
        cmd.Parameters.AddWithValue("@GstRatePercent", si.GstRatePercent);
        cmd.Parameters.AddWithValue("@CgstAmountPaisa", si.CgstAmountPaisa);
        cmd.Parameters.AddWithValue("@SgstAmountPaisa", si.SgstAmountPaisa);
        cmd.Parameters.AddWithValue("@IgstAmountPaisa", si.IgstAmountPaisa);
        cmd.Parameters.AddWithValue("@TaxAmountPaisa", si.TaxAmountPaisa);
        cmd.Parameters.AddWithValue("@CogsPaisa", si.CogsPaisa);
        cmd.Parameters.AddWithValue("@LineTotalPaisa", si.LineTotalPaisa);
    }

    internal static Sale MapSale(SqliteDataReader r)
    {
        return new Sale
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            InvoiceNumber = r.GetString(r.GetOrdinal("InvoiceNumber")),
            InvoiceDate = DateTime.Parse(r.GetString(r.GetOrdinal("InvoiceDate"))),
            CustomerId = r.IsDBNull(r.GetOrdinal("CustomerId")) ? null : r.GetInt64(r.GetOrdinal("CustomerId")),
            CustomerName = r.IsDBNull(r.GetOrdinal("CustomerName")) ? null : r.GetString(r.GetOrdinal("CustomerName")),
            CustomerPhone = r.IsDBNull(r.GetOrdinal("CustomerPhone")) ? null : r.GetString(r.GetOrdinal("CustomerPhone")),
            SubtotalPaisa = r.GetInt64(r.GetOrdinal("SubtotalPaisa")),
            DiscountAmountPaisa = r.GetInt64(r.GetOrdinal("DiscountAmountPaisa")),
            TaxableAmountPaisa = r.GetInt64(r.GetOrdinal("TaxableAmountPaisa")),
            CgstAmountPaisa = r.GetInt64(r.GetOrdinal("CgstAmountPaisa")),
            SgstAmountPaisa = r.GetInt64(r.GetOrdinal("SgstAmountPaisa")),
            IgstAmountPaisa = r.GetInt64(r.GetOrdinal("IgstAmountPaisa")),
            GrandTotalPaisa = r.GetInt64(r.GetOrdinal("GrandTotalPaisa")),
            AmountPaidPaisa = r.GetInt64(r.GetOrdinal("AmountPaidPaisa")),
            BalanceDuePaisa = r.GetInt64(r.GetOrdinal("BalanceDuePaisa")),
            PaymentMethod = (PaymentMethod)r.GetInt32(r.GetOrdinal("PaymentMethod")),
            PaymentStatus = (PaymentStatus)r.GetInt32(r.GetOrdinal("PaymentStatus")),
            CashReceivedPaisa = r.GetInt64(r.GetOrdinal("CashReceivedPaisa")),
            ChangeReturnedPaisa = r.GetInt64(r.GetOrdinal("ChangeReturnedPaisa")),
            Notes = r.IsDBNull(r.GetOrdinal("Notes")) ? null : r.GetString(r.GetOrdinal("Notes")),
            CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
            PrescriptionRef = r.IsDBNull(r.GetOrdinal("PrescriptionRef")) ? null : r.GetString(r.GetOrdinal("PrescriptionRef")),
            CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")))
        };
    }

    internal static SaleItem MapSaleItem(SqliteDataReader r)
    {
        return new SaleItem
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            SaleId = r.GetInt64(r.GetOrdinal("SaleId")),
            MedicineId = r.GetInt64(r.GetOrdinal("MedicineId")),
            MedicineName = r.GetString(r.GetOrdinal("MedicineName")),
            BatchId = r.GetInt64(r.GetOrdinal("BatchId")),
            BatchNumber = r.GetString(r.GetOrdinal("BatchNumber")),
            ExpiryDate = DateTime.Parse(r.GetString(r.GetOrdinal("ExpiryDate"))),
            HsnCode = r.IsDBNull(r.GetOrdinal("HsnCode")) ? null : r.GetString(r.GetOrdinal("HsnCode")),
            Quantity = r.GetInt32(r.GetOrdinal("Quantity")),
            UnitPricePaisa = r.GetInt64(r.GetOrdinal("UnitPricePaisa")),
            DiscountPercent = r.GetDecimal(r.GetOrdinal("DiscountPercent")),
            DiscountAmountPaisa = r.GetInt64(r.GetOrdinal("DiscountAmountPaisa")),
            TaxableValuePaisa = r.GetInt64(r.GetOrdinal("TaxableValuePaisa")),
            GstRatePercent = r.GetDecimal(r.GetOrdinal("GstRatePercent")),
            CgstAmountPaisa = r.GetInt64(r.GetOrdinal("CgstAmountPaisa")),
            SgstAmountPaisa = r.GetInt64(r.GetOrdinal("SgstAmountPaisa")),
            IgstAmountPaisa = r.GetInt64(r.GetOrdinal("IgstAmountPaisa")),
            TaxAmountPaisa = r.GetInt64(r.GetOrdinal("TaxAmountPaisa")),
            CogsPaisa = r.GetInt64(r.GetOrdinal("CogsPaisa")),
            LineTotalPaisa = r.GetInt64(r.GetOrdinal("LineTotalPaisa"))
        };
    }
}

public class SaleItemRepository : ISaleItemRepository
{
    private readonly Database.DatabaseManager _db;
    public SaleItemRepository(Database.DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<SaleItem>> GetBySaleIdAsync(long saleId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SaleItems WHERE SaleId = @SaleId ORDER BY Id";
        cmd.Parameters.AddWithValue("@SaleId", saleId);
        var list = new List<SaleItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(SaleRepository.MapSaleItem(reader));
        return list;
    }

    public async Task InsertAsync(SaleItem item)
    {
        await InsertRangeAsync(new[] { item });
    }

    public async Task InsertRangeAsync(IEnumerable<SaleItem> items)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        foreach (var item in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate,
                    HsnCode, Quantity, UnitPricePaisa, DiscountPercent, DiscountAmountPaisa, TaxableValuePaisa,
                    GstRatePercent, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa, TaxAmountPaisa, CogsPaisa, LineTotalPaisa)
                VALUES (@SaleId, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @ExpiryDate,
                    @HsnCode, @Quantity, @UnitPricePaisa, @DiscountPercent, @DiscountAmountPaisa, @TaxableValuePaisa,
                    @GstRatePercent, @CgstAmountPaisa, @SgstAmountPaisa, @IgstAmountPaisa, @TaxAmountPaisa, @CogsPaisa, @LineTotalPaisa);";
            SaleRepository.AddSaleItemParams(cmd, item);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
