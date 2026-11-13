using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>
/// Patient follow-ups / refill reminders. Follow-ups come from bills of chronic medicines
/// (<see cref="FollowupGenerationPlanner"/>, applied in one transaction) while Control Center's "Patient reminders"
/// sale setting is on; maths in <see cref="RefillCalculator"/>, suggestions in <see cref="RefillRecommender"/>, message
/// text in <see cref="FollowupMessageBuilder"/>. User actions (followed up, dismissed, reopened, consumption, manual
/// follow-ups, refill rules, settings) are audited.
/// </summary>
public sealed class FollowupService : IFollowupService
{
    /// <summary>Bills of this many days feed the recommended reminders.</summary>
    public const int RecommendationLookbackDays = 180;

    public const int MaxReasonLength = 200;

    private readonly IFollowupRepository _repository;
    private readonly IAppSettingsRepository _settings;
    private readonly IControlCenterSettingsService _controlCenter;
    private readonly IAuthService _auth;
    private readonly IAuditService _audit;
    private readonly ILogger<FollowupService> _logger;
    private readonly Func<DateTime> _now;

    public FollowupService(IFollowupRepository repository, IAppSettingsRepository settings, IControlCenterSettingsService controlCenter,
        IAuthService auth, IAuditService audit, ILogger<FollowupService> logger)
        : this(repository, settings, controlCenter, auth, audit, logger, () => DateTime.Now)
    {
    }

    /// <summary>Test constructor with a fixed clock.</summary>
    public FollowupService(IFollowupRepository repository, IAppSettingsRepository settings, IControlCenterSettingsService controlCenter,
        IAuthService auth, IAuditService audit, ILogger<FollowupService> logger, Func<DateTime> now)
    {
        _repository = repository;
        _settings = settings;
        _controlCenter = controlCenter;
        _auth = auth;
        _audit = audit;
        _logger = logger;
        _now = now;
    }

    private DateTime Today => _now().Date;
    private long? UserId => _auth.CurrentUser?.Id;
    private string? UserName => _auth.CurrentUser is { } u ? (string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName) : null;

    // ───────────────────────── Settings ─────────────────────────

    public async Task<FollowupSettings> GetSettingsAsync()
    {
        var values = new Dictionary<string, string?>();
        foreach (var key in FollowupSettings.Default.ToValues().Keys) values[key] = await _settings.GetValueAsync(key);
        return FollowupSettings.FromValues(key => values.GetValueOrDefault(key));
    }

    public async Task SaveSettingsAsync(FollowupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new DoctorsFollowupsValidationException(errors);
        var before = await GetSettingsAsync();
        foreach (var (key, value) in settings.ToValues()) await _settings.SetValueAsync(key, value);
        await AuditAsync("SaveFollowupSettings", null, JsonSerializer.Serialize(before), JsonSerializer.Serialize(settings),
            "Refill follow-up settings changed");
    }

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            return (await _controlCenter.GetSaleSettingsAsync()).PatientReminders;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the Patient reminders sale setting failed; follow-ups stay on");
            return true;
        }
    }

    // ───────────────────────── Generation and list ─────────────────────────

    public async Task<FollowupGenerationResult> GenerateFromSalesAsync()
    {
        if (!await IsEnabledAsync()) return FollowupGenerationResult.None;
        var settings = await GetSettingsAsync();
        var from = Today.AddDays(-settings.LookbackDays);
        var lines = await _repository.GetChronicSaleLinesAsync(from);
        var existing = await _repository.GetForGenerationAsync(from);
        var plan = FollowupGenerationPlanner.Plan(lines, existing, settings.DefaultUnitsPerDay, from);
        if (plan.IsEmpty) return FollowupGenerationResult.None;

        await _repository.ApplyPlanAsync(plan, UserId, UserName, _now());
        var result = FollowupGenerationResult.From(plan);
        _logger.LogInformation("Refill follow-ups from bills: {Summary}", result.Summary);
        return result;
    }

    public async Task<FollowupListResult> GetListAsync(FollowupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var settings = await GetSettingsAsync();
        var today = Today;
        var pending = (await _repository.QueryAsync(query with { Tab = FollowupTab.AllPending, Limit = 5000 }))
            .Select(f => RefillCalculator.Evaluate(f, today, settings)).ToList();
        var done = (await _repository.QueryAsync(query with { Tab = FollowupTab.Actioned, Limit = 500 }))
            .Select(f => RefillCalculator.Evaluate(f, today, settings)).ToList();

        var counts = RefillCalculator.Count(pending.Concat(done));
        var items = query.Tab == FollowupTab.Actioned
            ? done
            : pending.Where(f => RefillCalculator.IsInTab(f.Bucket, f.Status, query.Tab)).ToList();
        var sorted = RefillCalculator.Sort(items, query.Sort).Take(Math.Clamp(query.Limit, 1, 5000)).ToList();
        return new FollowupListResult(sorted, counts);
    }

    public async Task<PatientFollowup?> GetAsync(long id)
    {
        var followup = await _repository.GetAsync(id);
        return followup is null ? null : RefillCalculator.Evaluate(followup, Today, await GetSettingsAsync());
    }

    // ───────────────────────── Dashboard feed ─────────────────────────

    public async Task<IReadOnlyList<PatientFollowup>> GetDueRemindersAsync(int limit = 10)
    {
        await TryGenerateAsync();
        var list = await GetListAsync(new FollowupQuery { Tab = FollowupTab.Arriving, Sort = FollowupSort.FollowupDateAscending, Limit = Math.Max(1, limit) });
        return list.Items;
    }

    public async Task<FollowupCounts> GetCountsAsync()
    {
        await TryGenerateAsync();
        return (await GetListAsync(new FollowupQuery { Tab = FollowupTab.Arriving, Limit = 1 })).Counts;
    }

    private async Task TryGenerateAsync()
    {
        try
        {
            await GenerateFromSalesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating refill follow-ups from bills failed");
        }
    }

    // ───────────────────────── Actions ─────────────────────────

    public async Task<int> MarkDoneAsync(IReadOnlyCollection<long> ids, string? note)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new DoctorsFollowupsValidationException("Please select a follow-up to continue.");
        if (note is { Length: > FollowupKeys.MaxNoteLength })
            throw new DoctorsFollowupsValidationException($"A remark can have at most {FollowupKeys.MaxNoteLength} characters.");

        var changed = await _repository.CloseAsync(ids, FollowupStatus.Done, null, note, UserId, UserName, _now());
        if (changed == 0) throw new DoctorsFollowupsValidationException("The selected follow-ups are no longer pending.");
        await AuditAsync("FollowupDone", ids.Count == 1 ? ids.First() : null, null, JsonSerializer.Serialize(new { Ids = ids, Note = note }),
            $"{changed} refill follow-up(s) marked as followed up");
        return changed;
    }

    public async Task<int> DismissAsync(IReadOnlyCollection<long> ids, string reason)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new DoctorsFollowupsValidationException("Please select a follow-up to continue.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DoctorsFollowupsValidationException("Enter why the reminder is no longer needed.");
        if (reason.Trim().Length > MaxReasonLength)
            throw new DoctorsFollowupsValidationException($"The reason can have at most {MaxReasonLength} characters.");

        var changed = await _repository.CloseAsync(ids, FollowupStatus.Dismissed, reason.Trim(), null, UserId, UserName, _now());
        if (changed == 0) throw new DoctorsFollowupsValidationException("The selected follow-ups are no longer pending.");
        await AuditAsync("FollowupDismissed", ids.Count == 1 ? ids.First() : null, null, JsonSerializer.Serialize(new { Ids = ids }),
            $"{changed} refill follow-up(s) dismissed: {reason.Trim()}");
        return changed;
    }

    public async Task ReopenAsync(long id)
    {
        if (!await _repository.ReopenAsync(id, _now()))
            throw new DoctorsFollowupsValidationException("Only followed-up or dismissed reminders can be reopened.");
        await AuditAsync("FollowupReopened", id, null, null, "Refill follow-up reopened");
    }

    public async Task SnoozeAsync(long id, DateTime until)
    {
        if (RefillCalculator.ValidateSnooze(until, Today) is { } error) throw new DoctorsFollowupsValidationException(error);
        if (!await _repository.SnoozeAsync(id, until.Date, _now()))
            throw new DoctorsFollowupsValidationException("Only pending reminders can be snoozed.");
    }

    public async Task ClearSnoozeAsync(long id)
    {
        if (!await _repository.SnoozeAsync(id, null, _now()))
            throw new DoctorsFollowupsValidationException("Only pending reminders can be changed.");
    }

    public async Task SetHelpfulAsync(long id, bool? isHelpful)
    {
        if (!await _repository.SetHelpfulAsync(id, isHelpful, _now()))
            throw new DoctorsFollowupsValidationException("The follow-up no longer exists.");
    }

    public async Task<PatientFollowup> UpdateConsumptionAsync(long id, decimal unitsPerDay)
    {
        if (RefillCalculator.ValidateUnitsPerDay(unitsPerDay) is { } error) throw new DoctorsFollowupsValidationException(error);
        var followup = await _repository.GetAsync(id) ?? throw new DoctorsFollowupsValidationException("The follow-up no longer exists.");
        if (!followup.IsPending) throw new DoctorsFollowupsValidationException("Only pending reminders can be changed.");

        var next = RefillCalculator.NextRefillDate(followup.SaleDate, followup.QuantityUnits, unitsPerDay);
        if (!await _repository.UpdateConsumptionAsync(id, unitsPerDay, next, _now()))
            throw new DoctorsFollowupsValidationException("Only pending reminders can be changed.");
        await AuditAsync("FollowupConsumption", id,
            JsonSerializer.Serialize(new { followup.UnitsPerDay, followup.NextRefillDate }),
            JsonSerializer.Serialize(new { UnitsPerDay = unitsPerDay, NextRefillDate = next }),
            $"Consumption of {followup.MedicineName} for {followup.PatientDisplay} set to {unitsPerDay:0.##} unit(s)/day");
        return await GetAsync(id) ?? followup;
    }

    public async Task<long> CreateManualAsync(ManualFollowupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = RefillCalculator.ValidateManual(request, Today).ToList();
        if (errors.Count > 0) throw new DoctorsFollowupsValidationException(errors);

        var customer = await _repository.GetCustomerSummaryAsync(request.CustomerId)
                       ?? throw new DoctorsFollowupsValidationException("The customer no longer exists.");
        var item = await _repository.GetItemAsync(request.MedicineId)
                   ?? throw new DoctorsFollowupsValidationException("The medicine no longer exists.");
        var patientKey = FollowupKeys.PatientKey(request.PatientName, customer.CustomerName);
        var itemKey = FollowupKeys.ItemKey(item.CompositionId, item.MedicineId);
        if ((await _repository.GetPendingKeysAsync()).Contains((customer.CustomerId, patientKey, itemKey)))
            throw new DoctorsFollowupsValidationException($"{customer.CustomerName} already has a pending reminder for {item.Label}.");

        var followup = new NewFollowup
        {
            CustomerId = customer.CustomerId,
            PatientName = patientKey.Length == 0 ? null : request.PatientName?.Trim(),
            PatientKey = patientKey,
            ItemKey = itemKey,
            MedicineId = item.MedicineId,
            MedicineName = item.Name,
            CompositionId = item.CompositionId,
            CompositionName = item.CompositionName,
            SaleDate = request.StartDate.Date,
            QuantityUnits = request.QuantityUnits,
            UnitsPerDay = request.UnitsPerDay,
            UnitsPerDayIsManual = true,
            NextRefillDate = RefillCalculator.NextRefillDate(request.StartDate, request.QuantityUnits, request.UnitsPerDay),
            Source = request.Source == FollowupSource.Sale ? FollowupSource.Manual : request.Source,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };
        var id = await _repository.InsertAsync(followup, UserId, UserName, _now());
        await AuditAsync("CreateFollowup", id, null,
            JsonSerializer.Serialize(new { followup.CustomerId, followup.MedicineId, followup.QuantityUnits, followup.UnitsPerDay, followup.NextRefillDate }),
            $"Refill follow-up for {customer.CustomerName}: {item.Name}, due {followup.NextRefillDate:dd-MM-yyyy}");
        return id;
    }

    // ───────────────────────── Notes ─────────────────────────

    public Task<IReadOnlyList<FollowupNote>> GetNotesAsync(long followupId) => _repository.GetNotesAsync(followupId);

    public async Task AddNoteAsync(long followupId, string note)
    {
        var text = ValidateNote(note);
        _ = await _repository.GetAsync(followupId) ?? throw new DoctorsFollowupsValidationException("The follow-up no longer exists.");
        await _repository.AddNoteAsync(followupId, text, UserId, UserName, _now());
    }

    public async Task UpdateNoteAsync(long noteId, string note)
    {
        var text = ValidateNote(note);
        await EnsureNoteAuthorAsync(noteId);
        await _repository.UpdateNoteAsync(noteId, text, _now());
    }

    public async Task DeleteNoteAsync(long noteId)
    {
        await EnsureNoteAuthorAsync(noteId);
        await _repository.DeleteNoteAsync(noteId, _now());
    }

    private static string ValidateNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new DoctorsFollowupsValidationException("Type a remark first.");
        var text = note.Trim();
        if (text.Length > FollowupKeys.MaxNoteLength)
            throw new DoctorsFollowupsValidationException($"A remark can have at most {FollowupKeys.MaxNoteLength} characters.");
        return text;
    }

    /// <summary>Only the author (or an administrator) edits or deletes a remark (Localwell: the author's own notes).</summary>
    private async Task EnsureNoteAuthorAsync(long noteId)
    {
        var existing = await _repository.GetNoteAsync(noteId) ?? throw new DoctorsFollowupsValidationException("The remark no longer exists.");
        var user = _auth.CurrentUser;
        var isAuthor = existing.UserId is null || user?.Id == existing.UserId;
        if (!isAuthor && user?.Role != UserRole.Administrator)
            throw new DoctorsFollowupsValidationException("You can change only your own remarks.");
    }

    // ───────────────────────── Message, recommendations, look-ups ─────────────────────────

    public async Task<FollowupMessage> BuildMessageAsync(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var settings = await GetSettingsAsync();
        var selected = (await _repository.GetManyAsync(ids)).Select(f => RefillCalculator.Evaluate(f, Today, settings)).ToList();
        BusinessProfile profile;
        try
        {
            profile = await _controlCenter.GetBusinessProfileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the business profile failed; the reminder uses a generic shop name");
            profile = new BusinessProfile();
        }
        var (message, error) = FollowupMessageBuilder.Build(selected, profile.ShopName, profile.Phone);
        return message ?? throw new DoctorsFollowupsValidationException(error ?? "Please select a follow-up to continue.");
    }

    public async Task<IReadOnlyList<RefillRecommendation>> GetRecommendationsAsync()
    {
        var history = await _repository.GetPurchaseHistoryAsync(Today.AddDays(-RecommendationLookbackDays));
        var pending = await _repository.GetPendingKeysAsync();
        return RefillRecommender.Recommend(history, pending, Today);
    }

    public async Task<long> AcceptRecommendationAsync(RefillRecommendation recommendation, bool alwaysRemind)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var id = await CreateManualAsync(RefillRecommender.ToRequest(recommendation));
        if (alwaysRemind)
        {
            await SaveRuleAsync(recommendation.CompositionId is { } compositionId
                ? new RefillRule { CompositionId = compositionId, Name = recommendation.CompositionName ?? recommendation.MedicineName, RuleIsChronic = true }
                : new RefillRule { MedicineId = recommendation.MedicineId, Name = recommendation.MedicineName, RuleIsChronic = true });
        }
        return id;
    }

    public Task<CustomerFollowupSummary?> GetCustomerSummaryAsync(long customerId) => _repository.GetCustomerSummaryAsync(customerId);

    public Task<IReadOnlyList<FollowupCustomerOption>> SearchCustomersAsync(string? text) =>
        _repository.SearchCustomersAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim());

    public Task<IReadOnlyList<FollowupItemOption>> SearchItemsAsync(string? text) =>
        _repository.SearchItemsAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim());

    public Task<IReadOnlyList<RefillRule>> SearchRulesAsync(string? text, bool chronicOnly) =>
        _repository.SearchRulesAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim(), chronicOnly);

    public async Task SaveRuleAsync(RefillRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = rule.Validate();
        if (errors.Count > 0) throw new DoctorsFollowupsValidationException(errors);
        await _repository.SaveRuleAsync(rule, _now());
        await AuditAsync("SaveRefillRule", rule.CompositionId ?? rule.MedicineId, null,
            JsonSerializer.Serialize(new { rule.CompositionId, rule.MedicineId, rule.RuleIsChronic, rule.UnitsPerDay }),
            $"Refill reminders for {rule.Kind.ToLowerInvariant()} '{rule.Name}': {(rule.IsChronic ? "on" : "off")}, consumption {rule.ConsumptionLabel}");
    }

    private async Task AuditAsync(string action, long? id, string? oldValues, string? newValues, string reason)
    {
        try
        {
            await _audit.LogAsync(action, "PatientFollowup", id, oldValues, newValues, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit log failed for {Action} on follow-up {Id}", action, id);
        }
    }
}
