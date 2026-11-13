using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Billing;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Auto-saved drafts (spec 02 §O): every change to an unsaved bill is stored as JSON; an emptied bill deletes its
/// draft; drafts older than the retention window (Billing.DraftRetentionDays, default 7) are purged on listing.
/// </summary>
public sealed class SaleDraftService : ISaleDraftService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ISaleDraftRepository _drafts;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuthService _auth;
    private readonly ILogger<SaleDraftService> _logger;
    private readonly Func<DateTime> _clock;

    public SaleDraftService(ISaleDraftRepository drafts, IAppSettingsRepository settings, IAuthService auth, ILogger<SaleDraftService> logger)
        : this(drafts, settings, auth, logger, () => DateTime.Now)
    {
    }

    public SaleDraftService(ISaleDraftRepository drafts, IAppSettingsRepository settings, IAuthService auth,
        ILogger<SaleDraftService> logger, Func<DateTime> clock)
    {
        _drafts = drafts;
        _settings = settings;
        _auth = auth;
        _logger = logger;
        _clock = clock;
    }

    public async Task<long?> SaveAsync(SalePostingRequest request, long? draftId, long netPayablePaisa)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EditingSaleId is not null) return draftId; // edits of posted sales are not drafts
        if (request.Lines.Count == 0)
        {
            if (draftId is > 0) await _drafts.DeleteAsync(draftId.Value);
            return null;
        }

        // Payment details are entered again on save; they are never kept in a draft.
        var content = request with { DraftId = null, Tenders = Array.Empty<SaleTender>(), CashTenderedPaisa = 0 };
        var summary = new SaleDraftSummary
        {
            CustomerName = request.IsCounterSale ? null : request.CustomerName,
            ItemCount = request.Lines.Count,
            TotalPaisa = netPayablePaisa,
            CreatedByUserId = request.UserId ?? _auth.CurrentUser?.Id
        };
        return await _drafts.SaveAsync(draftId, summary, JsonSerializer.Serialize(content, Json));
    }

    public async Task<IReadOnlyList<SaleDraftSummary>> ListAsync()
    {
        var cutoff = _clock().AddDays(-await RetentionDaysAsync());
        var purged = await _drafts.PurgeAsync(cutoff);
        if (purged > 0) _logger.LogInformation("Purged {Count} sale drafts older than {Cutoff}", purged, cutoff);
        return await _drafts.ListAsync(cutoff);
    }

    public async Task<SalePostingRequest?> LoadAsync(long draftId)
    {
        var draft = await _drafts.GetAsync(draftId);
        if (draft is null) return null;
        if (BillingCalculator.IsDraftExpired(draft.Summary.UpdatedAt, _clock(), await RetentionDaysAsync()))
        {
            await _drafts.DeleteAsync(draftId);
            return null;
        }
        try
        {
            var request = JsonSerializer.Deserialize<SalePostingRequest>(draft.Payload, Json);
            return request is null ? null : request with { DraftId = draftId, EditingSaleId = null };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Draft {DraftId} could not be read", draftId);
            throw new BillingValidationException("This draft could not be opened (its content is damaged).");
        }
    }

    public Task DeleteAsync(long draftId) => _drafts.DeleteAsync(draftId);

    private async Task<int> RetentionDaysAsync() =>
        int.TryParse(await _settings.GetValueAsync(BillingSettings.DraftRetentionDaysKey), out var days) ? Math.Clamp(days, 1, 90) : 7;
}

/// <summary>Customer master for the POS: validation (spec 02 §D), PAN from GSTIN, duplicate-mobile check, audit.</summary>
public sealed class BillingCustomerService : IBillingCustomerService
{
    private readonly IBillingCustomerRepository _customers;
    private readonly IAuditService _audit;
    private readonly ILogger<BillingCustomerService> _logger;

    public BillingCustomerService(IBillingCustomerRepository customers, IAuditService audit, ILogger<BillingCustomerService> logger)
    {
        _customers = customers;
        _audit = audit;
        _logger = logger;
    }

    public Task<IReadOnlyList<BillingCustomer>> SearchAsync(string? text) => _customers.SearchAsync(text, 25);

    public Task<BillingCustomer?> GetAsync(long id) => _customers.GetByIdAsync(id);

    public async Task<BillingCustomer> SaveAsync(BillingCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var errors = BillingCustomerRules.Validate(customer).ToList();
        if (errors.Count > 0) throw new BillingValidationException(errors);

        var normalized = BillingCustomerRules.Normalize(customer);
        if (normalized.Phone is { } phone)
        {
            var existing = await _customers.FindByPhoneAsync(phone);
            if (existing is not null && existing.Id != normalized.Id)
                throw new BillingValidationException($"Mobile number {phone} already belongs to customer '{existing.Name}'");
        }

        BillingCustomer? before = null;
        if (normalized.Id > 0)
        {
            before = await _customers.GetByIdAsync(normalized.Id) ?? throw new BillingValidationException("The customer no longer exists.");
            await _customers.UpdateAsync(normalized);
        }
        else
        {
            normalized.Id = await _customers.InsertAsync(normalized);
        }

        try
        {
            await _audit.LogAsync(before is null ? "CreateCustomer" : "UpdateCustomer", "Customer", normalized.Id,
                before is null ? null : JsonSerializer.Serialize(new { before.Name, before.Phone, before.Gstin, before.DefaultDiscountPercent, before.OpeningBalancePaisa }),
                JsonSerializer.Serialize(new { normalized.Name, normalized.Phone, normalized.Gstin, normalized.DefaultDiscountPercent, normalized.OpeningBalancePaisa }),
                "Customer saved from billing");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for customer {CustomerId}", normalized.Id);
        }

        return await _customers.GetByIdAsync(normalized.Id) ?? normalized;
    }

    public async Task DeactivateAsync(long id)
    {
        var customer = await _customers.GetByIdAsync(id) ?? throw new BillingValidationException("The customer no longer exists.");
        if (customer.OutstandingBalancePaisa != 0)
            throw new BillingValidationException($"'{customer.Name}' has a balance of ₹{customer.OutstandingBalance:N2}; settle it before deleting.");
        await _customers.DeactivateAsync(id);
        try
        {
            await _audit.LogAsync("DeactivateCustomer", "Customer", id, JsonSerializer.Serialize(new { customer.Name, customer.Phone }), null, "Customer deleted from billing");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for customer {CustomerId}", id);
        }
    }
}
