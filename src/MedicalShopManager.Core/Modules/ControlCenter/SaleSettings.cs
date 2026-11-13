using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Control Center ▸ Sale Settings (Localwell <c>business_get_sale_settings</c> / <c>business_sync_sale_settings</c>;
/// keys seen in the bundle: <c>new_sale_is_credit</c>, <c>set_due_date</c>, <c>doctor</c> (default doctor),
/// <c>patient_reminders</c>, <c>expiry_cutoff</c>, <c>add_scheme_wrt_sale_qty</c>, <c>barcode_qty_in_packs</c>, and the
/// "Show Near Expiry &lt; 3/6/9/12 months" additional setting). Billing reads these through
/// <see cref="IControlCenterSettingsService.GetSaleSettingsAsync"/> and applies them with <see cref="SaleSettingsRules"/>.
/// </summary>
public sealed record SaleSettings
{
    /// <summary>New bills open as credit (unpaid, balance due) instead of fully paid.</summary>
    public bool NewSaleIsCredit { get; init; }

    /// <summary>Days after the invoice date a credit bill falls due when the customer has no credit days of its own.</summary>
    public int DefaultDueDays { get; init; }

    /// <summary>Doctor pre-filled on new bills (Doctors module id; no SQL foreign key) and the name shown meanwhile.</summary>
    public long? DefaultDoctorId { get; init; }
    public string DefaultDoctorName { get; init; } = string.Empty;

    /// <summary>
    /// Batches expiring within this many months are not offered for sale (0 = only expired batches are blocked).
    /// Localwell's <c>expiry_cutoff</c>.
    /// </summary>
    public int ExpiryCutoffMonths { get; init; }

    /// <summary>Batches expiring within this many months are highlighted EXPIRING on the bill (3, 6, 9 or 12; default 6).</summary>
    public int NearExpiryMonths { get; init; } = 6;

    /// <summary>How the bill total is rounded (stored as <see cref="RoundOffMode"/>, default nearest rupee).</summary>
    public RoundOffMode RoundOffMode { get; init; } = RoundOffMode.NearestRupee;

    /// <summary>Allow billing more than the stock on hand (default off; negative stock breaks FEFO and audits).</summary>
    public bool AllowNegativeStock { get; init; }

    /// <summary>Compute schemes (e.g. 10+1) on the sold quantity instead of copying the purchase scheme.</summary>
    public bool AddSchemeWrtSaleQty { get; init; }

    /// <summary>Barcode scans add whole packs instead of loose units.</summary>
    public bool BarcodeQtyInPacks { get; init; }

    /// <summary>Discount % pre-filled on every new bill line.</summary>
    public decimal DefaultDiscountPercent { get; init; }

    /// <summary>Bill-level discount bounds for users without the <c>sale_discount_limit_ignore</c> permission.</summary>
    public decimal MinDiscountPercent { get; init; }
    public decimal MaxDiscountPercent { get; init; } = 100m;

    /// <summary>Create refill follow-ups for chronic medicines (Localwell <c>sale_settings.patient_reminders</c>).</summary>
    public bool PatientReminders { get; init; } = true;

    /// <summary>Also require a doctor for Schedule H items (H1, X and NRx always require one by law).</summary>
    public bool RequireDoctorForScheduleH { get; init; }

    /// <summary>Require a prescription reference number on bills that contain any prescription-only item.</summary>
    public bool RequirePrescriptionReference { get; init; }
}

/// <summary>Validation of <see cref="SaleSettings"/> and the rules Billing applies with them (pure).</summary>
public static class SaleSettingsRules
{
    public static readonly IReadOnlyList<int> NearExpiryChoices = new[] { 3, 6, 9, 12 };
    public const int MaxDueDays = 365;
    public const int MaxExpiryCutoffMonths = 24;

    public static IReadOnlyList<SettingsError> Validate(SaleSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var errors = new List<SettingsError>();
        void Add(string field, string message) => errors.Add(new SettingsError(field, message));

        if (s.DefaultDueDays is < 0 or > MaxDueDays)
            Add(nameof(s.DefaultDueDays), $"Default due days must be between 0 and {MaxDueDays}.");
        if (s.ExpiryCutoffMonths is < 0 or > MaxExpiryCutoffMonths)
            Add(nameof(s.ExpiryCutoffMonths), $"Expiry cut-off must be between 0 and {MaxExpiryCutoffMonths} months.");
        if (!NearExpiryChoices.Contains(s.NearExpiryMonths))
            Add(nameof(s.NearExpiryMonths), "Near-expiry window must be 3, 6, 9 or 12 months.");
        if (!Enum.IsDefined(s.RoundOffMode))
            Add(nameof(s.RoundOffMode), "Select a round-off mode.");
        if (s.MinDiscountPercent is < 0 or > 100)
            Add(nameof(s.MinDiscountPercent), "Minimum discount must be between 0 and 100%.");
        if (s.MaxDiscountPercent is < 0 or > 100)
            Add(nameof(s.MaxDiscountPercent), "Maximum discount must be between 0 and 100%.");
        if (s.MinDiscountPercent > s.MaxDiscountPercent)
            Add(nameof(s.MinDiscountPercent), "Minimum discount cannot be more than the maximum discount.");
        if (s.DefaultDiscountPercent is < 0 or > 100)
            Add(nameof(s.DefaultDiscountPercent), "Default discount must be between 0 and 100%.");
        else if (s.DefaultDiscountPercent > s.MaxDiscountPercent)
            Add(nameof(s.DefaultDiscountPercent), "Default discount cannot exceed the maximum discount.");
        if (s.DefaultDoctorId is <= 0)
            Add(nameof(s.DefaultDoctorId), "Default doctor id is invalid.");
        if (s.DefaultDoctorName.Length > 100)
            Add(nameof(s.DefaultDoctorName), "Default doctor name must be at most 100 characters.");
        return errors;
    }

    /// <summary>
    /// True when a batch may be sold on <paramref name="today"/>: not expired (a batch is expired from its expiry date
    /// onwards) and, with a cut-off, expiring strictly after today + cut-off months.
    /// </summary>
    public static bool IsBatchSaleable(DateTime expiryDate, DateTime today, SaleSettings settings) =>
        expiryDate.Date > today.Date.AddMonths(Math.Max(0, settings.ExpiryCutoffMonths));

    public static bool IsExpired(DateTime expiryDate, DateTime today) => expiryDate.Date <= today.Date;

    /// <summary>Not yet expired but expiring within the near-expiry window (orange EXPIRING on the bill).</summary>
    public static bool IsNearExpiry(DateTime expiryDate, DateTime today, SaleSettings settings) =>
        !IsExpired(expiryDate, today) && expiryDate.Date <= today.Date.AddMonths(settings.NearExpiryMonths);

    /// <summary>Due date of a credit bill: the customer's own credit days win over the default.</summary>
    public static DateTime DueDate(DateTime invoiceDate, SaleSettings settings, int? customerCreditDays = null)
    {
        var days = customerCreditDays is > 0 ? customerCreditDays.Value : settings.DefaultDueDays;
        return invoiceDate.Date.AddDays(Math.Clamp(days, 0, MaxDueDays));
    }

    /// <summary>Bill discount bounds for a user; <paramref name="ignoreLimits"/> = permission sale_discount_limit_ignore.</summary>
    public static (decimal Min, decimal Max) DiscountBounds(SaleSettings settings, bool ignoreLimits) =>
        ignoreLimits ? (0m, 100m) : (settings.MinDiscountPercent, settings.MaxDiscountPercent);

    /// <summary>
    /// Line discount after a bill-level % (via <c>PricingCalculator.CombineBillDiscount</c>), clamped to the
    /// settings' bounds and then to the item's own cap.
    /// </summary>
    public static decimal ApplyBillDiscount(decimal lineDiscountPercent, decimal billDiscountPercent, SaleSettings settings,
        decimal? itemMaxDiscountPercent, bool ignoreLimits)
    {
        var (min, max) = DiscountBounds(settings, ignoreLimits);
        if (itemMaxDiscountPercent is { } cap && !ignoreLimits) max = Math.Min(max, cap);
        return Pricing.PricingCalculator.CombineBillDiscount(lineDiscountPercent, billDiscountPercent, min, max);
    }

    /// <summary>Doctor required on the bill for this schedule (statutory for H1/X/NRx; optional for H).</summary>
    public static bool RequiresDoctor(DrugSchedule schedule, SaleSettings settings) =>
        schedule.RequiresDoctor() || (schedule == DrugSchedule.H && settings.RequireDoctorForScheduleH);

    /// <summary>Prescription reference required for this item on the bill.</summary>
    public static bool RequiresPrescriptionReference(DrugSchedule schedule, SaleSettings settings) =>
        settings.RequirePrescriptionReference && schedule.RequiresPrescription();

    /// <summary>True when selling <paramref name="requestedUnits"/> is allowed against <paramref name="unitsOnHand"/>.</summary>
    public static bool CanSellQuantity(long requestedUnits, long unitsOnHand, SaleSettings settings) =>
        requestedUnits > 0 && (settings.AllowNegativeStock || requestedUnits <= unitsOnHand);
}
