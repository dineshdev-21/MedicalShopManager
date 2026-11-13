using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Point of Sale billing service implementing 10-step atomic transactional sales,
/// GST calculations, and invoice numbering.
/// </summary>
public class BillingService : IBillingService
{
    private readonly ISaleRepository _saleRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDocumentNumberService _documentNumbers;
    private readonly IAuditService _auditService;
    private readonly IExcelSyncQueueRepository _excelSyncQueueRepository;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        ISaleRepository saleRepository,
        IBatchRepository batchRepository,
        IPaymentRepository paymentRepository,
        IDocumentNumberService documentNumbers,
        IAuditService auditService,
        IExcelSyncQueueRepository excelSyncQueueRepository,
        ILogger<BillingService> logger)
    {
        _saleRepository = saleRepository;
        _batchRepository = batchRepository;
        _paymentRepository = paymentRepository;
        _documentNumbers = documentNumbers;
        _auditService = auditService;
        _excelSyncQueueRepository = excelSyncQueueRepository;
        _logger = logger;
    }

    public (long cgst, long sgst, long igst) CalculateGst(long taxableAmountPaisa, decimal gstRatePercent, bool isInterState = false)
    {
        if (taxableAmountPaisa <= 0 || gstRatePercent <= 0)
            return (0, 0, 0);

        // Tax once on the taxable value, then split (CGST takes any odd paisa) so CGST + SGST == total tax.
        var tax = PricingCalculator.TaxOnTaxable(taxableAmountPaisa, gstRatePercent);
        var split = PricingCalculator.SplitTax(tax, isInterState);
        return (split.CgstPaisa, split.SgstPaisa, split.IgstPaisa);
    }

    /// <summary>
    /// One interim POS line (per-unit rate, GST-inclusive). BillingCartItem builds the same SaleLineInput, so the
    /// screen total and the saved invoice cannot differ by a paisa. The discount is clamped to 0–100 %.
    /// </summary>
    private static SaleLineAmounts CalculateLine(long unitPricePaisa, int quantity, decimal discountPercent, decimal gstRatePercent) =>
        PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = unitPricePaisa,
            UnitsPerPack = 1,
            Units = Math.Max(quantity, 0),
            DiscountPercent = PricingCalculator.ClampDiscount(discountPercent),
            GstPercent = gstRatePercent,
            TaxMode = TaxMode.Inclusive
        });

    /// <summary>Preview of the next sale number for an unsaved bill; the real number is allocated when the sale is inserted.</summary>
    public Task<string> GenerateNextInvoiceNumberAsync() => _documentNumbers.PeekNextAsync(DocumentType.Sale);

    public async Task<Sale> ProcessSaleAsync(Sale sale, long userId)
    {
        if (sale.Items == null || sale.Items.Count == 0)
            throw new InvalidOperationException("Cannot process a sale without line items.");

        // Step 1: Pre-validation of batches and stock (friendly messages; the insert re-checks stock atomically)
        var batches = new List<MedicineBatch>();
        foreach (var item in sale.Items)
        {
            if (item.Quantity <= 0)
                throw new InvalidOperationException($"Invalid quantity {item.Quantity} for item {item.MedicineName}");

            var batch = await _batchRepository.GetByIdAsync(item.BatchId)
                ?? throw new KeyNotFoundException($"Batch {item.BatchNumber} (ID: {item.BatchId}) not found");

            if (batch.IsExpired)
                throw new InvalidOperationException($"Batch {batch.BatchNumber} for {item.MedicineName} has expired on {batch.ExpiryDate:dd-MM-yyyy} and cannot be sold");

            if (!batch.IsActive)
                throw new InvalidOperationException($"Batch {batch.BatchNumber} is marked inactive");

            if (batch.QuantityRemaining < item.Quantity)
            {
                throw new InvalidOperationException(
                    $"Insufficient stock for {item.MedicineName} (Batch: {batch.BatchNumber}). Available: {batch.QuantityRemaining}, Requested: {item.Quantity}");
            }

            batches.Add(batch);
        }

        // Step 2: Invoice numbering happens inside the insert transaction (SaleRepository.InsertWithItemsAsync),
        // so a failed sale never consumes or duplicates a number.

        // Step 3: Line amounts through PricingCalculator. Sale rates are GST-inclusive (MRP basis), so GST is
        // backed out of the line amount, never added on top. This interim POS bills per unit (pack of 1).
        var lines = new List<SaleLineAmounts>();
        foreach (var item in sale.Items)
        {
            var line = CalculateLine(item.UnitPricePaisa, item.Quantity, item.DiscountPercent, item.GstRatePercent);

            item.DiscountAmountPaisa = line.LineDiscountPaisa;
            item.TaxableValuePaisa = line.TaxablePaisa;
            item.CgstAmountPaisa = line.CgstPaisa;
            item.SgstAmountPaisa = line.SgstPaisa;
            item.IgstAmountPaisa = line.IgstPaisa;
            item.TaxAmountPaisa = line.TaxPaisa;
            item.LineTotalPaisa = line.TotalPaisa;
            lines.Add(line);
        }

        var totals = PricingCalculator.SummarizeBill(lines, RoundOffMode.None);
        sale.SubtotalPaisa = totals.GrossPaisa;
        sale.DiscountAmountPaisa = totals.DiscountPaisa;
        sale.TaxableAmountPaisa = totals.TaxablePaisa;
        sale.CgstAmountPaisa = totals.CgstPaisa;
        sale.SgstAmountPaisa = totals.SgstPaisa;
        sale.IgstAmountPaisa = totals.IgstPaisa;
        sale.GrandTotalPaisa = totals.NetPayablePaisa;

        // Payment status & change
        if (sale.AmountPaidPaisa >= sale.GrandTotalPaisa)
        {
            sale.PaymentStatus = PaymentStatus.Paid;
            sale.BalanceDuePaisa = 0;
            if (sale.CashReceivedPaisa > sale.GrandTotalPaisa)
            {
                sale.ChangeReturnedPaisa = sale.CashReceivedPaisa - sale.GrandTotalPaisa;
            }
        }
        else if (sale.AmountPaidPaisa > 0)
        {
            sale.PaymentStatus = PaymentStatus.Partial;
            sale.BalanceDuePaisa = sale.GrandTotalPaisa - sale.AmountPaidPaisa;
        }
        else
        {
            sale.PaymentStatus = PaymentStatus.Unpaid;
            sale.BalanceDuePaisa = sale.GrandTotalPaisa;
        }

        sale.CreatedByUserId = userId;
        sale.InvoiceDate = DateTime.Now;
        sale.CreatedAt = DateTime.UtcNow;

        // Step 4: Atomic posting — sale, items, invoice number, stock deduction and stock ledger in one transaction
        var saleId = await _saleRepository.InsertWithItemsAsync(sale);
        sale.Id = saleId;

        // Step 5: Excel sync for the touched batches and medicines
        foreach (var batch in batches)
        {
            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Batch, batch.Id);
            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, batch.MedicineId);
        }

        // Step 6: Record payment if received
        if (sale.AmountPaidPaisa > 0)
        {
            var payment = new Payment
            {
                PaymentType = PaymentType.SalePayment,
                TransactionId = saleId,
                PaymentDate = DateTime.Now,
                AmountPaisa = sale.AmountPaidPaisa,
                PaymentMethod = sale.PaymentMethod,
                ReferenceNumber = sale.InvoiceNumber,
                Notes = $"Payment for Invoice {sale.InvoiceNumber}",
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow
            };
            var paymentId = await _paymentRepository.InsertAsync(payment);
            payment.Id = paymentId;
            sale.Payments.Add(payment);

            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Payment, paymentId);
        }

        // Step 7: Enqueue Excel Sync for Sale & SaleItems
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Sale, saleId);
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.SaleItem, saleId);

        // Step 8: Audit log
        await _auditService.LogAsync("CreateSale", "Sale", saleId, null,
            $"{{\"Invoice\":\"{sale.InvoiceNumber}\",\"Total\":{sale.GrandTotalPaisa},\"Items\":{sale.Items.Count}}}",
            "Sale invoice generated");

        _logger.LogInformation("Sale processed successfully: Invoice {Invoice} (ID: {SaleId}), Grand Total: {Total} Paisa",
            sale.InvoiceNumber, saleId, sale.GrandTotalPaisa);

        return sale;
    }

    public async Task<Sale?> GetSaleAsync(long id)
    {
        return await _saleRepository.GetWithItemsAsync(id);
    }

    public async Task<Sale?> GetSaleByInvoiceAsync(string invoiceNumber)
    {
        return await _saleRepository.GetByInvoiceNumberAsync(invoiceNumber);
    }

    public async Task<IReadOnlyList<Sale>> GetSalesByDateAsync(DateTime from, DateTime to)
    {
        return await _saleRepository.GetByDateRangeAsync(from, to);
    }

    public async Task<IReadOnlyList<Sale>> SearchSalesAsync(string query)
    {
        return await _saleRepository.SearchAsync(query);
    }
}
