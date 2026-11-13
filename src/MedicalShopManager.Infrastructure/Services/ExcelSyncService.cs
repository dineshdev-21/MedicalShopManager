using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Excel synchronization service maintaining a 14-sheet mirror workbook using ClosedXML.
/// Features atomic file replacement, file lock detection with retry, and formula injection protection.
/// </summary>
public class ExcelSyncService : IExcelSyncService
{
    private readonly IExcelSyncQueueRepository _queueRepository;
    private readonly IMedicineRepository _medicineRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISaleRepository _saleRepository;
    private readonly IPurchaseRepository _purchaseRepository;
    private readonly ISupplierRepository _supplierRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IExpenseRepository _expenseRepository;
    private readonly IReturnRepository _returnRepository;
    private readonly IStockMovementRepository _movementRepository;
    private readonly IAuditLogRepository _auditRepository;
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly ILogger<ExcelSyncService> _logger;

    private string _workbookPath;
    private DateTime? _lastSyncTime;
    private bool _isSyncing;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public string WorkbookPath => _workbookPath;
    public DateTime? LastSyncTime => _lastSyncTime;
    public int PendingCount => _pendingCount;
    public bool IsSyncing => _isSyncing;

    private int _pendingCount;

    public event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;

    public ExcelSyncService(
        IExcelSyncQueueRepository queueRepository,
        IMedicineRepository medicineRepository,
        IBatchRepository batchRepository,
        ISaleRepository saleRepository,
        IPurchaseRepository purchaseRepository,
        ISupplierRepository supplierRepository,
        ICustomerRepository customerRepository,
        IPaymentRepository paymentRepository,
        IExpenseRepository expenseRepository,
        IReturnRepository returnRepository,
        IStockMovementRepository movementRepository,
        IAuditLogRepository auditRepository,
        IAppSettingsRepository settingsRepository,
        ILogger<ExcelSyncService> logger)
    {
        _queueRepository = queueRepository;
        _medicineRepository = medicineRepository;
        _batchRepository = batchRepository;
        _saleRepository = saleRepository;
        _purchaseRepository = purchaseRepository;
        _supplierRepository = supplierRepository;
        _customerRepository = customerRepository;
        _paymentRepository = paymentRepository;
        _expenseRepository = expenseRepository;
        _returnRepository = returnRepository;
        _movementRepository = movementRepository;
        _auditRepository = auditRepository;
        _settingsRepository = settingsRepository;
        _logger = logger;

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(docs, "MedicalShopManager", "Excel");
        Directory.CreateDirectory(dir);
        _workbookPath = Path.Combine(dir, "MedicalShop_Data.xlsx");
    }

    public async Task InitializeWorkbookAsync()
    {
        if (!File.Exists(_workbookPath))
        {
            await RebuildWorkbookAsync();
        }
    }

    public async Task EnqueueSyncAsync(SyncEntityType entityType, long? entityId = null)
    {
        await _queueRepository.EnqueueAsync(entityType, entityId);
        _pendingCount = await _queueRepository.GetPendingCountAsync();

        SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs
        {
            Status = SyncStatus.Pending,
            Message = $"{_pendingCount} pending updates",
            PendingCount = _pendingCount,
            LastSyncTime = _lastSyncTime
        });
    }

    public async Task SyncEntityAsync(SyncEntityType entityType, long? entityId = null, CancellationToken ct = default)
    {
        await EnqueueSyncAsync(entityType, entityId);
        await ProcessQueueAsync(ct);
    }

    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        await RebuildWorkbookAsync(ct);
    }

    public async Task ProcessQueueAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(100, ct))
            return;

        _isSyncing = true;
        try
        {
            var pending = await _queueRepository.GetPendingAsync(50);
            if (pending.Count == 0)
            {
                _pendingCount = 0;
                return;
            }

            if (await IsWorkbookLockedAsync())
            {
                _logger.LogWarning("Excel workbook is locked by another process (likely open in Excel). Sync deferred.");
                SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs
                {
                    Status = SyncStatus.Locked,
                    Message = "Workbook locked. Please close Excel.",
                    PendingCount = pending.Count,
                    LastSyncTime = _lastSyncTime
                });
                return;
            }

            // Sync full rebuild or incremental
            await RebuildWorkbookInternalAsync(ct);

            foreach (var item in pending)
            {
                await _queueRepository.MarkCompletedAsync(item.Id);
            }

            _lastSyncTime = DateTime.Now;
            _pendingCount = await _queueRepository.GetPendingCountAsync();

            SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs
            {
                Status = SyncStatus.Completed,
                Message = "Workbook synced successfully",
                PendingCount = _pendingCount,
                LastSyncTime = _lastSyncTime
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Excel sync queue");
            SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs
            {
                Status = SyncStatus.Failed,
                Message = $"Sync error: {ex.Message}",
                PendingCount = _pendingCount,
                LastSyncTime = _lastSyncTime
            });
        }
        finally
        {
            _isSyncing = false;
            _syncLock.Release();
        }
    }

    public async Task RebuildWorkbookAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        _isSyncing = true;
        try
        {
            await RebuildWorkbookInternalAsync(ct);
            _lastSyncTime = DateTime.Now;
            _pendingCount = 0;

            SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs
            {
                Status = SyncStatus.Completed,
                Message = "Workbook fully rebuilt",
                PendingCount = 0,
                LastSyncTime = _lastSyncTime
            });
        }
        finally
        {
            _isSyncing = false;
            _syncLock.Release();
        }
    }

    private async Task RebuildWorkbookInternalAsync(CancellationToken ct)
    {
        var tempFile = $"{_workbookPath}.tmp_{Guid.NewGuid():N}";
        var dir = Path.GetDirectoryName(_workbookPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using (var wb = new XLWorkbook())
        {
            // 1. Medicines
            await PopulateMedicinesSheet(wb);

            // 2. Batches
            await PopulateBatchesSheet(wb);

            // 3. Sales
            await PopulateSalesSheet(wb);

            // 4. SaleItems
            await PopulateSaleItemsSheet(wb);

            // 5. Purchases
            await PopulatePurchasesSheet(wb);

            // 6. PurchaseItems
            await PopulatePurchaseItemsSheet(wb);

            // 7. Suppliers
            await PopulateSuppliersSheet(wb);

            // 8. Customers
            await PopulateCustomersSheet(wb);

            // 9. Payments
            await PopulatePaymentsSheet(wb);

            // 10. Expenses
            await PopulateExpensesSheet(wb);

            // 11. StockMovements
            await PopulateStockMovementsSheet(wb);

            // 12. Returns
            await PopulateReturnsSheet(wb);

            // 13. AuditLog
            await PopulateAuditLogSheet(wb);

            // 14. AppSettings
            await PopulateAppSettingsSheet(wb);

            wb.SaveAs(tempFile);
        }

        // Retry replacement with backoff if needed
        int attempts = 0;
        while (attempts < 3)
        {
            try
            {
                if (File.Exists(_workbookPath))
                    File.Delete(_workbookPath);

                File.Move(tempFile, _workbookPath);
                break;
            }
            catch (IOException)
            {
                attempts++;
                if (attempts >= 3)
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                    throw;
                }
                await Task.Delay(500 * attempts, ct);
            }
        }
    }

    private static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // Formula injection mitigation: prefix dangerous leading characters with single quote
        if (input.StartsWith("=") || input.StartsWith("+") || input.StartsWith("-") || input.StartsWith("@"))
            return "'" + input;
        return input;
    }

    private static void StyleHeader(IXLWorksheet ws, int colCount)
    {
        var headerRange = ws.Range(1, 1, 1, colCount);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromArgb(0, 120, 212); // #0078D4
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, colCount).AdjustToContents(10, 60);
    }

    private async Task PopulateMedicinesSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Medicines");
        string[] headers = { "Id", "Name", "Composition", "Brand", "Category", "HsnCode", "RackLocation", "MinStock", "MaxStock", "Unit", "Schedule", "Status", "GstRate", "DefaultSaleRatePerPack", "Manufacturer", "PackLabel", "UnitsPerPack", "DefaultMrpPerPack" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _medicineRepository.GetAllAsync();
        int r = 2;
        foreach (var m in list)
        {
            ws.Cell(r, 1).Value = m.Id;
            ws.Cell(r, 2).Value = Sanitize(m.Name);
            ws.Cell(r, 3).Value = Sanitize(m.Composition);
            ws.Cell(r, 4).Value = Sanitize(m.Brand);
            ws.Cell(r, 5).Value = Sanitize(m.Category);
            ws.Cell(r, 6).Value = Sanitize(m.HsnCode);
            ws.Cell(r, 7).Value = Sanitize(m.RackLocation);
            ws.Cell(r, 8).Value = m.MinStock;
            ws.Cell(r, 9).Value = m.MaxStock;
            ws.Cell(r, 10).Value = Sanitize(m.Unit);
            ws.Cell(r, 11).Value = m.Schedule.ToString();
            ws.Cell(r, 12).Value = m.Status.ToString();
            ws.Cell(r, 13).Value = (double)m.GstRatePercent;
            ws.Cell(r, 14).Value = (double)m.DefaultSellingPrice;
            ws.Cell(r, 14).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 15).Value = Sanitize(m.Manufacturer);
            ws.Cell(r, 16).Value = Sanitize(m.PackLabel);
            ws.Cell(r, 17).Value = m.UnitsPerPack;
            ws.Cell(r, 18).Value = (double)m.DefaultMrp;
            ws.Cell(r, 18).Style.NumberFormat.Format = "₹#,##0.00";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateBatchesSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Batches");
        string[] headers = { "Id", "MedicineId", "BatchNumber", "ManufacturingDate", "ExpiryDate", "PurchaseDate", "SupplierId", "QtyReceived", "QtyRemaining", "FreeUnits", "PurchaseRatePerPack", "SaleRatePerPack", "MrpPerPack", "Discount%", "GST%", "IsActive", "UnitsPerPack", "PtrPerPack" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _batchRepository.GetAllAsync();
        int r = 2;
        foreach (var b in list)
        {
            ws.Cell(r, 1).Value = b.Id;
            ws.Cell(r, 2).Value = b.MedicineId;
            ws.Cell(r, 3).Value = Sanitize(b.BatchNumber);
            ws.Cell(r, 4).Value = b.ManufacturingDate?.ToString("yyyy-MM-dd") ?? "";
            ws.Cell(r, 5).Value = b.ExpiryDate.ToString("yyyy-MM-dd");
            ws.Cell(r, 6).Value = b.PurchaseDate.ToString("yyyy-MM-dd");
            ws.Cell(r, 7).Value = b.SupplierId ?? 0;
            ws.Cell(r, 8).Value = b.QuantityReceived;
            ws.Cell(r, 9).Value = b.QuantityRemaining;
            ws.Cell(r, 10).Value = b.FreeUnits;
            ws.Cell(r, 11).Value = (double)b.PurchaseRate;
            ws.Cell(r, 11).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 12).Value = (double)b.SaleRate;
            ws.Cell(r, 12).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 13).Value = (double)b.Mrp;
            ws.Cell(r, 13).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 14).Value = (double)b.DiscountPercent;
            ws.Cell(r, 15).Value = (double)b.GstRatePercent;
            ws.Cell(r, 16).Value = b.IsActive ? "Yes" : "No";
            ws.Cell(r, 17).Value = b.UnitsPerPack;
            ws.Cell(r, 18).Value = (double)b.Ptr;
            ws.Cell(r, 18).Style.NumberFormat.Format = "₹#,##0.00";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateSalesSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Sales");
        string[] headers = { "Id", "InvoiceNumber", "InvoiceDate", "CustomerId", "CustomerName", "CustomerPhone", "Subtotal", "Discount", "TaxableAmount", "CGST", "SGST", "IGST", "GrandTotal", "AmountPaid", "BalanceDue", "PaymentMethod", "PaymentStatus" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _saleRepository.GetAllAsync();
        int r = 2;
        foreach (var s in list)
        {
            ws.Cell(r, 1).Value = s.Id;
            ws.Cell(r, 2).Value = Sanitize(s.InvoiceNumber);
            ws.Cell(r, 3).Value = s.InvoiceDate.ToString("yyyy-MM-dd HH:mm");
            ws.Cell(r, 4).Value = s.CustomerId ?? 0;
            ws.Cell(r, 5).Value = Sanitize(s.CustomerName);
            ws.Cell(r, 6).Value = Sanitize(s.CustomerPhone);
            ws.Cell(r, 7).Value = (double)s.Subtotal;
            ws.Cell(r, 8).Value = (double)s.DiscountAmount;
            ws.Cell(r, 9).Value = (double)s.TaxableAmount;
            ws.Cell(r, 10).Value = (double)s.CgstAmount;
            ws.Cell(r, 11).Value = (double)s.SgstAmount;
            ws.Cell(r, 12).Value = (double)s.IgstAmount;
            ws.Cell(r, 13).Value = (double)s.GrandTotal;
            ws.Cell(r, 14).Value = (double)s.AmountPaid;
            ws.Cell(r, 15).Value = (double)s.BalanceDue;
            ws.Cell(r, 16).Value = s.PaymentMethod.ToString();
            ws.Cell(r, 17).Value = s.PaymentStatus.ToString();

            for (int col = 7; col <= 15; col++)
                ws.Cell(r, col).Style.NumberFormat.Format = "₹#,##0.00";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateSaleItemsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("SaleItems");
        string[] headers = { "Id", "SaleId", "MedicineId", "MedicineName", "BatchId", "BatchNumber", "Quantity", "UnitPrice", "DiscountAmount", "TaxableValue", "GST%", "TaxAmount", "LineTotal" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var sales = await _saleRepository.GetAllAsync();
        int r = 2;
        foreach (var s in sales)
        {
            var fullSale = await _saleRepository.GetWithItemsAsync(s.Id);
            if (fullSale == null) continue;

            foreach (var item in fullSale.Items)
            {
                ws.Cell(r, 1).Value = item.Id;
                ws.Cell(r, 2).Value = item.SaleId;
                ws.Cell(r, 3).Value = item.MedicineId;
                ws.Cell(r, 4).Value = Sanitize(item.MedicineName);
                ws.Cell(r, 5).Value = item.BatchId;
                ws.Cell(r, 6).Value = Sanitize(item.BatchNumber);
                ws.Cell(r, 7).Value = item.Quantity;
                ws.Cell(r, 8).Value = (double)item.UnitPrice;
                ws.Cell(r, 9).Value = (double)item.DiscountAmount;
                ws.Cell(r, 10).Value = (double)item.TaxableValue;
                ws.Cell(r, 11).Value = (double)item.GstRatePercent;
                ws.Cell(r, 12).Value = (double)item.TaxAmount;
                ws.Cell(r, 13).Value = (double)item.LineTotal;

                ws.Cell(r, 8).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 9).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 10).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 12).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 13).Style.NumberFormat.Format = "₹#,##0.00";
                r++;
            }
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulatePurchasesSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Purchases");
        string[] headers = { "Id", "SupplierInvoice", "SupplierId", "SupplierName", "PurchaseDate", "Subtotal", "Discount", "Tax", "GrandTotal", "AmountPaid", "Balance", "Status", "PaymentStatus" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _purchaseRepository.GetAllAsync();
        int r = 2;
        foreach (var p in list)
        {
            ws.Cell(r, 1).Value = p.Id;
            ws.Cell(r, 2).Value = Sanitize(p.SupplierInvoiceNumber);
            ws.Cell(r, 3).Value = p.SupplierId;
            ws.Cell(r, 4).Value = Sanitize(p.SupplierName);
            ws.Cell(r, 5).Value = p.PurchaseDate.ToString("yyyy-MM-dd");
            ws.Cell(r, 6).Value = (double)p.Subtotal;
            ws.Cell(r, 7).Value = (double)p.DiscountAmount;
            ws.Cell(r, 8).Value = (double)p.TaxAmount;
            ws.Cell(r, 9).Value = (double)p.GrandTotal;
            ws.Cell(r, 10).Value = (double)p.AmountPaid;
            ws.Cell(r, 11).Value = (double)p.Balance;
            ws.Cell(r, 12).Value = p.Status.ToString();
            ws.Cell(r, 13).Value = p.PaymentStatus.ToString();

            for (int col = 6; col <= 11; col++)
                ws.Cell(r, col).Style.NumberFormat.Format = "₹#,##0.00";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulatePurchaseItemsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("PurchaseItems");
        string[] headers = { "Id", "PurchaseId", "MedicineId", "MedicineName", "BatchNumber", "ExpiryDate", "Qty", "FreeQty", "PurchasePrice", "MRP", "SellingPrice", "GST%", "LineTotal" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var purchases = await _purchaseRepository.GetAllAsync();
        int r = 2;
        foreach (var p in purchases)
        {
            var full = await _purchaseRepository.GetWithItemsAsync(p.Id);
            if (full == null) continue;

            foreach (var item in full.Items)
            {
                ws.Cell(r, 1).Value = item.Id;
                ws.Cell(r, 2).Value = item.PurchaseId;
                ws.Cell(r, 3).Value = item.MedicineId;
                ws.Cell(r, 4).Value = Sanitize(item.MedicineName);
                ws.Cell(r, 5).Value = Sanitize(item.BatchNumber);
                ws.Cell(r, 6).Value = item.ExpiryDate.ToString("yyyy-MM-dd");
                ws.Cell(r, 7).Value = item.Quantity;
                ws.Cell(r, 8).Value = item.FreeQuantity;
                ws.Cell(r, 9).Value = (double)item.PurchasePrice;
                ws.Cell(r, 10).Value = (double)item.Mrp;
                ws.Cell(r, 11).Value = (double)item.SellingPrice;
                ws.Cell(r, 12).Value = (double)item.GstRatePercent;
                ws.Cell(r, 13).Value = (double)item.LineTotal;

                ws.Cell(r, 9).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 10).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 11).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(r, 13).Style.NumberFormat.Format = "₹#,##0.00";
                r++;
            }
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateSuppliersSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Suppliers");
        string[] headers = { "Id", "Name", "ContactPerson", "Phone", "Email", "Gstin", "DrugLicenseNumber", "Address", "OutstandingBalance", "IsActive" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _supplierRepository.GetAllAsync();
        int r = 2;
        foreach (var s in list)
        {
            ws.Cell(r, 1).Value = s.Id;
            ws.Cell(r, 2).Value = Sanitize(s.Name);
            ws.Cell(r, 3).Value = Sanitize(s.ContactPerson);
            ws.Cell(r, 4).Value = Sanitize(s.Phone);
            ws.Cell(r, 5).Value = Sanitize(s.Email);
            ws.Cell(r, 6).Value = Sanitize(s.Gstin);
            ws.Cell(r, 7).Value = Sanitize(s.DrugLicenseNumber);
            ws.Cell(r, 8).Value = Sanitize(s.Address);
            ws.Cell(r, 9).Value = (double)s.OutstandingBalance;
            ws.Cell(r, 9).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 10).Value = s.IsActive ? "Yes" : "No";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateCustomersSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Customers");
        string[] headers = { "Id", "Name", "Phone", "Email", "Address", "DoctorName", "OutstandingBalance", "IsActive" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _customerRepository.GetAllAsync();
        int r = 2;
        foreach (var c in list)
        {
            ws.Cell(r, 1).Value = c.Id;
            ws.Cell(r, 2).Value = Sanitize(c.Name);
            ws.Cell(r, 3).Value = Sanitize(c.Phone);
            ws.Cell(r, 4).Value = Sanitize(c.Email);
            ws.Cell(r, 5).Value = Sanitize(c.Address);
            ws.Cell(r, 6).Value = Sanitize(c.DoctorName);
            ws.Cell(r, 7).Value = (double)c.OutstandingBalance;
            ws.Cell(r, 7).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 8).Value = c.IsActive ? "Yes" : "No";
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulatePaymentsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Payments");
        string[] headers = { "Id", "PaymentType", "TransactionId", "PaymentDate", "Amount", "Method", "ReferenceNumber", "Notes" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _paymentRepository.GetAllAsync();
        int r = 2;
        foreach (var p in list)
        {
            ws.Cell(r, 1).Value = p.Id;
            ws.Cell(r, 2).Value = p.PaymentType.ToString();
            ws.Cell(r, 3).Value = p.TransactionId;
            ws.Cell(r, 4).Value = p.PaymentDate.ToString("yyyy-MM-dd HH:mm");
            ws.Cell(r, 5).Value = (double)p.Amount;
            ws.Cell(r, 5).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 6).Value = p.PaymentMethod.ToString();
            ws.Cell(r, 7).Value = Sanitize(p.ReferenceNumber);
            ws.Cell(r, 8).Value = Sanitize(p.Notes);
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateExpensesSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Expenses");
        string[] headers = { "Id", "ExpenseDate", "Category", "Amount", "Description", "PaymentMethod", "ReferenceNumber" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _expenseRepository.GetAllAsync();
        int r = 2;
        foreach (var e in list)
        {
            ws.Cell(r, 1).Value = e.Id;
            ws.Cell(r, 2).Value = e.ExpenseDate.ToString("yyyy-MM-dd");
            ws.Cell(r, 3).Value = e.Category.ToString();
            ws.Cell(r, 4).Value = (double)e.Amount;
            ws.Cell(r, 4).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 5).Value = Sanitize(e.Description);
            ws.Cell(r, 6).Value = e.PaymentMethod.ToString();
            ws.Cell(r, 7).Value = Sanitize(e.ReferenceNumber);
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateStockMovementsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("StockMovements");
        string[] headers = { "Id", "MedicineId", "BatchId", "MovementDate", "MovementType", "Quantity", "BalanceAfter", "ReferenceType", "ReferenceId", "Notes" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _movementRepository.GetAllAsync();
        int r = 2;
        foreach (var m in list)
        {
            ws.Cell(r, 1).Value = m.Id;
            ws.Cell(r, 2).Value = m.MedicineId;
            ws.Cell(r, 3).Value = m.BatchId;
            ws.Cell(r, 4).Value = m.MovementDate.ToString("yyyy-MM-dd HH:mm");
            ws.Cell(r, 5).Value = m.MovementType.ToString();
            ws.Cell(r, 6).Value = m.Quantity;
            ws.Cell(r, 7).Value = m.BalanceAfter;
            ws.Cell(r, 8).Value = Sanitize(m.ReferenceType);
            ws.Cell(r, 9).Value = m.ReferenceId ?? 0;
            ws.Cell(r, 10).Value = Sanitize(m.Notes);
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateReturnsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Returns");
        string[] headers = { "Id", "ReturnType", "OriginalInvoice", "ReturnDate", "RefundAmount", "Reason" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _returnRepository.GetAllAsync();
        int r = 2;
        foreach (var rt in list)
        {
            ws.Cell(r, 1).Value = rt.Id;
            ws.Cell(r, 2).Value = rt.ReturnType.ToString();
            ws.Cell(r, 3).Value = Sanitize(rt.OriginalReference);
            ws.Cell(r, 4).Value = rt.ReturnDate.ToString("yyyy-MM-dd");
            ws.Cell(r, 5).Value = (double)rt.RefundAmount;
            ws.Cell(r, 5).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(r, 6).Value = Sanitize(rt.Reason);
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateAuditLogSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("AuditLog");
        string[] headers = { "Id", "Timestamp", "Username", "Action", "EntityType", "EntityId", "Reason" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _auditRepository.GetRecentAsync(500);
        int r = 2;
        foreach (var a in list)
        {
            ws.Cell(r, 1).Value = a.Id;
            ws.Cell(r, 2).Value = a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(r, 3).Value = Sanitize(a.Username);
            ws.Cell(r, 4).Value = Sanitize(a.Action);
            ws.Cell(r, 5).Value = Sanitize(a.EntityType);
            ws.Cell(r, 6).Value = a.EntityId ?? 0;
            ws.Cell(r, 7).Value = Sanitize(a.Reason);
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    private async Task PopulateAppSettingsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("AppSettings");
        string[] headers = { "Key", "Value", "Description", "UpdatedAt" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var list = await _settingsRepository.GetAllAsync();
        int r = 2;
        foreach (var s in list)
        {
            ws.Cell(r, 1).Value = Sanitize(s.Key);
            ws.Cell(r, 2).Value = Sanitize(s.Value);
            ws.Cell(r, 3).Value = Sanitize(s.Description);
            ws.Cell(r, 4).Value = s.UpdatedAt.ToString("yyyy-MM-dd HH:mm");
            r++;
        }
        StyleHeader(ws, headers.Length);
    }

    public Task<bool> IsWorkbookLockedAsync()
    {
        if (!File.Exists(_workbookPath))
            return Task.FromResult(false);

        try
        {
            using var stream = File.Open(_workbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult(false);
        }
        catch (IOException)
        {
            return Task.FromResult(true);
        }
    }

    public Task ChangeWorkbookPathAsync(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath))
            throw new ArgumentException("Workbook path cannot be empty", nameof(newPath));

        _workbookPath = newPath;
        var dir = Path.GetDirectoryName(_workbookPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return Task.CompletedTask;
    }
}
