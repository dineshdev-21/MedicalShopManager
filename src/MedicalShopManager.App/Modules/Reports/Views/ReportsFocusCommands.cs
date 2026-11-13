using System.Windows.Input;

namespace MedicalShopManager.App.Modules.Reports.Views;

/// <summary>View-only routed commands of the Reports pages (keyboard focus moves, handled in code-behind).</summary>
public static class ReportsFocusCommands
{
    public static readonly RoutedUICommand FocusSearch = new("Focus search", nameof(FocusSearch), typeof(ReportsFocusCommands));
}
