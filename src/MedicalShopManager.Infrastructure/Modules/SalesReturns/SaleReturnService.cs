using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.Infrastructure.Modules.SalesReturns;

/// <summary>
/// The sale return workflow. Rules and amounts come from <see cref="SaleReturnCalculator"/> (and so PricingCalculator);
/// posting, refunds and cancellation are transactional in <see cref="ISaleReturnRepository"/>; every money / stock
/// change is written to the audit log and queued for the Excel mirror.
/// </summary>
public sealed class SaleReturnService : ISaleReturnService
{
    public const string RoundOffSettingKey = "SalesReturns.RoundOffToRupee";

    private readonly ISaleReturnRepository _repository;
    private readonly IDocumentNumberService _numbers;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly IExcelSyncQueueRepository _excelSync;
    private readonly ILogger<SaleReturnService> _logger;
    private readonly Func<DateTime> _today;

    public SaleReturnService(ISaleReturnRepository repository, IDocumentNumberService numbers, IAppSettingsRepository settings,
        IAuditService audit, IExcelSyncQueueRepository excelSync, ILogger<SaleReturnService> logger)
        : this(repository, numbers, settings, audit, excelSync, logger, () => DateTime.Today)
    {
    }

    /// <summary>Test constructor with a fixed clock.</summary>
    public SaleReturnService(ISaleReturnRepository repository, IDocumentNumberService numbers, IAppSettingsRepository settings,
        IAuditService audit, IExcelSyncQueueRepository excelSync, ILogger<SaleReturnService> logger, Func<DateTime> today)
    {
        _repository = repository;
        _numbers = numbers;
        _settings = settings;
        _audit = audit;
        _excelSync = excelSync;
        _logger = logger;
        _today = today;
    }

    public async Task<RoundOffMode> GetRoundOffAsync()
    {
        var value = await _settings.GetValueAsync(RoundOffSettingKey);
        return value?.Trim() is "0" or "false" or "False" ? RoundOffMode.None : RoundOffMode.NearestRupee;
    }

    public async Task<(string ReturnNumber, string CreditNoteNumber)> PeekNumbersAsync(DateTime returnDate) =>
        (await _numbers.PeekNextAsync(DocumentType.SaleReturn, returnDate), await _numbers.PeekNextAsync(DocumentType.CreditNote, returnDate));

    public Task<ReturnableSale?> GetReturnableSaleAsync(long saleId) => _repository.GetReturnableSaleAsync(saleId);

    public Task<IReadOnlyList<SaleReturnItemOption>> SearchItemsAsync(string text) => _repository.SearchItemsAsync(text);

    public Task<IReadOnlyList<SaleReturnBatchInfo>> GetItemBatchesAsync(long medicineId) => _repository.GetItemBatchesAsync(medicineId);

    public Task<IReadOnlyList<SaleReturnCustomerOption>> SearchCustomersAsync(string text) => _repository.SearchCustomersAsync(text);

    public (SaleReturnDocument Document, IReadOnlyList<string> Errors) Preview(SaleReturnRequest request, ReturnableSale? sale,
        IReadOnlyDictionary<long, SaleReturnBatchInfo> batches, RoundOffMode roundOff) =>
        SaleReturnCalculator.Build(request, sale, batches, roundOff, _today());

    public async Task<SaleReturnDocument> PostAsync(SaleReturnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var roundOff = await GetRoundOffAsync();
        var sale = request.SaleId is { } saleId ? await _repository.GetReturnableSaleAsync(saleId) : null;
        var batches = request.SaleId is null
            ? await _repository.GetBatchesAsync(request.Lines.Where(l => l.Units > 0).Select(l => l.BatchId))
            : new Dictionary<long, SaleReturnBatchInfo>();

        var (document, errors) = SaleReturnCalculator.Build(request, sale, batches, roundOff, _today());
        if (errors.Count > 0) throw new SaleReturnValidationException(errors);

        var (id, returnNumber, creditNoteNumber) = await _repository.InsertAsync(document);
        document.Id = id;
        document.ReturnNumber = returnNumber;
        document.CreditNoteNumber = creditNoteNumber;

        await AuditAsync("CreateSaleReturn", id, null, Snapshot(document),
            $"Sale return {returnNumber} / credit note {creditNoteNumber} for ₹{SaleReturnCalculator.Rupees(document.NetAmountPaisa)} " +
            $"({document.RefundModeLabel}){(document.SaleInvoiceNumber is { } inv ? $" against {inv}" : " without bill")}");
        await EnqueueSyncAsync(document);
        return await _repository.GetAsync(id) ?? document;
    }

    public Task<SaleReturnDocument?> GetAsync(long id) => _repository.GetAsync(id);

    public async Task<(IReadOnlyList<SaleReturnRow> Rows, SaleReturnTotals Totals)> ListAsync(SaleReturnFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.OpenCreditOnly && filter.To.Date < filter.From.Date)
            throw new SaleReturnValidationException("The 'to' date is before the 'from' date.");
        var rows = await _repository.ListAsync(filter);
        return (rows, SaleReturnTotals.From(rows));
    }

    public async Task<SaleReturnDocument> RefundCreditAsync(CreditRefundRequest refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        var note = await _repository.GetAsync(refund.SaleReturnId)
                   ?? throw new SaleReturnValidationException("The credit note no longer exists.");
        var errors = SaleReturnCalculator.ValidateRefund(note, refund, _today());
        if (errors.Count > 0) throw new SaleReturnValidationException(errors);

        await _repository.AddRefundAsync(refund, note.CreditNoteNumber);
        await AuditAsync("RefundCreditNote", note.Id,
            JsonSerializer.Serialize(new { Open = note.CreditBalancePaisa }),
            JsonSerializer.Serialize(new { Refund = refund.AmountPaisa, Method = refund.Method.ToString(), refund.Reference }),
            $"Refunded ₹{SaleReturnCalculator.Rupees(refund.AmountPaisa)} of credit note {note.CreditNoteNumber}");
        await SafeEnqueueAsync(SyncEntityType.Payment, note.Id);
        return await _repository.GetAsync(note.Id) ?? note;
    }

    public async Task CancelAsync(long id, string reason, long? userId)
    {
        var note = await _repository.GetAsync(id) ?? throw new SaleReturnValidationException("The sale return no longer exists.");
        var errors = SaleReturnCalculator.ValidateCancel(note, reason);
        if (errors.Count > 0) throw new SaleReturnValidationException(errors);

        await _repository.CancelAsync(id, reason.Trim(), userId);
        await AuditAsync("CancelSaleReturn", id, Snapshot(note), null,
            $"Sale return {note.ReturnNumber} / credit note {note.CreditNoteNumber} cancelled: {reason.Trim()}");
        await EnqueueSyncAsync(note);
    }

    public Task<long> GetCustomerCreditAsync(long customerId) => _repository.GetCustomerCreditAsync(customerId);

    private static string Snapshot(SaleReturnDocument d) => JsonSerializer.Serialize(new
    {
        d.ReturnNumber,
        d.CreditNoteNumber,
        d.SaleId,
        d.CustomerId,
        RefundMode = d.RefundMode.ToString(),
        RefundMethod = d.RefundMethod.ToString(),
        d.TaxablePaisa,
        d.TaxPaisa,
        d.NetAmountPaisa,
        d.RefundedPaisa,
        Lines = d.Lines.Select(l => new { l.SaleItemId, l.MedicineId, l.BatchId, l.Units, l.LineTotalPaisa, Disposition = l.Disposition.ToString() })
    });

    private async Task AuditAsync(string action, long id, string? oldValues, string? newValues, string reason)
    {
        try
        {
            await _audit.LogAsync(action, "SaleReturn", id, oldValues, newValues, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for {Action} on sale return {Id}", action, id);
        }
    }

    private async Task EnqueueSyncAsync(SaleReturnDocument document)
    {
        await SafeEnqueueAsync(SyncEntityType.Return, document.Id);
        await SafeEnqueueAsync(SyncEntityType.Payment, document.Id);
        foreach (var line in document.Lines.Where(l => l.GoesToStock).DistinctBy(l => l.BatchId))
        {
            await SafeEnqueueAsync(SyncEntityType.Batch, line.BatchId);
            await SafeEnqueueAsync(SyncEntityType.Medicine, line.MedicineId);
        }
    }

    private async Task SafeEnqueueAsync(SyncEntityType type, long id)
    {
        try
        {
            await _excelSync.EnqueueAsync(type, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel sync enqueue failed for {Type} {Id}", type, id);
        }
    }
}
