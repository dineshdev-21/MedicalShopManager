using System.Globalization;
using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Read-only POS lookups on the frozen item master (Medicines / MedicineBatches): product search with stock
/// aggregates (spec 02 §F) and the batches of an item (§G). A batch is sellable while active, in stock and its
/// expiry date is after today (expired from the expiry date onwards, as <c>MedicineBatch.IsExpired</c>).
/// </summary>
public sealed class SaleCatalogRepository : ISaleCatalogRepository
{
    private const string Sellable = "b.IsActive = 1 AND b.QuantityRemaining > 0 AND b.ExpiryDate > @Today";

    private const string SelectItems = @"
        SELECT m.Id, m.Name, m.Composition, m.Manufacturer, m.PackLabel, m.UnitsPerPack, m.AllowLooseSale, m.Schedule,
               m.HsnCode, m.GstRatePercent, m.RackLocation, m.MaxDiscountPercent, m.Barcode, m.DefaultMrpPaisa,
               COALESCE(SUM(CASE WHEN " + Sellable + @" THEN b.QuantityRemaining END), 0) AS StockUnits,
               MIN(CASE WHEN " + Sellable + @" THEN b.ExpiryDate END) AS NearestExpiry,
               COUNT(CASE WHEN " + Sellable + @" THEN 1 END) AS SellableBatchCount,
               (SELECT b2.MrpPaisa FROM MedicineBatches b2
                 WHERE b2.MedicineId = m.Id AND b2.IsActive = 1 AND b2.QuantityRemaining > 0 AND b2.ExpiryDate > @Today
                 ORDER BY b2.ExpiryDate, b2.Id LIMIT 1) AS NearestMrpPaisa
        FROM Medicines m
        LEFT JOIN MedicineBatches b ON b.MedicineId = m.Id";

    private readonly DatabaseManager _db;

    public SaleCatalogRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<SaleCatalogItem>> SearchAsync(string text, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SaleCatalogItem>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectItems + @"
            WHERE m.Status = @Active AND (m.Name LIKE @Like ESCAPE '\' OR m.Composition LIKE @Like ESCAPE '\'
                OR m.Manufacturer LIKE @Like ESCAPE '\' OR m.Brand LIKE @Like ESCAPE '\' OR m.Barcode = @Exact OR m.Sku = @Exact)
            GROUP BY m.Id
            ORDER BY CASE WHEN StockUnits > 0 THEN 0 ELSE 1 END,
                     CASE WHEN m.Name LIKE @Prefix ESCAPE '\' THEN 0 ELSE 1 END, m.Name COLLATE NOCASE
            LIMIT @Limit");
        var trimmed = text.Trim();
        cmd.Add("@Like", BillingSql.Like(trimmed));
        cmd.Add("@Prefix", BillingSql.Like(trimmed)[1..]);
        cmd.Add("@Exact", trimmed);
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        AddCommon(cmd);
        return await ReadItemsAsync(cmd);
    }

    public async Task<SaleCatalogItem?> FindByCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectItems + @"
            WHERE m.Status = @Active AND (m.Barcode = @Code OR m.Sku = @Code)
            GROUP BY m.Id ORDER BY m.Id LIMIT 1");
        cmd.Add("@Code", code.Trim());
        AddCommon(cmd);
        return (await ReadItemsAsync(cmd)).FirstOrDefault();
    }

    public async Task<SaleCatalogItem?> GetItemAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectItems + " WHERE m.Id = @Id GROUP BY m.Id");
        cmd.Add("@Id", medicineId);
        AddCommon(cmd);
        return (await ReadItemsAsync(cmd)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<SaleBatchOption>> GetBatchesAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT * FROM MedicineBatches WHERE MedicineId = @Id AND IsActive = 1 ORDER BY ExpiryDate, Id");
        cmd.Add("@Id", medicineId);
        return await ReadBatchesAsync(cmd);
    }

    public async Task<SaleBatchOption?> GetBatchAsync(long batchId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT * FROM MedicineBatches WHERE Id = @Id");
        cmd.Add("@Id", batchId);
        return (await ReadBatchesAsync(cmd)).FirstOrDefault();
    }

    private static void AddCommon(SqliteCommand cmd)
    {
        cmd.Add("@Active", (int)MedicineStatus.Active);
        cmd.Add("@Today", DateTime.Today.ToString(BillingSql.DateFormat, CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<SaleCatalogItem>> ReadItemsAsync(SqliteCommand cmd)
    {
        var list = new List<SaleCatalogItem>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SaleCatalogItem
            {
                MedicineId = r.Long("Id"),
                Name = r.Str("Name") ?? string.Empty,
                Composition = r.Str("Composition"),
                Manufacturer = r.Str("Manufacturer"),
                PackLabel = r.Str("PackLabel"),
                UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
                AllowLooseSale = r.Bool("AllowLooseSale", true),
                Schedule = (DrugSchedule)r.Int("Schedule"),
                HsnCode = r.Str("HsnCode"),
                GstPercent = r.Dec("GstRatePercent"),
                RackLocation = r.Str("RackLocation"),
                MaxDiscountPercent = r.DecOrNull("MaxDiscountPercent"),
                Barcode = r.Str("Barcode"),
                StockUnits = r.Long("StockUnits"),
                NearestExpiry = r.DateOrNull("NearestExpiry"),
                MrpPerPackPaisa = r.LongOrNull("NearestMrpPaisa") ?? r.Long("DefaultMrpPaisa"),
                SellableBatchCount = r.Int("SellableBatchCount")
            });
        }
        return list;
    }

    private static async Task<IReadOnlyList<SaleBatchOption>> ReadBatchesAsync(SqliteCommand cmd)
    {
        var list = new List<SaleBatchOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SaleBatchOption
            {
                BatchId = r.Long("Id"),
                MedicineId = r.Long("MedicineId"),
                BatchNumber = r.Str("BatchNumber") ?? string.Empty,
                ExpiryDate = r.Date("ExpiryDate"),
                UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
                MrpPaisa = r.Long("MrpPaisa"),
                SaleRatePaisa = r.Long("SaleRatePaisa"),
                PurchaseRatePaisa = r.Long("PurchaseRatePaisa"),
                PtrPaisa = r.Long("PtrPaisa"),
                DiscountPercent = r.Dec("DiscountPercent"),
                GstPercent = r.Dec("GstRatePercent"),
                QuantityRemaining = r.Long("QuantityRemaining"),
                CreatedAt = r.Date("CreatedAt"),
                IsActive = r.Bool("IsActive", true)
            });
        }
        return list;
    }
}
