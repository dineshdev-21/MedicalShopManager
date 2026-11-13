namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// How the shop is registered under GST. Stored as INTEGER text in AppSettings
/// (<c>ControlCenter.Business.GstRegistrationType</c>); never renumber.
/// </summary>
public enum GstRegistrationType
{
    /// <summary>Normal taxpayer: issues a Tax Invoice and charges GST (CGST + SGST, or IGST inter-state).</summary>
    Regular = 1,

    /// <summary>Composition dealer (section 10): cannot collect GST, issues a Bill of Supply.</summary>
    Composition = 2,

    /// <summary>Below the threshold / not registered: no GSTIN, issues a Bill of Supply.</summary>
    Unregistered = 3
}

public static class GstRegistrationTypeExtensions
{
    /// <summary>True when the shop may charge GST on its sales (Tax Invoice).</summary>
    public static bool ChargesGst(this GstRegistrationType type) => type == GstRegistrationType.Regular;

    /// <summary>True when sale documents must be titled "Bill of Supply" instead of "Tax Invoice".</summary>
    public static bool IssuesBillOfSupply(this GstRegistrationType type) => !type.ChargesGst();

    /// <summary>True when a GSTIN is mandatory for this registration type.</summary>
    public static bool RequiresGstin(this GstRegistrationType type) => type != GstRegistrationType.Unregistered;

    /// <summary>Title printed on a sale document.</summary>
    public static string SaleDocumentTitle(this GstRegistrationType type) =>
        type.ChargesGst() ? "Tax Invoice" : "Bill of Supply";

    public static string ToLabel(this GstRegistrationType type) => type switch
    {
        GstRegistrationType.Regular => "Regular (Tax Invoice)",
        GstRegistrationType.Composition => "Composition (Bill of Supply)",
        GstRegistrationType.Unregistered => "Unregistered (Bill of Supply)",
        _ => type.ToString()
    };
}
