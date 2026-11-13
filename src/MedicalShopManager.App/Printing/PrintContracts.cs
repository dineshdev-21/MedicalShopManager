using System.Windows.Documents;
using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.App.Printing;

/// <summary>Shop details printed in document headers (read from AppSettings by the print service).</summary>
public sealed record ShopProfile(
    string Name,
    string Address,
    string Phone,
    string Gstin,
    string DrugLicenseNumber,
    string PharmacistName)
{
    public bool IsGstRegistered => !string.IsNullOrWhiteSpace(Gstin);
}

/// <summary>Everything a template needs besides its model.</summary>
public sealed record PrintContext(PaperSize PaperSize, ShopProfile Shop, string? CopyLabel = null)
{
    public bool IsThermal => PaperSize is PaperSize.Thermal80mm or PaperSize.Thermal58mm;

    /// <summary>Base font size for the paper (templates scale headings from it).</summary>
    public double BaseFontSize => PaperLayout.BaseFontSize(PaperSize);
}

/// <summary>
/// A printable layout for one model type. Modules add their own (sale invoice, credit note, purchase order,
/// barcode labels, …) as classes in App/Modules/&lt;Name&gt;/Printing and register them in DI as
/// <c>IDocumentTemplate&lt;TModel&gt;</c>. Templates only build the FlowDocument; page size, margins and the
/// printer dialog are the print service's job.
/// </summary>
public interface IDocumentTemplate<in TModel>
{
    /// <summary>Stable key; also the settings key suffix for the default paper size ("Print.PaperSize.&lt;Name&gt;").</summary>
    string Name { get; }

    IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; }

    /// <summary>Paper size used when neither the caller nor the settings choose one.</summary>
    PaperSize DefaultPaperSize { get; }

    FlowDocument Render(TModel model, PrintContext context);
}

/// <summary>Per-call print options; null members fall back to settings / template defaults.</summary>
public sealed record PrintOptions(PaperSize? PaperSize = null, bool ShowPrintDialog = true, string? CopyLabel = null);

/// <summary>Prints or previews any <see cref="IDocumentTemplate{TModel}"/> as a WPF FlowDocument.</summary>
public interface IPrintService
{
    /// <summary>Prints the document. Returns false when the user cancels the printer dialog.</summary>
    Task<bool> PrintAsync<TModel>(IDocumentTemplate<TModel> template, TModel model, PrintOptions? options = null);

    /// <summary>Shows a modal preview with a Print button.</summary>
    Task PreviewAsync<TModel>(IDocumentTemplate<TModel> template, TModel model, PrintOptions? options = null);

    /// <summary>Paper size for a template: settings "Print.PaperSize.&lt;Name&gt;" if valid and supported, else the template default.</summary>
    Task<PaperSize> GetPaperSizeAsync<TModel>(IDocumentTemplate<TModel> template);
}
