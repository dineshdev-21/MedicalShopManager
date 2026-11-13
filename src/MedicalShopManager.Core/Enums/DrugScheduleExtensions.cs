namespace MedicalShopManager.Core.Enums;

/// <summary>
/// Dispensing rules per <see cref="DrugSchedule"/> (MASTER-SPEC §4.6). Billing and the H1 register use these
/// instead of switching on the enum themselves.
/// </summary>
public static class DrugScheduleExtensions
{
    /// <summary>True when a prescription is needed before the item can be sold.</summary>
    public static bool RequiresPrescription(this DrugSchedule schedule) => schedule != DrugSchedule.None;

    /// <summary>True when the bill must name a doctor (H when the sale setting asks for it is decided by the caller).</summary>
    public static bool RequiresDoctor(this DrugSchedule schedule) =>
        schedule is DrugSchedule.H1 or DrugSchedule.X or DrugSchedule.NRx;

    /// <summary>True when each sale must be written to a statutory register (H1 / X / narcotic).</summary>
    public static bool RequiresRegisterEntry(this DrugSchedule schedule) =>
        schedule is DrugSchedule.H1 or DrugSchedule.X or DrugSchedule.NRx;

    /// <summary>Short label as printed on bills and shown in grids, e.g. "Sch 'H1'"; OTC shows "-".</summary>
    public static string ToLabel(this DrugSchedule schedule) => schedule switch
    {
        DrugSchedule.None => "-",
        DrugSchedule.H => "Sch 'H'",
        DrugSchedule.H1 => "Sch 'H1'",
        DrugSchedule.X => "Sch 'X'",
        DrugSchedule.NRx => "NRx",
        _ => "Rx"
    };
}
