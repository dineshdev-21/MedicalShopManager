using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.ViewModels;

/// <summary>Page keys of the Reports module (Reports group).</summary>
public static class ReportsPages
{
    public const string Catalogue = "Reports";
    public const string Viewer = "ReportViewer";
}

/// <summary>A report tile in the catalogue.</summary>
public sealed partial class ReportCardItem : ObservableObject
{
    public ReportCardItem(ReportDefinition definition, bool isFavourite)
    {
        Definition = definition;
        _isFavourite = isFavourite;
    }

    public ReportDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string CategoryLabel => Definition.CategoryLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph))]
    private bool _isFavourite;

    public string StarGlyph => IsFavourite ? "★" : "☆";
}

/// <summary>A category in the catalogue's left list. Key null = all reports; <see cref="IsFavourites"/> = starred only.</summary>
public sealed record ReportCategoryItem(string Label, ReportCategory? Category, bool IsFavourites = false)
{
    public static readonly ReportCategoryItem AllReports = new("All reports", null);
    public static readonly ReportCategoryItem Favourites = new("★ Favourites", null, true);
}

/// <summary>A drop-down filter of the open report.</summary>
public sealed partial class ReportChoiceItem : ObservableObject
{
    public ReportChoiceItem(ReportChoice choice, string selectedKey)
    {
        Choice = choice;
        _selected = choice.Options.FirstOrDefault(o => o.Key == selectedKey) ?? choice.Options[0];
    }

    public ReportChoice Choice { get; }
    public string Label => Choice.Label;
    public IReadOnlyList<ReportChoiceOption> Options => Choice.Options;

    [ObservableProperty]
    private ReportChoiceOption _selected;
}

/// <summary>A label / value pair for the summary strip and the totals footer.</summary>
public sealed record ReportFigure(string Label, string Value);

/// <summary>A date preset button.</summary>
public sealed record ReportPresetItem(ReportDatePreset Preset)
{
    public string Label => Preset.ToLabel();
}
