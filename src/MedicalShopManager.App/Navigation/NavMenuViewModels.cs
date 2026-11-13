using CommunityToolkit.Mvvm.ComponentModel;

namespace MedicalShopManager.App.Navigation;

/// <summary>One sidebar section with its header and pages.</summary>
public sealed class NavGroupViewModel
{
    public NavGroupViewModel(NavGroup group, IReadOnlyList<NavEntryViewModel> entries)
    {
        Group = group;
        Entries = entries;
    }

    public NavGroup Group { get; }
    public string Header => Group.ToTitle().ToUpperInvariant();
    public IReadOnlyList<NavEntryViewModel> Entries { get; }
}

/// <summary>One sidebar button; <see cref="IsSelected"/> follows the active page.</summary>
public sealed partial class NavEntryViewModel : ObservableObject
{
    public NavEntryViewModel(NavItem item) => Item = item;

    public NavItem Item { get; }
    public string Key => Item.Key;
    public string Title => Item.Title;
    public string IconGlyph => Item.IconGlyph;
    public string? ToolTip => string.IsNullOrWhiteSpace(Item.KeyGesture) ? Item.Title : $"{Item.Title} ({Item.KeyGesture})";

    [ObservableProperty]
    private bool _isSelected;
}
