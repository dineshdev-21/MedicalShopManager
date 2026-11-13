using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>Purchase returns and expiry returns (debit notes): validation, totals, posting and audit.</summary>
public sealed class PurchaseReturnService : IPurchaseReturnService
{
    private readonly IPurchaseReturnRepository _returns;
    private readonly IDistributorRepository _distributors;
    private readonly IDocumentNumberService _numbers;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly IExcelSyncQueueRepository _excel;
    private readonly ILogger<PurchaseReturnService> _logger;

    public PurchaseReturnService(IPurchaseReturnRepository returns, IDistributorRepository distributors, IDocumentNumberService numbers,
        IAppSettingsRepository settings, IAuditService audit, IExcelSyncQueueRepository excel, ILogger<PurchaseReturnService> logger)
    {
        _returns = returns;
        _distributors = distributors;
        _numbers = numbers;
        _settings = settings;
        _audit = audit;
        _excel = excel;
        _logger = logger;
    }

    public Task<IReadOnlyList<ReturnableBatch>> GetReturnableBatchesAsync(long supplierId, long? purchaseId = null, string? search = null) =>
        _returns.GetReturnableBatchesAsync(supplierId, purchaseId, search);

    public async Task<IReadOnlyList<ReturnableBatch>> GetExpiryCandidatesAsync(long? supplierId, int? withinMonths = null)
    {
        var settings = await PurchaseSettingsReader.ReadAsync(_settings);
        var months = Math.Clamp(withinMonths ?? settings.NearExpiryMonths, 0, 24);
        return await _returns.GetExpiryCandidatesAsync(DateTime.Today.AddMonths(months), supplierId);
    }

    public PurchaseReturnLine CreateLine(ReturnableBatch batch, int units)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new PurchaseReturnLine
        {
            PurchaseItemId = batch.PurchaseItemId,
            MedicineId = batch.MedicineId,
            MedicineName = batch.MedicineName,
            HsnCode = batch.HsnCode,
            BatchId = batch.BatchId,
            BatchNumber = batch.BatchNumber,
            ExpiryDate = batch.ExpiryDate,
            UnitsPerPack = batch.UnitsPerPack,
            Quantity = Math.Clamp(units, 0, batch.MaxReturnableUnits),
            RatePaisa = batch.RatePaisa,
            GstRatePercent = batch.GstRatePercent
        };
    }

    public async Task RecalculateAsync(PurchaseReturnDocument doc)
    {
        var settings = await PurchaseSettingsReader.ReadAsync(_settings);
        PurchaseCalculator.RecalculateReturn(doc, settings.RoundOffToRupee ? RoundOffMode.NearestRupee : RoundOffMode.None);
    }

    public async Task<PurchaseReturnDocument> PostAsync(PurchaseReturnDocument doc, long? userId)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var distributor = doc.SupplierId > 0 ? await _distributors.GetByIdAsync(doc.SupplierId) : null;
        if (doc.SupplierId > 0 && distributor == null) throw new PurchaseRuleException("The selected distributor no longer exists.");
        if (distributor != null)
        {
            doc.SupplierName = distributor.Name;
            doc.SupplierGstin = distributor.Gstin;
            var settings = await PurchaseSettingsReader.ReadAsync(_settings);
            doc.IsInterState = PricingCalculator.IsInterStateSupply(settings.BusinessStateCode, distributor.Gstin);
        }

        await RecalculateAsync(doc);
        var errors = PurchaseValidator.ValidateReturn(doc, DateTime.Today).ToList();
        if (doc.RefundReceivedPaisa > doc.NetAmountPaisa)
            errors.Add($"Refund cannot be more than the debit note amount (₹{doc.NetAmountPaisa / 100m:N2}).");
        if (errors.Count > 0) throw new PurchaseValidationException(errors);

        var posted = await _returns.PostAsync(doc, userId);
        await _audit.LogAsync("PostDebitNote", "PurchaseReturn", posted.Id, null, JsonSerializer.Serialize(new
        {
            posted.ReturnNumber, Kind = posted.Kind.ToString(), posted.SupplierId, posted.SupplierName, Reason = posted.Reason.ToString(),
            posted.NetAmountPaisa, posted.RefundReceivedPaisa,
            Lines = posted.Lines.Select(l => new { l.MedicineId, l.BatchId, l.BatchNumber, l.Quantity, l.RatePaisa, l.LineTotalPaisa })
        }), $"{posted.Kind.ToLabel()} {posted.ReturnNumber}: stock out to {posted.SupplierName}");
        foreach (var line in posted.Lines)
        {
            await _excel.EnqueueAsync(SyncEntityType.Batch, line.BatchId);
            await _excel.EnqueueAsync(SyncEntityType.Medicine, line.MedicineId);
        }
        await _excel.EnqueueAsync(SyncEntityType.Supplier, posted.SupplierId);
        _logger.LogInformation("Debit note {Number} posted for supplier {Supplier}", posted.ReturnNumber, posted.SupplierId);
        return posted;
    }

    public Task<PurchaseReturnDocument?> GetAsync(long id) => _returns.GetAsync(id);

    public Task<IReadOnlyList<PurchaseReturnListRow>> ListAsync(DateTime from, DateTime to, long? supplierId = null)
    {
        if (to.Date < from.Date) throw new PurchaseRuleException("The 'to' date is before the 'from' date.");
        return _returns.ListAsync(from, to, supplierId);
    }

    public async Task CancelAsync(long id, long? userId)
    {
        var doc = await _returns.GetAsync(id) ?? throw new PurchaseRuleException($"Debit note #{id} was not found.");
        await _returns.CancelAsync(id, userId);
        await _audit.LogAsync("CancelDebitNote", "PurchaseReturn", id, JsonSerializer.Serialize(new { doc.ReturnNumber, doc.NetAmountPaisa }),
            "{\"IsCancelled\":true}", $"Debit note {doc.ReturnNumber} cancelled, stock put back");
        foreach (var line in doc.Lines) await _excel.EnqueueAsync(SyncEntityType.Batch, line.BatchId);
        await _excel.EnqueueAsync(SyncEntityType.Supplier, doc.SupplierId);
    }

    public Task<string> PeekNextNumberAsync(DateTime date) => _numbers.PeekNextAsync(DocumentType.DebitNote, date);
}
