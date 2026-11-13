using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// The pre-6.2 <see cref="IPurchaseService"/> contract served by the Purchases module's per-pack entry, so any caller
/// still using it gets transactional posting, real pack sizes and document numbers. Legacy <see cref="PurchaseItem"/>
/// prices are per unit; they are turned into per-pack rates of the item's pack size (exact: rate × units / pack is
/// the same amount), so batches carry the item's real UnitsPerPack instead of the old pack-of-1 interim.
/// </summary>
public sealed class PurchaseServiceAdapter : IPurchaseService
{
    private readonly IPurchaseEntryService _entry;
    private readonly IMedicineRepository _medicines;

    public PurchaseServiceAdapter(IPurchaseEntryService entry, IMedicineRepository medicines)
    {
        _entry = entry;
        _medicines = medicines;
    }

    public async Task<long> CreateDraftAsync(Purchase purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (purchase.Items == null || purchase.Items.Count == 0)
            throw new ArgumentException("Purchase must contain at least one line item.", nameof(purchase));

        var bill = new PurchaseBill
        {
            SupplierId = purchase.SupplierId,
            SupplierName = purchase.SupplierName,
            SupplierInvoiceNumber = purchase.SupplierInvoiceNumber,
            InvoiceDate = purchase.PurchaseDate.Date,
            Notes = purchase.Notes,
            CreatedByUserId = purchase.CreatedByUserId
        };
        foreach (var item in purchase.Items)
        {
            var medicine = await _medicines.GetByIdAsync(item.MedicineId);
            var pack = Math.Max(1, medicine?.UnitsPerPack ?? 1);
            bill.Lines.Add(new PurchaseBillLine
            {
                MedicineId = item.MedicineId,
                MedicineName = string.IsNullOrWhiteSpace(item.MedicineName) ? medicine?.Name ?? string.Empty : item.MedicineName,
                HsnCode = medicine?.HsnCode,
                BatchNumber = item.BatchNumber,
                ExpiryDate = item.ExpiryDate,
                ManufacturingDate = item.ManufacturingDate,
                UnitsPerPack = pack,
                Quantity = item.Quantity,
                FreeQuantity = item.FreeQuantity,
                EnteredRatePaisa = item.PurchasePricePaisa * pack,
                MrpPaisa = item.MrpPaisa * pack,
                SaleRatePaisa = item.SellingPricePaisa * pack,
                DiscountPercent = item.DiscountPercent,
                GstRatePercent = item.GstRatePercent
            });
        }

        var id = await _entry.SaveDraftAsync(bill, purchase.CreatedByUserId);
        CopyBack(bill, purchase);
        return id;
    }

    public async Task<Purchase> ConfirmPurchaseAsync(long purchaseId, long userId)
    {
        var bill = await _entry.GetAsync(purchaseId) ?? throw new KeyNotFoundException($"Purchase ID {purchaseId} not found");
        if (bill.Status == PurchaseStatus.Confirmed) throw new InvalidOperationException($"Purchase ID {purchaseId} is already confirmed");
        await _entry.ConfirmAsync(bill, null, userId, allowDuplicateInvoice: true);
        return ToLegacy((await _entry.GetAsync(purchaseId))!);
    }

    public async Task<Purchase?> GetPurchaseAsync(long id) =>
        await _entry.GetAsync(id) is { } bill ? ToLegacy(bill) : null;

    public async Task<IReadOnlyList<Purchase>> GetPurchasesBySupplierAsync(long supplierId)
    {
        var rows = await _entry.ListAsync(new PurchaseListFilter(new DateTime(2000, 1, 1), DateTime.Today.AddYears(1), supplierId));
        return rows.Select(ToLegacy).ToList();
    }

    public async Task<IReadOnlyList<Purchase>> GetPurchasesByDateAsync(DateTime from, DateTime to)
    {
        var rows = await _entry.ListAsync(new PurchaseListFilter(from, to));
        return rows.Select(ToLegacy).ToList();
    }

    public Task RecordPaymentAsync(long purchaseId, long amountPaisa, PaymentMethod method, string? reference, long userId) =>
        _entry.RecordPaymentAsync(purchaseId, new PurchasePaymentInput(amountPaisa, method, reference, DateTime.Now), userId);

    private static void CopyBack(PurchaseBill bill, Purchase purchase)
    {
        purchase.Id = bill.Id;
        purchase.SubtotalPaisa = bill.SubtotalPaisa;
        purchase.DiscountAmountPaisa = bill.DiscountAmountPaisa;
        purchase.TaxAmountPaisa = bill.TaxAmountPaisa;
        purchase.GrandTotalPaisa = bill.GrandTotalPaisa;
        purchase.BalancePaisa = bill.BalancePaisa;
        purchase.Status = bill.Status;
        purchase.PaymentStatus = bill.PaymentStatus;
    }

    private static Purchase ToLegacy(PurchaseBill bill)
    {
        var purchase = new Purchase
        {
            Id = bill.Id,
            SupplierInvoiceNumber = bill.SupplierInvoiceNumber,
            SupplierId = bill.SupplierId,
            SupplierName = bill.SupplierName,
            PurchaseDate = bill.InvoiceDate,
            SubtotalPaisa = bill.SubtotalPaisa,
            DiscountAmountPaisa = bill.DiscountAmountPaisa,
            TaxAmountPaisa = bill.TaxAmountPaisa,
            GrandTotalPaisa = bill.GrandTotalPaisa,
            AmountPaidPaisa = bill.AmountPaidPaisa,
            BalancePaisa = bill.BalancePaisa,
            Status = bill.Status,
            PaymentStatus = bill.PaymentStatus,
            Notes = bill.Notes,
            CreatedByUserId = bill.CreatedByUserId,
            CreatedAt = bill.CreatedAt,
            UpdatedAt = bill.UpdatedAt
        };
        foreach (var l in bill.Lines)
        {
            // Legacy items are per unit; display-only rounding of the per-pack rates.
            long PerUnit(long perPack) => PricingCalculator.RoundPaisa(PricingCalculator.UnitRatePaisa(perPack, Math.Max(1, l.UnitsPerPack)));
            purchase.Items.Add(new PurchaseItem
            {
                Id = l.Id,
                PurchaseId = l.PurchaseId,
                MedicineId = l.MedicineId,
                MedicineName = l.MedicineName,
                BatchNumber = l.BatchNumber,
                ExpiryDate = l.ExpiryDate,
                ManufacturingDate = l.ManufacturingDate,
                Quantity = l.Quantity,
                FreeQuantity = l.FreeQuantity,
                PurchasePricePaisa = PerUnit(l.PurchaseRatePaisa),
                MrpPaisa = PerUnit(l.MrpPaisa),
                SellingPricePaisa = PerUnit(l.SaleRatePaisa),
                GstRatePercent = l.GstRatePercent,
                GstAmountPaisa = l.GstAmountPaisa,
                DiscountPercent = l.DiscountPercent,
                DiscountAmountPaisa = l.DiscountAmountPaisa,
                LineTotalPaisa = l.LineTotalPaisa
            });
        }
        return purchase;
    }

    private static Purchase ToLegacy(PurchaseListRow row) => new()
    {
        Id = row.Id,
        SupplierInvoiceNumber = row.SupplierInvoiceNumber,
        SupplierId = row.SupplierId,
        SupplierName = row.SupplierName,
        PurchaseDate = row.InvoiceDate,
        TaxAmountPaisa = row.TaxAmountPaisa,
        GrandTotalPaisa = row.GrandTotalPaisa,
        AmountPaidPaisa = row.AmountPaidPaisa,
        BalancePaisa = row.BalancePaisa,
        Status = row.Status,
        PaymentStatus = row.PaymentStatus
    };
}
