namespace MedicalShopManager.App.Services;

/// <summary>A request to show a page, with an optional parameter for INavigationAware ViewModels.</summary>
public sealed record NavigationRequest(string PageKey, object? Parameter);

/// <summary>
/// Service for managing page navigation within the main window frame. Page keys are NavItem.Key values
/// registered in INavigationRegistry.
/// </summary>
public interface INavigationService
{
    event EventHandler<NavigationRequest>? NavigationChanged;
    string CurrentPage { get; }

    /// <summary>Shows a page. Re-navigating to the current page is ignored unless a parameter is passed.</summary>
    void NavigateTo(string pageKey, object? parameter = null);
}

public class NavigationService : INavigationService
{
    public event EventHandler<NavigationRequest>? NavigationChanged;
    public string CurrentPage { get; private set; } = string.Empty;

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        if (parameter == null && string.Equals(CurrentPage, pageKey, StringComparison.OrdinalIgnoreCase)) return;
        CurrentPage = pageKey;
        NavigationChanged?.Invoke(this, new NavigationRequest(pageKey, parameter));
    }
}
