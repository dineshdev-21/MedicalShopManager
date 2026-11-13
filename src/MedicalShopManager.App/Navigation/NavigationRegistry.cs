using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App.Navigation;

/// <summary>All pages known to the shell. Populated once at startup by the module registration methods.</summary>
public interface INavigationRegistry
{
    /// <summary>Adds a page. Throws on a duplicate key, duplicate shortcut or a ViewModel that is not a BaseViewModel.</summary>
    void Register(NavItem item);

    /// <summary>All pages ordered by group, then order, then title.</summary>
    IReadOnlyList<NavItem> Items { get; }

    NavItem? Find(string key);
}

public sealed class NavigationRegistry : INavigationRegistry
{
    private readonly Dictionary<string, NavItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<NavItem> Items => _items.Values
        .OrderBy(i => i.Group)
        .ThenBy(i => i.Order)
        .ThenBy(i => i.Title, StringComparer.CurrentCulture)
        .ToList();

    public void Register(NavItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Key))
            throw new ArgumentException("Navigation key is required.", nameof(item));
        if (!typeof(BaseViewModel).IsAssignableFrom(item.ViewModelType))
            throw new ArgumentException($"{item.ViewModelType.Name} must derive from BaseViewModel.", nameof(item));
        if (_items.TryGetValue(item.Key, out var existing))
            throw new InvalidOperationException($"Navigation key '{item.Key}' is already registered by {existing.ViewModelType.Name}.");

        if (!string.IsNullOrWhiteSpace(item.KeyGesture))
        {
            var clash = _items.Values.FirstOrDefault(i =>
                string.Equals(Normalize(i.KeyGesture), Normalize(item.KeyGesture), StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                throw new InvalidOperationException($"Shortcut '{item.KeyGesture}' is already used by page '{clash.Key}'.");
        }

        _items.Add(item.Key, item);
    }

    public NavItem? Find(string key) =>
        !string.IsNullOrWhiteSpace(key) && _items.TryGetValue(key, out var item) ? item : null;

    private static string? Normalize(string? gesture) => gesture?.Replace(" ", string.Empty);
}
