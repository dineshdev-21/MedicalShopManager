using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>One permission checkbox in the matrix.</summary>
public partial class PermissionItem : ObservableObject
{
    public PermissionItem(PermissionDefinition definition, bool isPermitted, bool isEditable)
    {
        Definition = definition;
        _isPermitted = isPermitted;
        _isEditable = isEditable;
    }

    public PermissionDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.IsMaster ? Definition.Label + " (master)" : Definition.Label;
    public bool IsMaster => Definition.IsMaster;

    [ObservableProperty] private bool _isPermitted;

    /// <summary>False for administrators, and for non-master grants while the group's master switch is off.</summary>
    [ObservableProperty] private bool _isEditable;
}

/// <summary>A permission group (sale, purchase, …); its master <c>*_view</c> switch gates the other grants.</summary>
public sealed class PermissionGroupItem
{
    private readonly bool _editable;

    public PermissionGroupItem(PermissionGroupDefinition group, IReadOnlyDictionary<string, bool> values, bool editable)
    {
        _editable = editable;
        Title = group.Label;
        Items = group.Permissions.Select(p => new PermissionItem(p, values.TryGetValue(p.Key, out var v) && v, editable)).ToList();
        Master = Items.FirstOrDefault(i => i.IsMaster);
        if (Master != null)
        {
            Master.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PermissionItem.IsPermitted)) ApplyMaster();
            };
        }
        ApplyMaster();
    }

    public string Title { get; }
    public IReadOnlyList<PermissionItem> Items { get; }
    public PermissionItem? Master { get; }

    private void ApplyMaster()
    {
        foreach (var item in Items.Where(i => !i.IsMaster && !i.Definition.IsRestriction))
            item.IsEditable = _editable && (Master?.IsPermitted ?? true);
    }
}
