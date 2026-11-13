using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>
/// Doctor master (rules in <see cref="DoctorRules"/>), the <see cref="IDoctorLookup"/> other modules use, and the
/// doctor-wise sales report (commission via <see cref="DoctorSalesCalculator"/> and so PricingCalculator).
/// Every create / update / (de)activation is written to the audit log (commission % is money-relevant).
/// </summary>
public sealed class DoctorService : IDoctorService
{
    private readonly IDoctorRepository _repository;
    private readonly IAuditService _audit;
    private readonly ILogger<DoctorService> _logger;

    public DoctorService(IDoctorRepository repository, IAuditService audit, ILogger<DoctorService> logger)
    {
        _repository = repository;
        _audit = audit;
        _logger = logger;
    }

    public Task<IReadOnlyList<Doctor>> SearchAsync(string? text, bool includeInactive) =>
        _repository.SearchAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim(), includeInactive);

    public Task<Doctor?> GetAsync(long id) => _repository.GetAsync(id);

    public async Task<long> SaveAsync(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        var normalized = DoctorRules.Normalize(doctor);
        var errors = DoctorRules.Validate(normalized).ToList();
        if (normalized.RegistrationNo is { } reg && errors.Count == 0)
        {
            var other = await _repository.FindByRegistrationNoAsync(reg, normalized.Id == 0 ? null : normalized.Id);
            if (other is not null) errors.Add($"Registration no. {reg} is already used by {other.DisplayName}.");
        }
        if (errors.Count > 0) throw new DoctorsFollowupsValidationException(errors);

        if (normalized.Id == 0)
        {
            var id = await _repository.InsertAsync(normalized);
            await AuditAsync("CreateDoctor", id, null, Snapshot(normalized), $"Doctor {normalized.DisplayName} added");
            return id;
        }

        var before = await _repository.GetAsync(normalized.Id)
                     ?? throw new DoctorsFollowupsValidationException("The doctor no longer exists.");
        if (!await _repository.UpdateAsync(normalized)) throw new DoctorsFollowupsValidationException("The doctor no longer exists.");
        var reason = before.CommissionPercent != normalized.CommissionPercent || before.CommissionOnProfit != normalized.CommissionOnProfit
            ? $"Doctor {normalized.DisplayName} updated; commission {before.CommissionLabel} → {normalized.CommissionLabel}"
            : $"Doctor {normalized.DisplayName} updated";
        await AuditAsync("UpdateDoctor", normalized.Id, Snapshot(before), Snapshot(normalized), reason);
        return normalized.Id;
    }

    public async Task SetActiveAsync(long id, bool isActive)
    {
        var doctor = await _repository.GetAsync(id) ?? throw new DoctorsFollowupsValidationException("The doctor no longer exists.");
        if (doctor.IsActive == isActive) return;
        await _repository.SetActiveAsync(id, isActive);
        await AuditAsync(isActive ? "ActivateDoctor" : "DeactivateDoctor", id,
            JsonSerializer.Serialize(new { doctor.IsActive }), JsonSerializer.Serialize(new { IsActive = isActive }),
            $"Doctor {doctor.DisplayName} {(isActive ? "activated" : "deactivated")}");
    }

    public async Task<DoctorSalesSummary> GetSalesSummaryAsync(DateTime from, DateTime to, bool includeUnlinked)
    {
        if (DoctorSalesCalculator.ValidateRange(from, to) is { } error) throw new DoctorsFollowupsValidationException(error);
        var rows = await _repository.GetSalesSummaryAsync(from.Date, to.Date, includeUnlinked);
        return DoctorSalesCalculator.Build(from, to, rows);
    }

    public Task<IReadOnlyList<DoctorSaleBill>> GetBillsAsync(DoctorSalesRow row, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _repository.GetBillsAsync(row.DoctorId, row.IsLinked ? null : row.DoctorName, from.Date, to.Date);
    }

    // ───────────────────────── IDoctorLookup ─────────────────────────

    public async Task<IReadOnlyList<DoctorLookupItem>> FindDoctorsAsync(string? text, int limit = 25) =>
        (await _repository.SearchAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim(), false, Math.Clamp(limit, 1, 500)))
        .Select(d => d.ToLookupItem())
        .ToList();

    public async Task<DoctorLookupItem?> GetDoctorAsync(long id) => (await _repository.GetAsync(id))?.ToLookupItem();

    public async Task<DoctorLookupItem> QuickAddDoctorAsync(DoctorQuickAdd doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        var normalized = DoctorRules.Normalize(DoctorRules.FromQuickAdd(doctor));
        var errors = DoctorRules.Validate(normalized);
        if (errors.Count > 0) throw new DoctorsFollowupsValidationException(errors);

        var existing = normalized.RegistrationNo is { } reg
            ? await _repository.FindByRegistrationNoAsync(reg)
            : await _repository.FindByNameAndPhoneAsync(normalized.Name, normalized.Phone);
        if (existing is not null)
        {
            if (!existing.IsActive) await SetActiveAsync(existing.Id, true);
            return existing.ToLookupItem() with { IsActive = true };
        }

        var id = await _repository.InsertAsync(normalized);
        await AuditAsync("CreateDoctor", id, null, Snapshot(normalized), $"Doctor {normalized.DisplayName} added from another screen");
        return (normalized with { Id = id }).ToLookupItem();
    }

    private static string Snapshot(Doctor d) => JsonSerializer.Serialize(new
    {
        d.Name,
        d.RegistrationNo,
        d.Qualification,
        d.Specialization,
        d.ClinicName,
        d.Phone,
        d.City,
        d.CommissionPercent,
        d.CommissionOnProfit,
        d.IsActive
    });

    private async Task AuditAsync(string action, long id, string? oldValues, string? newValues, string reason)
    {
        try
        {
            await _audit.LogAsync(action, "Doctor", id, oldValues, newValues, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for {Action} on doctor {Id}", action, id);
        }
    }
}
