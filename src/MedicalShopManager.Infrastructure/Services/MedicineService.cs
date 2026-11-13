using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Medicine catalog management service.
/// </summary>
public class MedicineService : IMedicineService
{
    private readonly IMedicineRepository _medicineRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly IAuditService _auditService;
    private readonly IExcelSyncQueueRepository _excelSyncQueueRepository;
    private readonly ILogger<MedicineService> _logger;

    public MedicineService(
        IMedicineRepository medicineRepository,
        IBatchRepository batchRepository,
        IAuditService auditService,
        IExcelSyncQueueRepository excelSyncQueueRepository,
        ILogger<MedicineService> logger)
    {
        _medicineRepository = medicineRepository;
        _batchRepository = batchRepository;
        _auditService = auditService;
        _excelSyncQueueRepository = excelSyncQueueRepository;
        _logger = logger;
    }

    public async Task<Medicine?> GetByIdAsync(long id)
    {
        return await _medicineRepository.GetByIdAsync(id);
    }

    public async Task<Medicine?> GetWithBatchesAsync(long id)
    {
        var medicine = await _medicineRepository.GetByIdAsync(id);
        if (medicine == null)
            return null;

        var batches = await _batchRepository.GetByMedicineIdAsync(id);
        medicine.Batches = batches.ToList();
        return medicine;
    }

    public async Task<IReadOnlyList<Medicine>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await _medicineRepository.GetAllAsync();

        return await _medicineRepository.SearchAsync(query.Trim());
    }

    public async Task<Medicine?> GetByBarcodeAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return null;

        return await _medicineRepository.GetByBarcodeAsync(barcode.Trim());
    }

    public async Task<IReadOnlyList<Medicine>> GetAllAsync()
    {
        return await _medicineRepository.GetAllAsync();
    }

    public async Task<IReadOnlyList<Medicine>> GetLowStockAsync()
    {
        return await _medicineRepository.GetLowStockAsync();
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        return await _medicineRepository.GetCategoriesAsync();
    }

    /// <summary>
    /// Item-master rules shared by create and update. Returns null when valid, otherwise a user-facing message.
    /// </summary>
    public static string? Validate(Medicine medicine)
    {
        if (string.IsNullOrWhiteSpace(medicine.Name)) return "Medicine name is required.";
        if (medicine.UnitsPerPack < 1) return "Units per pack must be at least 1.";
        if (medicine.GstRatePercent is < 0 or > 28) return "GST % must be between 0 and 28.";
        if (medicine.MinStock < 0 || medicine.MaxStock < 0) return "Min and max stock cannot be negative.";
        if (medicine.MaxStock > 0 && medicine.MaxStock < medicine.MinStock) return "Max stock must be at least the min stock.";
        if (medicine.MaxDiscountPercent is < 0 or > 100) return "Max discount % must be between 0 and 100.";
        if (medicine.DefaultMrpPaisa < 0 || medicine.DefaultSellingPricePaisa < 0) return "Prices cannot be negative.";
        if (medicine.DefaultMrpPaisa > 0 && medicine.DefaultSellingPricePaisa > medicine.DefaultMrpPaisa)
            return "Sale rate cannot be more than the MRP.";
        if (!string.IsNullOrWhiteSpace(medicine.HsnCode))
        {
            var hsn = medicine.HsnCode.Trim();
            if (!hsn.All(char.IsAsciiDigit) || hsn.Length is not (4 or 6 or 8))
                return "HSN code must be 4, 6 or 8 digits.";
        }
        return null;
    }

    public async Task<long> CreateAsync(Medicine medicine)
    {
        var error = Validate(medicine);
        if (error != null)
            throw new ArgumentException(error, nameof(medicine));

        medicine.CreatedAt = DateTime.UtcNow;
        medicine.UpdatedAt = DateTime.UtcNow;

        var id = await _medicineRepository.InsertAsync(medicine);
        medicine.Id = id;

        await _auditService.LogAsync("CreateMedicine", "Medicine", id, null, 
            $"{{\"Name\":\"{medicine.Name}\",\"Category\":\"{medicine.Category}\"}}", "New medicine catalog entry");

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, id);
        _logger.LogInformation("Medicine created: {Name} (ID: {Id})", medicine.Name, id);

        return id;
    }

    public async Task<bool> UpdateAsync(Medicine medicine)
    {
        if (medicine.Id <= 0)
            throw new ArgumentException("Invalid medicine ID", nameof(medicine));
        var error = Validate(medicine);
        if (error != null)
            throw new ArgumentException(error, nameof(medicine));

        var existing = await _medicineRepository.GetByIdAsync(medicine.Id);
        if (existing == null)
            return false;

        medicine.UpdatedAt = DateTime.UtcNow;
        var updated = await _medicineRepository.UpdateAsync(medicine);

        if (updated)
        {
            await _auditService.LogAsync("UpdateMedicine", "Medicine", medicine.Id,
                $"{{\"Name\":\"{existing.Name}\",\"Price\":{existing.DefaultSellingPricePaisa}}}",
                $"{{\"Name\":\"{medicine.Name}\",\"Price\":{medicine.DefaultSellingPricePaisa}}}",
                "Medicine updated");

            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, medicine.Id);
            _logger.LogInformation("Medicine updated: {Name} (ID: {Id})", medicine.Name, medicine.Id);
        }

        return updated;
    }

    public async Task<bool> DeactivateAsync(long id)
    {
        var medicine = await _medicineRepository.GetByIdAsync(id);
        if (medicine == null)
            return false;

        medicine.Status = MedicineStatus.Inactive;
        medicine.UpdatedAt = DateTime.UtcNow;

        var result = await _medicineRepository.UpdateAsync(medicine);
        if (result)
        {
            await _auditService.LogAsync("DeactivateMedicine", "Medicine", id, null, null, "Medicine deactivated");
            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, id);
            _logger.LogInformation("Medicine deactivated: {Name} (ID: {Id})", medicine.Name, id);
        }

        return result;
    }
}
