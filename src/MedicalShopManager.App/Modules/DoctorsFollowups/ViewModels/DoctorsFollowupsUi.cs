using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.ViewModels;

/// <summary>An option of a drop-down: value plus the text the user sees.</summary>
public sealed record DoctorsFollowupsOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Shared UI helpers of the doctors and follow-up screens: error wording, option lists, clipboard.</summary>
internal static class DoctorsFollowupsUi
{
    /// <summary>User-facing messages for an exception (validation messages as written; details of anything else go to the log).</summary>
    public static IReadOnlyList<string> Messages(Exception ex, string action) => ex switch
    {
        DoctorsFollowupsValidationException v => v.Errors.Count > 0 ? v.Errors : new[] { v.Message },
        ArgumentException or InvalidOperationException => new[] { ex.Message },
        _ => new[] { $"Could not {action}: {ex.Message}" }
    };

    public static string Message(Exception ex, string action) => string.Join(Environment.NewLine, Messages(ex, action));

    /// <summary>Copies text to the Windows clipboard; false when another program holds the clipboard.</summary>
    public static bool TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static IReadOnlyList<DoctorsFollowupsOption<FollowupSort>> Sorts { get; } = new[]
    {
        new DoctorsFollowupsOption<FollowupSort>(FollowupSort.FollowupDateAscending, "Follow-up date (soonest first)"),
        new DoctorsFollowupsOption<FollowupSort>(FollowupSort.FollowupDateDescending, "Follow-up date (latest first)"),
        new DoctorsFollowupsOption<FollowupSort>(FollowupSort.LastSaleDescending, "Last sale (newest first)"),
        new DoctorsFollowupsOption<FollowupSort>(FollowupSort.PatientNameAscending, "Customer name A to Z"),
        new DoctorsFollowupsOption<FollowupSort>(FollowupSort.PatientNameDescending, "Customer name Z to A")
    };

    public static IReadOnlyList<DoctorsFollowupsOption<int?>> Genders { get; } = new[]
    {
        new DoctorsFollowupsOption<int?>(null, "All genders"),
        new DoctorsFollowupsOption<int?>(1, "Male"),
        new DoctorsFollowupsOption<int?>(2, "Female"),
        new DoctorsFollowupsOption<int?>(3, "Others")
    };

    public static IReadOnlyList<DoctorsFollowupsOption<FollowupTab>> Tabs { get; } = new[]
    {
        new DoctorsFollowupsOption<FollowupTab>(FollowupTab.Arriving, "Arriving"),
        new DoctorsFollowupsOption<FollowupTab>(FollowupTab.Expired, "Expired"),
        new DoctorsFollowupsOption<FollowupTab>(FollowupTab.Actioned, "Actioned"),
        new DoctorsFollowupsOption<FollowupTab>(FollowupTab.AllPending, "All pending")
    };

    public static IReadOnlyList<DoctorsFollowupsOption<bool?>> RuleChoices { get; } = new[]
    {
        new DoctorsFollowupsOption<bool?>(null, "Default"),
        new DoctorsFollowupsOption<bool?>(true, "Remind"),
        new DoctorsFollowupsOption<bool?>(false, "Do not remind")
    };
}
