using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Purchase entry rules (spec 04): every computed field comes from <see cref="PurchaseCalculator"/>, every save is
/// validated, stock and money changes go through the repository's transactions and are audit-logged afterwards.
/// </summary>
public sealed class PurchaseEntryService : IPurchaseEntryService
{
    private readonly IPurchaseBillRepository _purchases;
    private readonly IDistributorRepository _distributors;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly IExcelSyncQueueRepository _excel;
    private readonly ILogger<PurchaseEntryService> _logger;

    public PurchaseEntryService(IPurchaseBillRepository purchases, IDistributorRepository distributors, IAppSettingsRepository settings,
        IAuditService audit, IExcelSyncQueueRepository excel, ILogger<PurchaseEntryService> logger)
    {
        _purchases = purchases;
        _distributors = distributors;
        _settings = settings;
        _audit = audit;
        _excel = excel;
        _logger = logger;
    }

    public Task<PurchaseSettings> GetSettingsAsync() => PurchaseSettingsReader.ReadAsync(_settings);

    public async Task<PurchaseTotals> RecalculateAsync(PurchaseBill bill)
    {
        var settings = await GetSettingsAsync();
        return PurchaseCalculator.Recalculate(bill, settings.RoundOffToRupee ? RoundOffMode.NearestRupee : RoundOffMode.None);
    }

    public async Task<PurchaseBillLine> CreateLineAsync(Medicine medicine)
    {
        ArgumentNullException.ThrowIfNull(medicine);
        var settings = await GetSettingsAsync();
        var last = await _purchases.GetLastLineForMedicineAsync(medicine.Id);
        var pack = Math.Max(1, medicine.UnitsPerPack);
        var samePack = last != null && last.UnitsPerPack == pack;
        var mrp = samePack && last!.MrpPaisa > 0 ? last.MrpPaisa : medicine.DefaultMrpPaisa;

        return new PurchaseBillLine
        {
            MedicineId = medicine.Id,
            MedicineName = medicine.Name,
            HsnCode = !string.IsNullOrWhiteSpace(medicine.HsnCode) ? medicine.HsnCode : last?.HsnCode,
            UnitsPerPack = pack,
            GstRatePercent = medicine.GstRatePercent > 0 ? medicine.GstRatePercent : last?.GstRatePercent ?? 0m,
            MrpPaisa = mrp,
            EnteredRatePaisa = samePack ? last!.EnteredRatePaisa : 0,
            PtrPaisa = samePack ? last!.PtrPaisa : 0,
            DiscountPercent = last?.DiscountPercent ?? 0m,
            SaleRatePaisa = PurchaseCalculator.DefaultSaleRate(mrp, settings.DefaultSaleDiscountPercent)
        };
    }

    public async Task<bool> IsInterStateAsync(Distributor distributor)
    {
        ArgumentNullException.ThrowIfNull(distributor);
        var settings = await GetSettingsAsync();
        return PricingCalculator.IsInterStateSupply(settings.BusinessStateCode, distributor.Gstin);
    }

    public Task<PurchaseBill?> GetAsync(long id) => _purchases.GetAsync(id);

    public Task<IReadOnlyList<PurchaseListRow>> ListAsync(PurchaseListFilter filter)
    {
        if (filter.To.Date < filter.From.Date) throw new PurchaseRuleException("The 'to' date is before the 'from' date.");
        return _purchases.ListAsync(filter);
    }

    public async Task<PurchaseListRow?> FindDuplicateAsync(PurchaseBill bill)
    {
        if (bill.SupplierId <= 0 || string.IsNullOrWhiteSpace(bill.SupplierInvoiceNumber)) return null;
        return await _purchases.FindDuplicateAsync(bill.SupplierId, bill.SupplierInvoiceNumber.Trim(), bill.Id);
    }

    public async Task<long> SaveDraftAsync(PurchaseBill bill, long? userId)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var errors = new List<string>();
        if (bill.SupplierId <= 0) errors.Add("Please add Distributor");
        for (var i = 0; i < bill.Lines.Count; i++)
        {
            var l = bill.Lines[i];
            if (l.MedicineId <= 0) errors.Add($"Please fill Product Name on Row: {i + 1}");
            if (l.UnitsPerPack < 1) errors.Add($"Please fill Units Per Pack on Row: {i + 1}");
            if (l.DiscountPercent is < 0 or > 100 || l.SchemePercent is < 0 or > 100 || l.GstRatePercent is < 0 or > 28 || l.Quantity < 0 || l.FreeQuantity < 0)
                errors.Add($"Check quantity, discount and GST on Row: {i + 1}");
        }
        if (bill.BillDiscountPercent is < 0 or > 100) errors.Add("Bill discount must be between 0 and 100%.");
        if (errors.Count > 0) throw new PurchaseValidationException(errors);

        await ApplyDistributorAsync(bill);
        bill.CreatedByUserId ??= userId;
        await RecalculateAsync(bill);
        var id = await _purchases.SaveDraftAsync(bill);

        await _audit.LogAsync("SavePurchaseDraft", "Purchase", id, null, Json(bill), "Purchase draft saved");
        await _excel.EnqueueAsync(SyncEntityType.Purchase, id);
        return id;
    }

    public async Task<PurchasePostingResult> ConfirmAsync(PurchaseBill bill, PurchasePaymentInput? initialPayment, long? userId, bool allowDuplicateInvoice = false)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Status != PurchaseStatus.Draft) throw new PurchaseRuleException("This purchase is already confirmed or cancelled.");
        var distributor = await ApplyDistributorAsync(bill);
        if (!bill.DueDate.HasValue) bill.DueDate = PurchaseCalculator.DueDate(bill.InvoiceDate, distributor?.CreditDays ?? 0);
        bill.CreatedByUserId ??= userId;

        var paying = initialPayment?.AmountPaisa ?? 0;
        await RecalculateAsync(bill);
        var errors = PurchaseValidator.ValidatePurchase(bill, DateTime.Today, paying).ToList();
        if (initialPayment != null && initialPayment.Date.Date > DateTime.Today) errors.Add("Payment date cannot be in the future.");
        if (paying > 0 && bill.AmountPaidPaisa + paying > bill.GrandTotalPaisa)
            errors.Add($"Payment cannot be more than the bill amount (₹{bill.GrandTotalPaisa / 100m:N2}).");
        if (errors.Count > 0) throw new PurchaseValidationException(errors);
        await RequireNoDuplicateAsync(bill, allowDuplicateInvoice);

        var result = await _purchases.ConfirmAsync(bill, paying > 0 ? initialPayment : null, userId);
        bill.AmountPaidPaisa += paying;
        bill.BalancePaisa = bill.GrandTotalPaisa - bill.AmountPaidPaisa;
        bill.PaymentStatus = PurchaseCalculator.PaymentStatusFor(bill.GrandTotalPaisa, bill.AmountPaidPaisa);

        await _audit.LogAsync("ConfirmPurchase", "Purchase", result.PurchaseId, null, Json(bill),
            $"Purchase {result.PurchaseNumber} posted to stock ({bill.TotalUnits} units)" + (paying > 0 ? $", paid ₹{paying / 100m:N2}" : ""));
        await EnqueueStockAsync(bill, result);
        _logger.LogInformation("Purchase {Number} confirmed: {Lines} lines, {Total} paisa", result.PurchaseNumber, bill.Lines.Count, bill.GrandTotalPaisa);
        return result;
    }

    public async Task<PurchasePostingResult> UpdateConfirmedAsync(PurchaseBill bill, long? userId, bool allowDuplicateInvoice = false)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var before = await _purchases.GetAsync(bill.Id) ?? throw new PurchaseRuleException($"Purchase #{bill.Id} was not found.");
        if (before.Status != PurchaseStatus.Confirmed) throw new PurchaseRuleException("Only a confirmed purchase can be edited.");

        await ApplyDistributorAsync(bill);
        bill.AmountPaidPaisa = before.AmountPaidPaisa;
        await RecalculateAsync(bill);
        var errors = PurchaseValidator.ValidatePurchase(bill, DateTime.Today, 0, expiryCutoff: bill.InvoiceDate).ToList();
        if (bill.GrandTotalPaisa < before.AmountPaidPaisa)
            errors.Add($"The bill total cannot be less than the ₹{before.AmountPaidPaisa / 100m:N2} already paid.");
        if (errors.Count > 0) throw new PurchaseValidationException(errors);
        await RequireNoDuplicateAsync(bill, allowDuplicateInvoice);

        var result = await _purchases.UpdateConfirmedAsync(bill, userId);
        await _audit.LogAsync("EditPurchase", "Purchase", bill.Id, Json(before), Json(bill),
            $"Purchase {result.PurchaseNumber} edited: stock reversed and re-posted");
        await EnqueueStockAsync(before, result);
        await EnqueueStockAsync(bill, result);
        return result;
    }

    public async Task DeleteAsync(long purchaseId, string reason, long? userId)
    {
        var bill = await _purchases.GetAsync(purchaseId) ?? throw new PurchaseRuleException($"Purchase #{purchaseId} was not found.");
        switch (bill.Status)
        {
            case PurchaseStatus.Draft:
                await _purchases.DeleteDraftAsync(purchaseId);
                await _audit.LogAsync("DeletePurchaseDraft", "Purchase", purchaseId, Json(bill), null, string.IsNullOrWhiteSpace(reason) ? "Draft deleted" : reason);
                break;
            case PurchaseStatus.Confirmed:
                if (string.IsNullOrWhiteSpace(reason)) throw new PurchaseRuleException("Please enter a reason for deleting this purchase.");
                await _purchases.CancelConfirmedAsync(purchaseId, reason.Trim(), userId);
                await _audit.LogAsync("CancelPurchase", "Purchase", purchaseId, Json(bill), "{\"Status\":\"Cancelled\"}",
                    $"Purchase {bill.PurchaseNumber} deleted, stock reversed: {reason.Trim()}");
                await EnqueueStockAsync(bill, null);
                break;
            default:
                throw new PurchaseRuleException("This purchase is already cancelled.");
        }
        await _excel.EnqueueAsync(SyncEntityType.Purchase, purchaseId);
    }

    public async Task RecordPaymentAsync(long purchaseId, PurchasePaymentInput payment, long? userId)
    {
        ValidatePayment(payment);
        var paymentId = await _purchases.RecordPaymentAsync(purchaseId, payment, userId);
        await _audit.LogAsync("RecordSupplierPayment", "Purchase", purchaseId, null,
            JsonSerializer.Serialize(new { payment.AmountPaisa, Method = payment.Method.ToString(), payment.Reference, PaymentId = paymentId }),
            "Supplier payment recorded");
        await _excel.EnqueueAsync(SyncEntityType.Payment, paymentId);
        await _excel.EnqueueAsync(SyncEntityType.Purchase, purchaseId);
    }

    public async Task<IReadOnlyList<PaymentAllocation>> RecordSupplierPaymentAsync(long supplierId, PurchasePaymentInput payment, long? userId)
    {
        ValidatePayment(payment);
        var allocations = await _purchases.RecordSupplierPaymentAsync(supplierId, payment, userId);
        await _audit.LogAsync("RecordSupplierPayment", "Supplier", supplierId, null,
            JsonSerializer.Serialize(new { payment.AmountPaisa, Method = payment.Method.ToString(), payment.Reference, Allocations = allocations }),
            $"Supplier payment spread over {allocations.Count} bill(s)");
        foreach (var a in allocations) await _excel.EnqueueAsync(SyncEntityType.Purchase, a.PurchaseId);
        await _excel.EnqueueAsync(SyncEntityType.Supplier, supplierId);
        return allocations;
    }

    public Task<IReadOnlyList<PurchasePaymentRecord>> GetPaymentsAsync(long purchaseId) => _purchases.GetPaymentsAsync(purchaseId);

    // ─── helpers ───

    private static void ValidatePayment(PurchasePaymentInput payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.AmountPaisa <= 0) throw new PurchaseRuleException("Payment amount must be more than zero.");
        if (payment.Date.Date > DateTime.Today) throw new PurchaseRuleException("Payment date cannot be in the future.");
        if (payment.Method == PaymentMethod.Credit) throw new PurchaseRuleException("Choose how the payment was made (cash, UPI, bank, card or cheque).");
    }

    /// <summary>Copies the distributor's current name and GSTIN onto the bill; refuses unknown distributors.</summary>
    private async Task<Distributor?> ApplyDistributorAsync(PurchaseBill bill)
    {
        if (bill.SupplierId <= 0) return null;
        var distributor = await _distributors.GetByIdAsync(bill.SupplierId)
            ?? throw new PurchaseRuleException("The selected distributor no longer exists.");
        bill.SupplierName = distributor.Name;
        bill.SupplierGstin = distributor.Gstin;
        return distributor;
    }

    private async Task RequireNoDuplicateAsync(PurchaseBill bill, bool allowDuplicateInvoice)
    {
        if (allowDuplicateInvoice) return;
        var duplicate = await FindDuplicateAsync(bill);
        if (duplicate != null) throw new DuplicatePurchaseInvoiceException(duplicate);
    }

    private async Task EnqueueStockAsync(PurchaseBill bill, PurchasePostingResult? result)
    {
        await _excel.EnqueueAsync(SyncEntityType.Purchase, result?.PurchaseId ?? bill.Id);
        foreach (var batchId in result?.BatchIds ?? bill.Lines.Where(l => l.BatchId.HasValue).Select(l => l.BatchId!.Value))
            await _excel.EnqueueAsync(SyncEntityType.Batch, batchId);
        foreach (var medicineId in bill.Lines.Select(l => l.MedicineId).Distinct())
            await _excel.EnqueueAsync(SyncEntityType.Medicine, medicineId);
        await _excel.EnqueueAsync(SyncEntityType.Supplier, bill.SupplierId);
    }

    private static string Json(PurchaseBill b) => JsonSerializer.Serialize(new
    {
        b.Id, b.PurchaseNumber, b.SupplierId, b.SupplierName, b.SupplierInvoiceNumber,
        InvoiceDate = b.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Status = b.Status.ToString(),
        b.TaxablePaisa, b.TaxAmountPaisa, b.GrandTotalPaisa, b.AmountPaidPaisa,
        Lines = b.Lines.Select(l => new { l.MedicineId, l.MedicineName, l.BatchNumber, l.UnitsPerPack, l.Quantity, l.FreeQuantity, l.PurchaseRatePaisa, l.LineTotalPaisa })
    });
}

/// <summary>Reads the Purchases.* settings (seeded by migration 12) with safe fallbacks.</summary>
internal static class PurchaseSettingsReader
{
    public static async Task<PurchaseSettings> ReadAsync(IAppSettingsRepository settings)
    {
        var discountText = await settings.GetValueAsync(PurchaseSettings.SaleDiscountKey);
        var monthsText = await settings.GetValueAsync(PurchaseSettings.NearExpiryMonthsKey);
        var roundText = await settings.GetValueAsync(PurchaseSettings.RoundOffKey);
        var shopGstin = await settings.GetValueAsync("ShopGstin");

        var discount = decimal.TryParse(discountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 0m, 100m) : 0m;
        var months = int.TryParse(monthsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? Math.Clamp(m, 1, 24) : 3;
        var round = roundText is null || roundText.Trim() is "1" or "true" or "True";
        var state = shopGstin is { Length: >= 2 } g && char.IsDigit(g[0]) && char.IsDigit(g[1]) ? g[..2] : null;
        return new PurchaseSettings(discount, months, round, state);
    }
}
