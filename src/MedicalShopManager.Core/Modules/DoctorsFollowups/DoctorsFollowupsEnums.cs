namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>Life cycle of a refill follow-up. Stored as INTEGER in PatientFollowups.Status; never renumber.</summary>
public enum FollowupStatus
{
    /// <summary>Waiting for the patient's refill (shown in Arriving / Expired).</summary>
    Pending = 1,

    /// <summary>The shop followed up (called / messaged): "Actioned".</summary>
    Done = 2,

    /// <summary>The patient bought the item again: a newer follow-up replaced this one.</summary>
    Refilled = 3,

    /// <summary>Stopped by the user (therapy stopped, patient moved, not needed).</summary>
    Dismissed = 4,

    /// <summary>The source bill was cancelled or the item was removed from it.</summary>
    Cancelled = 5
}

/// <summary>How a follow-up was created. Stored as INTEGER in PatientFollowups.Source; never renumber.</summary>
public enum FollowupSource
{
    /// <summary>Generated from a sale of a chronic medicine.</summary>
    Sale = 1,

    /// <summary>Added by hand on the follow-ups page.</summary>
    Manual = 2,

    /// <summary>Accepted from the recommended reminders (repeat purchases of a non-chronic item).</summary>
    Recommended = 3
}

/// <summary>Where a follow-up stands relative to today (computed, never stored).</summary>
public enum FollowupBucket
{
    /// <summary>Past the due date by more than the grace period, no refill: Localwell "Expired".</summary>
    Lapsed = 1,
    Overdue = 2,
    DueToday = 3,
    /// <summary>Due within the upcoming window.</summary>
    Upcoming = 4,
    /// <summary>Due after the upcoming window.</summary>
    Later = 5,
    /// <summary>Not pending any more (followed up, refilled, dismissed, cancelled).</summary>
    Closed = 6
}

/// <summary>Tabs of the follow-ups page (Localwell: Arriving | Expired, plus Actioned and All).</summary>
public enum FollowupTab
{
    /// <summary>Pending and due: overdue, due today or within the upcoming window.</summary>
    Arriving = 1,

    /// <summary>Pending but lapsed beyond the grace period.</summary>
    Expired = 2,

    /// <summary>Followed up (Done).</summary>
    Actioned = 3,

    /// <summary>Every pending follow-up, including those due later.</summary>
    AllPending = 4
}

/// <summary>Sort order of the follow-up list (Localwell SORT BY options).</summary>
public enum FollowupSort
{
    FollowupDateAscending = 1,
    FollowupDateDescending = 2,
    LastSaleDescending = 3,
    PatientNameAscending = 4,
    PatientNameDescending = 5
}
