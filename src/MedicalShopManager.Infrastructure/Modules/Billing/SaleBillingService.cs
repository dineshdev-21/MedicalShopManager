using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// The pack-based POS workflow (spec 02): validates with <see cref="BillingCalculator"/>, refreshes stock and expiry
/// from the database (the screen's figures are never trusted), posts through <see cref="IBillingSaleRepository"/> in
/// one transaction and then writes the audit log and Excel sync queue (best effort, after commit).
/// </summary>
public sealed class SaleBillingService : ISaleBillingService
{
    private readonly IBillingSaleRepository _sales;
    private readonly ISaleCatalogRepository _catalog;
    private readonly IBillingCustomerRepository _customers;
    private readonly IAppSettingsRepository _settings;
    private readonly IControlCenterSettingsService _controlCenter;
    private readonly IAuditService _audit;
    private readonly IExcelSyncQueueRepository _excelSync;
    private readonly IAuthService _auth;
    private readonly ILogger<SaleBillingService> _logger;
    private readonly Func<DateTime> _clock;

    public SaleBillingService(IBillingSaleRepository sales, ISaleCatalogRepository catalog, IBillingCustomerRepository customers,
        IAppSettingsRepository settings, IControlCenterSettingsService controlCenter, IAuditService audit,
        IExcelSyncQueueRepository excelSync, IAuthService auth, ILogger<SaleBillingService> logger)
        : this(sales, catalog, customers, settings, controlCenter, audit, excelSync, auth, logger, () => DateTime.Now)
    {
    }

    /// <summary>Test constructor with an injectable clock.</summary>
    public SaleBillingService(IBillingSaleRepository sales, ISaleCatalogRepository catalog, IBillingCustomerRepository customers,
        IAppSettingsRepository settings, IControlCenterSettingsService controlCenter, IAuditService audit,
        IExcelSyncQueueRepository excelSync, IAuthService auth, ILogger<SaleBillingService> logger, Func<DateTime> clock)
    {
        _sales = sales;
        _catalog = catalog;
        _customers = customers;
        _settings = settings;
        _controlCenter = controlCenter;
        _audit = audit;
        _excelSync = excelSync;
        _auth = auth;
        _logger = logger;
        _clock = clock;
    }

    // ───────────────────────── Settings and live computation ─────────────────────────

    public async Task<BillingSettings> GetSettingsAsync()
    {
        var sale = await _controlCenter.GetSaleSettingsAsync();
        var profile = await _controlCenter.GetBusinessProfileAsync();
        var draftDays = await IntAsync(BillingSettings.DraftRetentionDaysKey, 7);
        var order = await IntAsync(BillingSettings.BatchOrderKey, 0) == 1 ? BatchPickOrder.Lifo : BatchPickOrder.Fefo;
        return BillingSettings.From(sale, profile, draftDays, order);
    }

    /// <summary>Remembers the BATCH ORDER toggle of the billing screen (Billing-owned setting).</summary>
    public async Task SaveBatchOrderAsync(BatchPickOrder order) =>
        await _settings.SetValueAsync(BillingSettings.BatchOrderKey, order == BatchPickOrder.Lifo ? "1" : "0");

    public bool IsInterState(BillingSettings settings, string? customerGstin) =>
        PricingCalculator.IsInterStateSupply(settings.ShopStateCode, customerGstin);

    public BillComputation Compute(SalePostingRequest request, BillingSettings settings) =>
        BillingCalculator.ComputeBill(BillRequest.From(request, IsInterState(settings, request.CustomerGstin), settings));

    public IReadOnlyList<string> Validate(SalePostingRequest request, BillingSettings settings) =>
        BillingCalculator.ValidateSale(request, settings, _clock());

    public async Task<IReadOnlyList<SaleCatalogItem>> SearchItemsAsync(string text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length < BillingCalculator.MinSearchLength) return Array.Empty<SaleCatalogItem>();
        return await _catalog.SearchAsync(trimmed);
    }

    public Task<SaleCatalogItem?> FindByCodeAsync(string code) => _catalog.FindByCodeAsync(code);

    public Task<SaleCatalogItem?> GetItemAsync(long medicineId) => _catalog.GetItemAsync(medicineId);

    public async Task<IReadOnlyList<SaleBatchOption>> GetBatchesAsync(long medicineId, DateTime billDate)
    {
        var settings = await GetSettingsAsync();
        var batches = BillingCalculator.Annotate(await _catalog.GetBatchesAsync(medicineId), billDate, settings.NearExpiryMonths,
            settings.ExpiryCutoffMonths);
        return BillingCalculator.OrderBatches(batches, settings.BatchOrder);
    }

    // ───────────────────────── Save (new or edit) ─────────────────────────

    public async Task<PostedSale> SaveAsync(SalePostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await GetSettingsAsync();
        var userId = request.UserId ?? _auth.CurrentUser?.Id;
        BillingInvoice? original = null;
        if (request.EditingSaleId is { } editId)
        {
            original = await _sales.GetInvoiceAsync(editId) ?? throw new BillingValidationException("The sale being edited no longer exists.");
            if (original.Status != SaleStatus.Active) throw new BillingValidationException($"Invoice {original.InvoiceNumber} is deleted and cannot be edited.");
            if (await _sales.HasReturnsAsync(editId)) throw new BillingValidationException($"Invoice {original.InvoiceNumber} has sale returns and cannot be edited.");
        }

        var customer = await ResolveCustomerAsync(request);
        request = await RefreshLinesAsync(request with
        {
            UserId = userId,
            CustomerName = customer?.Name ?? request.CustomerName,
            CustomerPhone = customer?.Phone ?? request.CustomerPhone,
            CustomerGstin = customer?.Gstin ?? (request.IsCounterSale ? null : request.CustomerGstin),
            InvoiceDate = request.InvoiceDate == default ? _clock() : request.InvoiceDate
        }, original);

        var errors = Validate(request, settings);
        if (errors.Count > 0) throw new BillingValidationException(errors);

        var bill = Compute(request, settings);
        var (payment, paymentErrors) = BillingCalculator.ResolvePayment(bill.Totals.NetPayablePaisa, request);
        if (payment is null) throw new BillingValidationException(paymentErrors);

        var posting = new SalePosting(request, bill, payment, IsInterState(settings, request.CustomerGstin), settings.AllowNegativeStock);
        long saleId;
        string number;
        if (original is not null)
        {
            await _sales.UpdateAsync(original.Id, posting);
            (saleId, number) = (original.Id, original.InvoiceNumber);
            await AuditAsync("EditSale", saleId, Snapshot(original), Snapshot(number, bill, payment), "Sale invoice edited");
        }
        else
        {
            (saleId, number) = await _sales.InsertAsync(posting);
            await AuditAsync("CreateSale", saleId, null, Snapshot(number, bill, payment), "Sale invoice generated");
        }

        await EnqueueSyncAsync(saleId, request.Lines.Concat(original?.Lines.Select(ToLine) ?? Enumerable.Empty<BillLine>()));
        _logger.LogInformation("Sale {Invoice} (ID {SaleId}) saved: total {Total} paisa, paid {Paid}, due {Due}",
            number, saleId, bill.Totals.NetPayablePaisa, payment.AmountPaidPaisa, payment.BalanceDuePaisa);
        return new PostedSale(saleId, number, bill.Totals.NetPayablePaisa, payment.AmountPaidPaisa, payment.BalanceDuePaisa, payment.ChangePaisa);
    }

    private async Task<BillingCustomer?> ResolveCustomerAsync(SalePostingRequest request)
    {
        if (request.IsCounterSale || request.CustomerId is not > 0) return null;
        var customer = await _customers.GetByIdAsync(request.CustomerId.Value);
        if (customer is null || !customer.IsActive) throw new BillingValidationException("Please add customer (the selected customer no longer exists)");
        return customer;
    }

    /// <summary>
    /// Re-reads every line's batch: availability = batch stock (+ this sale's own units when editing), expiry and
    /// pack size from the database, so stale screen data cannot oversell or sell an expired batch.
    /// </summary>
    private async Task<SalePostingRequest> RefreshLinesAsync(SalePostingRequest request, BillingInvoice? original)
    {
        var lines = new List<BillLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var batch = line.BatchId > 0 ? await _catalog.GetBatchAsync(line.BatchId) : null;
            if (batch is null || batch.MedicineId != line.MedicineId)
            {
                lines.Add(line with { AvailableUnits = 0 });
                continue;
            }
            var ownUnits = original?.Lines.Where(l => l.BatchId == batch.BatchId).Sum(l => (long)l.Quantity) ?? 0;
            lines.Add(line with
            {
                AvailableUnits = batch.QuantityRemaining + ownUnits,
                ExpiryDate = batch.ExpiryDate,
                BatchNumber = batch.BatchNumber,
                UnitsPerPack = Math.Max(batch.UnitsPerPack, 1)
            });
        }
        return request with { Lines = lines };
    }

    // ───────────────────────── Read, edit, replicate, delete ─────────────────────────

    public async Task<BillingInvoice?> GetInvoiceAsync(long saleId)
    {
        var invoice = await _sales.GetInvoiceAsync(saleId);
        if (invoice is null) return null;
        var settings = await GetSettingsAsync();
        invoice.IsTaxInvoice = settings.ShopChargesGst && invoice.WithGst;
        return invoice;
    }

    public async Task<SalePostingRequest?> LoadForEditAsync(long saleId)
    {
        var invoice = await _sales.GetInvoiceAsync(saleId);
        if (invoice is null || invoice.Status != SaleStatus.Active) return null;

        var lines = new List<BillLine>();
        foreach (var saved in invoice.Lines)
        {
            var batch = await _catalog.GetBatchAsync(saved.BatchId);
            var item = await _catalog.GetItemAsync(saved.MedicineId);
            var ownUnits = invoice.Lines.Where(l => l.BatchId == saved.BatchId).Sum(l => (long)l.Quantity);
            lines.Add(ToLine(saved) with
            {
                AllowLooseSale = item?.AllowLooseSale ?? true,
                MaxDiscountPercent = item?.MaxDiscountPercent,
                Composition = item?.Composition,
                CostPerPackExGstPaisa = batch?.PurchaseRatePaisa ?? 0,
                AvailableUnits = (batch?.QuantityRemaining ?? 0) + ownUnits
            });
        }

        // A bill discount entered in rupees is stored as per-line shares; re-entering their sum redistributes it identically.
        var billDiscountAmount = invoice.Lines.Sum(l => l.BillDiscountPaisa);
        return new SalePostingRequest
        {
            EditingSaleId = invoice.Id,
            InvoiceDate = invoice.InvoiceDate,
            IsCounterSale = invoice.IsCounterSale || invoice.CustomerId is not > 0,
            CustomerId = invoice.CustomerId,
            CustomerName = invoice.CustomerName,
            CustomerPhone = invoice.CustomerPhone,
            CustomerGstin = invoice.CustomerGstin,
            PatientName = invoice.PatientName,
            PatientAge = invoice.PatientAge,
            PatientGender = invoice.PatientGender,
            PatientAddress = invoice.PatientAddress,
            DoctorId = invoice.DoctorId,
            DoctorName = invoice.DoctorName,
            DoctorRegNo = invoice.DoctorRegNo,
            PrescriptionNumber = invoice.PrescriptionNumber,
            PrescriptionFilePath = invoice.PrescriptionFilePath,
            Lines = lines,
            BillDiscountKind = billDiscountAmount > 0 ? BillDiscountKind.Amount : BillDiscountKind.None,
            BillDiscountAmountPaisa = billDiscountAmount,
            CustomChargeName = invoice.CustomChargeName,
            CustomChargePaisa = invoice.CustomChargePaisa,
            WithGst = invoice.WithGst,
            IsCredit = invoice.AmountPaidPaisa == 0 && invoice.BalanceDuePaisa > 0,
            DueDate = invoice.DueDate,
            Tenders = invoice.Payments.Select(p => new SaleTender { Mode = ToTender(p.Method), AmountPaisa = p.AmountPaisa, Reference = p.Reference }).ToList(),
            Notes = invoice.Notes
        };
    }

    public async Task<(SalePostingRequest Request, IReadOnlyList<string> Warnings)> ReplicateAsync(long saleId)
    {
        var invoice = await _sales.GetInvoiceAsync(saleId) ?? throw new BillingValidationException("The sale to replicate was not found.");
        var settings = await GetSettingsAsync();
        var today = _clock();
        var warnings = new List<string>();
        var lines = new List<BillLine>();
        foreach (var saved in invoice.Lines)
        {
            var item = await _catalog.GetItemAsync(saved.MedicineId);
            if (item is null)
            {
                warnings.Add($"{saved.MedicineName} is no longer in the item master");
                continue;
            }
            var batches = BillingCalculator.Annotate(await _catalog.GetBatchesAsync(saved.MedicineId), today, settings.NearExpiryMonths,
                settings.ExpiryCutoffMonths);
            var needed = (long)saved.Quantity;
            var batch = batches.FirstOrDefault(b => b.BatchId == saved.BatchId && b.IsSellable && b.QuantityRemaining >= needed)
                        ?? BillingCalculator.OrderBatches(batches, settings.BatchOrder).FirstOrDefault(b => b.IsSellable && b.QuantityRemaining >= needed)
                        ?? BillingCalculator.PickDefaultBatch(batches, settings.BatchOrder);
            if (batch is null)
            {
                warnings.Add($"{saved.MedicineName} is out of stock");
                continue;
            }
            if (batch.BatchId != saved.BatchId) warnings.Add($"{saved.MedicineName}: batch {saved.BatchNumber} replaced by {batch.BatchNumber}");
            if (batch.QuantityRemaining < needed) warnings.Add($"{saved.MedicineName}: only {batch.StockDisplay} available");

            var pack = Math.Max(batch.UnitsPerPack, 1);
            var paid = saved.PaidUnits;
            lines.Add(BillingCalculator.CreateLine(item, batch, 0m, paid / pack, paid % pack) with
            {
                DiscountPercent = PricingCalculator.ClampDiscount(saved.DiscountPercent, 0m, item.MaxDiscountPercent),
                FreeUnits = saved.FreeUnits
            });
        }

        var request = new SalePostingRequest
        {
            InvoiceDate = today,
            IsCounterSale = invoice.IsCounterSale || invoice.CustomerId is not > 0,
            CustomerId = invoice.CustomerId,
            CustomerName = invoice.CustomerName,
            CustomerPhone = invoice.CustomerPhone,
            CustomerGstin = invoice.CustomerGstin,
            PatientName = invoice.PatientName,
            PatientAge = invoice.PatientAge,
            PatientGender = invoice.PatientGender,
            PatientAddress = invoice.PatientAddress,
            DoctorId = invoice.DoctorId,
            DoctorName = invoice.DoctorName,
            DoctorRegNo = invoice.DoctorRegNo,
            WithGst = invoice.WithGst,
            CustomChargeName = invoice.CustomChargeName,
            CustomChargePaisa = invoice.CustomChargePaisa,
            Lines = lines
        };
        return (request, warnings);
    }

    public async Task DeleteAsync(long saleId, long? userId, string reason)
    {
        var invoice = await _sales.GetInvoiceAsync(saleId) ?? throw new BillingValidationException("The sale was not found.");
        if (invoice.Status != SaleStatus.Active) throw new BillingValidationException($"Invoice {invoice.InvoiceNumber} is already deleted.");
        if (await _sales.HasReturnsAsync(saleId)) throw new BillingValidationException($"Invoice {invoice.InvoiceNumber} has sale returns and cannot be deleted.");
        if (string.IsNullOrWhiteSpace(reason)) reason = "Deleted from billing screen";

        await _sales.CancelAsync(saleId, userId ?? _auth.CurrentUser?.Id, reason.Trim());
        await AuditAsync("DeleteSale", saleId, Snapshot(invoice), null, reason.Trim());
        await EnqueueSyncAsync(saleId, invoice.Lines.Select(ToLine));
    }

    public Task<IReadOnlyList<H1RegisterEntry>> GetScheduleRegisterAsync(DateTime from, DateTime to) => _sales.GetScheduleRegisterAsync(from, to);

    // ───────────────────────── Helpers ─────────────────────────

    private static BillLine ToLine(BillingInvoiceLine saved) => new()
    {
        MedicineId = saved.MedicineId,
        MedicineName = saved.MedicineName,
        Manufacturer = saved.Manufacturer,
        Schedule = saved.Schedule,
        HsnCode = saved.HsnCode,
        RackLocation = saved.RackLocation,
        BatchId = saved.BatchId,
        BatchNumber = saved.BatchNumber,
        ExpiryDate = saved.ExpiryDate,
        UnitsPerPack = Math.Max(saved.UnitsPerPack, 1),
        MrpPerPackPaisa = saved.MrpPerPackPaisa,
        RatePerPackPaisa = saved.RatePerPackPaisa,
        DiscountPercent = saved.DiscountPercent,
        GstPercent = saved.GstPercent,
        Packs = saved.Packs,
        Loose = saved.Loose,
        FreeUnits = saved.FreeUnits
    };

    private static TenderMode ToTender(PaymentMethod method) => method switch
    {
        PaymentMethod.UPI => TenderMode.Upi,
        PaymentMethod.Card => TenderMode.Card,
        PaymentMethod.BankTransfer => TenderMode.BankTransfer,
        _ => TenderMode.Cash
    };

    private async Task<int> IntAsync(string key, int fallback) =>
        int.TryParse(await _settings.GetValueAsync(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string Snapshot(string number, BillComputation bill, PaymentResolution payment) => JsonSerializer.Serialize(new
    {
        Invoice = number,
        Total = bill.Totals.NetPayablePaisa,
        RoundOff = bill.Totals.RoundOffPaisa,
        Discount = bill.Totals.DiscountPaisa,
        Items = bill.ItemCount,
        Units = bill.TotalUnits,
        Paid = payment.AmountPaidPaisa,
        Due = payment.BalanceDuePaisa,
        Method = payment.Method.ToString()
    });

    private static string Snapshot(BillingInvoice invoice) => JsonSerializer.Serialize(new
    {
        Invoice = invoice.InvoiceNumber,
        Total = invoice.GrandTotalPaisa,
        Items = invoice.Lines.Count,
        Units = invoice.TotalUnits,
        Paid = invoice.AmountPaidPaisa,
        Due = invoice.BalanceDuePaisa,
        Lines = invoice.Lines.Select(l => new { l.MedicineId, l.BatchId, l.Quantity, l.LineTotalPaisa })
    });

    private async Task AuditAsync(string action, long saleId, string? oldValues, string? newValues, string reason)
    {
        try
        {
            await _audit.LogAsync(action, "Sale", saleId, oldValues, newValues, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for {Action} on sale {SaleId}", action, saleId);
        }
    }

    private async Task EnqueueSyncAsync(long saleId, IEnumerable<BillLine> lines)
    {
        try
        {
            await _excelSync.EnqueueAsync(SyncEntityType.Sale, saleId);
            await _excelSync.EnqueueAsync(SyncEntityType.SaleItem, saleId);
            await _excelSync.EnqueueAsync(SyncEntityType.Payment, saleId);
            foreach (var line in lines.DistinctBy(l => l.BatchId))
            {
                await _excelSync.EnqueueAsync(SyncEntityType.Batch, line.BatchId);
                await _excelSync.EnqueueAsync(SyncEntityType.Medicine, line.MedicineId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel sync enqueue failed for sale {SaleId}", saleId);
        }
    }
}
