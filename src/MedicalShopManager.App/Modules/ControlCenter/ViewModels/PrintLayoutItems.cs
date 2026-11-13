using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.App.Modules.ControlCenter.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>A print template row with its chosen default paper size.</summary>
public partial class TemplatePaperSizeItem : ObservableObject
{
    public TemplatePaperSizeItem(PrintTemplateEntry entry, PaperSize selected)
    {
        Entry = entry;
        _selectedSize = selected;
    }

    public PrintTemplateEntry Entry { get; }
    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;
    public IReadOnlyCollection<PaperSize> SupportedSizes => Entry.SupportedPaperSizes;
    public string DefaultLabel => $"Template default: {Entry.DefaultPaperSize}";

    /// <summary>A sample model exists only for the sale invoice (Core <see cref="Sale"/>); other documents are previewed from their own screens.</summary>
    public bool CanPreview => Entry.ModelType == typeof(Sale);

    [ObservableProperty] private PaperSize _selectedSize;
}

/// <summary>One invoice print toggle.</summary>
public partial class InvoiceFieldItem : ObservableObject
{
    public InvoiceFieldItem(InvoiceFieldDefinition definition, bool isEnabled)
    {
        Definition = definition;
        _isEnabled = isEnabled;
    }

    public InvoiceFieldDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public string Hint => Definition.Hint ?? string.Empty;

    [ObservableProperty] private bool _isEnabled;
}

/// <summary>Invoice toggles of one section (Header, Party, Items, Totals, Footer).</summary>
public sealed record InvoiceFieldGroupItem(string Title, IReadOnlyList<InvoiceFieldItem> Items);
