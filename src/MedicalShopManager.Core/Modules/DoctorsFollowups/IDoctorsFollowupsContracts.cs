namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// <b>For other modules (Billing's doctor field, Control Center's default doctor).</b> Look up and quick-add doctors of
/// the doctor master; store <see cref="DoctorLookupItem.Id"/> in Sales.DoctorId and the name / reg. no as the bill's
/// snapshot. Resolve from DI; implemented by the DoctorsFollowups module.
/// </summary>
public interface IDoctorLookup
{
    /// <summary>Active doctors whose name, registration no, phone, clinic or specialization contains the text (all when blank).</summary>
    Task<IReadOnlyList<DoctorLookupItem>> FindDoctorsAsync(string? text, int limit = 25);

    /// <summary>The doctor (active or not), or null.</summary>
    Task<DoctorLookupItem?> GetDoctorAsync(long id);

    /// <summary>
    /// Adds a doctor from another screen, or returns the existing one with the same registration number (or, without a
    /// registration number, the same name and phone).
    /// </summary>
    /// <exception cref="DoctorsFollowupsValidationException">Invalid name, registration number or phone.</exception>
    Task<DoctorLookupItem> QuickAddDoctorAsync(DoctorQuickAdd doctor);
}

/// <summary>
/// <b>For the dashboard (DashboardSearch module).</b> The Refill Reminders card: due reminders (overdue, today,
/// upcoming) and the counts. Open the follow-ups page with <see cref="DoctorsFollowupsPages.Followups"/> and a
/// <see cref="OpenFollowupsRequest"/>.
/// </summary>
public interface IRefillReminderFeed
{
    /// <summary>Pending reminders in the Arriving tab, most urgent first.</summary>
    Task<IReadOnlyList<PatientFollowup>> GetDueRemindersAsync(int limit = 10);

    Task<FollowupCounts> GetCountsAsync();
}

/// <summary>Doctors table (parameterised SQL; doctors are deactivated, never deleted).</summary>
public interface IDoctorRepository
{
    Task<IReadOnlyList<Doctor>> SearchAsync(string? text, bool includeInactive, int limit = 500);
    Task<Doctor?> GetAsync(long id);

    /// <summary>A doctor with this registration number (case-insensitive), other than <paramref name="excludeId"/>.</summary>
    Task<Doctor?> FindByRegistrationNoAsync(string registrationNo, long? excludeId = null);

    /// <summary>A doctor with the same name (case-insensitive) and phone (both may be blank).</summary>
    Task<Doctor?> FindByNameAndPhoneAsync(string name, string? phone);

    Task<long> InsertAsync(Doctor doctor);
    Task<bool> UpdateAsync(Doctor doctor);
    Task<bool> SetActiveAsync(long id, bool isActive);

    /// <summary>Per doctor: active bills, patients, bill totals, taxable and cost of the period (both dates inclusive).</summary>
    Task<IReadOnlyList<DoctorSalesRow>> GetSalesSummaryAsync(DateTime from, DateTime to, bool includeUnlinked);

    /// <summary>Active bills of a doctor (by id, or by typed name for unlinked bills) in the period.</summary>
    Task<IReadOnlyList<DoctorSaleBill>> GetBillsAsync(long? doctorId, string? doctorName, DateTime from, DateTime to);
}

/// <summary>PatientFollowups / FollowupNotes / FollowupRefillRules (parameterised SQL; multi-row changes in one transaction).</summary>
public interface IFollowupRepository
{
    /// <summary>Lines of active bills of identified customers since <paramref name="from"/> whose item is marked for refill reminders.</summary>
    Task<IReadOnlyList<FollowupSaleLine>> GetChronicSaleLinesAsync(DateTime from);

    /// <summary>Pending follow-ups (any date) plus every follow-up of a bill dated on or after <paramref name="from"/>.</summary>
    Task<IReadOnlyList<ExistingFollowup>> GetForGenerationAsync(DateTime from);

    /// <summary>Applies a generation plan in one transaction (inserts, updates, closures); rollback leaves no trace.</summary>
    Task ApplyPlanAsync(FollowupPlan plan, long? userId, string? userName, DateTime now);

    /// <summary>Follow-ups for the list (status Pending, or Done for the Actioned tab), unevaluated.</summary>
    Task<IReadOnlyList<PatientFollowup>> QueryAsync(FollowupQuery query);

    Task<PatientFollowup?> GetAsync(long id);
    Task<IReadOnlyList<PatientFollowup>> GetManyAsync(IReadOnlyCollection<long> ids);
    Task<long> InsertAsync(NewFollowup followup, long? userId, string? userName, DateTime now);

    /// <summary>Sets the status of pending follow-ups (Done / Dismissed) and adds the note to each, in one transaction; returns rows changed.</summary>
    Task<int> CloseAsync(IReadOnlyCollection<long> ids, FollowupStatus status, string? reason, string? note, long? userId, string? userName, DateTime now);

    /// <summary>Back to pending (undo Done / Dismissed).</summary>
    Task<bool> ReopenAsync(long id, DateTime now);

    Task<bool> SnoozeAsync(long id, DateTime? until, DateTime now);
    Task<bool> SetHelpfulAsync(long id, bool? isHelpful, DateTime now);
    Task<bool> UpdateConsumptionAsync(long id, decimal unitsPerDay, DateTime nextRefillDate, DateTime now);

    Task<IReadOnlyList<FollowupNote>> GetNotesAsync(long followupId);
    Task<FollowupNote?> GetNoteAsync(long noteId);
    Task<long> AddNoteAsync(long followupId, string note, long? userId, string? userName, DateTime now);
    Task<bool> UpdateNoteAsync(long noteId, string note, DateTime now);
    Task<bool> DeleteNoteAsync(long noteId, DateTime now);

    /// <summary>Lines of active bills of identified customers since <paramref name="from"/> (for recommendations).</summary>
    Task<IReadOnlyList<FollowupPurchaseRow>> GetPurchaseHistoryAsync(DateTime from);

    /// <summary>(customer, patient key, item key) of every pending follow-up.</summary>
    Task<IReadOnlySet<(long CustomerId, string PatientKey, string ItemKey)>> GetPendingKeysAsync();

    Task<CustomerFollowupSummary?> GetCustomerSummaryAsync(long customerId);
    Task<IReadOnlyList<FollowupCustomerOption>> SearchCustomersAsync(string? text, int limit = 25);
    Task<IReadOnlyList<FollowupItemOption>> SearchItemsAsync(string? text, int limit = 30);
    Task<FollowupItemOption?> GetItemAsync(long medicineId);

    /// <summary>Compositions (and medicines without a composition) matching the text, with their effective rule.</summary>
    Task<IReadOnlyList<RefillRule>> SearchRulesAsync(string? text, bool chronicOnly, int limit = 200);

    /// <summary>Inserts or replaces the module's rule for one composition or medicine.</summary>
    Task SaveRuleAsync(RefillRule rule, DateTime now);
}

/// <summary>Doctor master and doctor-wise sales.</summary>
public interface IDoctorService : IDoctorLookup
{
    Task<IReadOnlyList<Doctor>> SearchAsync(string? text, bool includeInactive);
    Task<Doctor?> GetAsync(long id);

    /// <summary>Normalises, validates (including a unique registration number), saves and audits; returns the id.</summary>
    /// <exception cref="DoctorsFollowupsValidationException">Invalid doctor.</exception>
    Task<long> SaveAsync(Doctor doctor);

    Task SetActiveAsync(long id, bool isActive);

    /// <exception cref="DoctorsFollowupsValidationException">Invalid period.</exception>
    Task<DoctorSalesSummary> GetSalesSummaryAsync(DateTime from, DateTime to, bool includeUnlinked);

    Task<IReadOnlyList<DoctorSaleBill>> GetBillsAsync(DoctorSalesRow row, DateTime from, DateTime to);
}

/// <summary>Patient follow-ups / refill reminders.</summary>
public interface IFollowupService : IRefillReminderFeed
{
    Task<FollowupSettings> GetSettingsAsync();

    /// <exception cref="DoctorsFollowupsValidationException">Invalid settings.</exception>
    Task SaveSettingsAsync(FollowupSettings settings);

    /// <summary>
    /// Creates / updates follow-ups from recent bills of chronic medicines (skipped when Control Center's
    /// "Patient reminders" sale setting is off). Safe to run repeatedly.
    /// </summary>
    Task<FollowupGenerationResult> GenerateFromSalesAsync();

    /// <summary>Whether Control Center's "Patient reminders" sale setting is on.</summary>
    Task<bool> IsEnabledAsync();

    Task<FollowupListResult> GetListAsync(FollowupQuery query);
    Task<PatientFollowup?> GetAsync(long id);

    /// <exception cref="DoctorsFollowupsValidationException">Nothing pending among <paramref name="ids"/>.</exception>
    Task<int> MarkDoneAsync(IReadOnlyCollection<long> ids, string? note);

    /// <exception cref="DoctorsFollowupsValidationException">No reason, or nothing pending.</exception>
    Task<int> DismissAsync(IReadOnlyCollection<long> ids, string reason);

    Task ReopenAsync(long id);

    /// <exception cref="DoctorsFollowupsValidationException">Date not after today or too far ahead.</exception>
    Task SnoozeAsync(long id, DateTime until);

    Task ClearSnoozeAsync(long id);
    Task SetHelpfulAsync(long id, bool? isHelpful);

    /// <summary>Changes the consumption and recomputes the next refill date (kept on later regeneration).</summary>
    Task<PatientFollowup> UpdateConsumptionAsync(long id, decimal unitsPerDay);

    /// <exception cref="DoctorsFollowupsValidationException">Invalid request.</exception>
    Task<long> CreateManualAsync(ManualFollowupRequest request);

    Task<IReadOnlyList<FollowupNote>> GetNotesAsync(long followupId);
    Task AddNoteAsync(long followupId, string note);

    /// <exception cref="DoctorsFollowupsValidationException">Not the author, or blank text.</exception>
    Task UpdateNoteAsync(long noteId, string note);

    Task DeleteNoteAsync(long noteId);

    /// <summary>Message and links for the selected follow-ups of one patient.</summary>
    /// <exception cref="DoctorsFollowupsValidationException">Nothing selected or several patients.</exception>
    Task<FollowupMessage> BuildMessageAsync(IReadOnlyCollection<long> ids);

    Task<IReadOnlyList<RefillRecommendation>> GetRecommendationsAsync();

    /// <summary>Creates the follow-up; with <paramref name="alwaysRemind"/> also marks the item for refill reminders.</summary>
    Task<long> AcceptRecommendationAsync(RefillRecommendation recommendation, bool alwaysRemind);

    Task<CustomerFollowupSummary?> GetCustomerSummaryAsync(long customerId);
    Task<IReadOnlyList<FollowupCustomerOption>> SearchCustomersAsync(string? text);
    Task<IReadOnlyList<FollowupItemOption>> SearchItemsAsync(string? text);
    Task<IReadOnlyList<RefillRule>> SearchRulesAsync(string? text, bool chronicOnly);

    /// <exception cref="DoctorsFollowupsValidationException">Invalid rule.</exception>
    Task SaveRuleAsync(RefillRule rule);
}

/// <summary>A user-facing validation failure in the DoctorsFollowups module.</summary>
public sealed class DoctorsFollowupsValidationException : Exception
{
    public DoctorsFollowupsValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Validation failed." : string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public DoctorsFollowupsValidationException(string error) : this(new[] { error }) { }

    public IReadOnlyList<string> Errors { get; }
}
