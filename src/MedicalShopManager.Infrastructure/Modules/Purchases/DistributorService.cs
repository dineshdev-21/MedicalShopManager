using System.Text.Json;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>Distributor master: normalisation, validation, duplicate-GSTIN check, audit, statements.</summary>
public sealed class DistributorService : IDistributorService
{
    private readonly IDistributorRepository _repository;
    private readonly IAuditService _audit;
    private readonly IExcelSyncQueueRepository _excel;

    public DistributorService(IDistributorRepository repository, IAuditService audit, IExcelSyncQueueRepository excel)
    {
        _repository = repository;
        _audit = audit;
        _excel = excel;
    }

    public Task<IReadOnlyList<Distributor>> SearchAsync(string? query, bool includeInactive = false) => _repository.SearchAsync(query, includeInactive);

    public Task<Distributor?> GetAsync(long id) => _repository.GetByIdAsync(id);

    public async Task<long> SaveAsync(Distributor distributor)
    {
        ArgumentNullException.ThrowIfNull(distributor);
        Normalize(distributor);
        var errors = PurchaseValidator.ValidateDistributor(distributor).ToList();
        if (errors.Count == 0 && !string.IsNullOrWhiteSpace(distributor.Gstin) && await _repository.GstinExistsAsync(distributor.Gstin, distributor.Id))
            errors.Add($"Another distributor already uses GSTIN {distributor.Gstin}.");
        if (errors.Count > 0) throw new PurchaseValidationException(errors);

        var snapshot = JsonSerializer.Serialize(new
        {
            distributor.Name, distributor.Gstin, distributor.DrugLicenseNumber, distributor.Phone, distributor.StateCode,
            distributor.CreditDays, distributor.OpeningBalancePaisa
        });
        if (distributor.Id == 0)
        {
            distributor.Id = await _repository.InsertAsync(distributor);
            await _audit.LogAsync("CreateSupplier", "Supplier", distributor.Id, null, snapshot, "Distributor added");
        }
        else
        {
            var before = await _repository.GetByIdAsync(distributor.Id) ?? throw new PurchaseRuleException("This distributor no longer exists.");
            distributor.CreatedAt = before.CreatedAt;
            await _repository.UpdateAsync(distributor);
            await _audit.LogAsync("UpdateSupplier", "Supplier", distributor.Id,
                JsonSerializer.Serialize(new { before.Name, before.Gstin, before.CreditDays, before.OpeningBalancePaisa }), snapshot, "Distributor updated");
        }
        await _excel.EnqueueAsync(SyncEntityType.Supplier, distributor.Id);
        return distributor.Id;
    }

    public async Task SetActiveAsync(long id, bool isActive)
    {
        if (!await _repository.SetActiveAsync(id, isActive)) throw new PurchaseRuleException("This distributor no longer exists.");
        await _audit.LogAsync(isActive ? "ActivateSupplier" : "DeactivateSupplier", "Supplier", id, null, $"{{\"IsActive\":{(isActive ? "true" : "false")}}}",
            isActive ? "Distributor re-activated" : "Distributor deactivated (history kept)");
        await _excel.EnqueueAsync(SyncEntityType.Supplier, id);
    }

    public async Task<SupplierStatement> GetStatementAsync(long supplierId, DateTime from, DateTime to)
    {
        var supplier = await _repository.GetByIdAsync(supplierId) ?? throw new PurchaseRuleException("This distributor no longer exists.");
        var entries = await _repository.GetLedgerEntriesAsync(supplierId);
        return SupplierStatementBuilder.Build(supplier, entries, from, to);
    }

    /// <summary>Trims text, upper-cases the GSTIN, and fills the state code from the GSTIN when blank.</summary>
    internal static void Normalize(Distributor d)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        d.Name = d.Name?.Trim() ?? string.Empty;
        d.ContactPerson = Clean(d.ContactPerson);
        d.Phone = Clean(d.Phone);
        d.Email = Clean(d.Email);
        d.Address = Clean(d.Address);
        d.Pincode = Clean(d.Pincode);
        d.DrugLicenseNumber = Clean(d.DrugLicenseNumber)?.ToUpperInvariant();
        d.Gstin = Clean(d.Gstin)?.ToUpperInvariant();
        d.StateCode = Clean(d.StateCode);
        if (d.StateCode == null && d.Gstin is { Length: >= 2 } g && char.IsDigit(g[0]) && char.IsDigit(g[1])) d.StateCode = g[..2];
    }
}
