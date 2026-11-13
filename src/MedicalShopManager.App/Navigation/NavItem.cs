namespace MedicalShopManager.App.Navigation;

/// <summary>
/// Sidebar sections, in display order (Localwell grouping). The enum order is the sidebar order.
/// </summary>
public enum NavGroup
{
    Dashboard,
    Sales,
    Purchases,
    Stock,
    Invoices,
    Orderbook,
    Reports,
    Finance,
    ControlCenter,
    System
}

/// <summary>
/// One navigable page. Modules register these in their AddXxxModule method; the sidebar, keyboard shortcuts and
/// page resolution are all built from the registry, so no shell file changes when a page is added.
/// </summary>
/// <param name="Key">Unique, stable page key used by INavigationService.NavigateTo (e.g. "NewBill").</param>
/// <param name="Title">Sidebar text.</param>
/// <param name="Group">Sidebar section.</param>
/// <param name="IconGlyph">Segoe MDL2 Assets glyph, e.g. "".</param>
/// <param name="Order">Position inside the group (ascending; use steps of 10).</param>
/// <param name="ViewModelType">Page ViewModel (derives from BaseViewModel, registered in DI). Its view is found by name: FooViewModel → FooPage / FooView.</param>
/// <param name="KeyGesture">Optional window-level shortcut in WPF KeyGesture syntax, e.g. "Ctrl+Alt+S" or "F8".</param>
/// <param name="ShowInSidebar">False for pages reached only by code or shortcut (detail / entry screens).</param>
public sealed record NavItem(
    string Key,
    string Title,
    NavGroup Group,
    string IconGlyph,
    int Order,
    Type ViewModelType,
    string? KeyGesture = null,
    bool ShowInSidebar = true);

public static class NavGroupExtensions
{
    public static string ToTitle(this NavGroup group) => group switch
    {
        NavGroup.ControlCenter => "Control Center",
        _ => group.ToString()
    };
}
