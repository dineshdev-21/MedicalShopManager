using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.App.Modules.Billing.Printing;

/// <summary>
/// The billing screen's invoice print (F2 preview, Ctrl+P). Shares the name "SaleInvoice" with
/// <see cref="SaleInvoiceTemplate"/>, so both use the same default paper size (Print.PaperSize.SaleInvoice).
/// </summary>
public sealed class BillingInvoiceTemplate : IDocumentTemplate<SaleInvoicePrintModel>
{
    public string Name => SaleInvoiceTemplate.TemplateName;

    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } =
        new[] { PaperSize.A4, PaperSize.A5, PaperSize.Thermal80mm, PaperSize.Thermal58mm };

    /// <summary>Localwell defaults laser/inkjet bills to A5.</summary>
    public PaperSize DefaultPaperSize => PaperSize.A5;

    public FlowDocument Render(SaleInvoicePrintModel model, PrintContext context) => SaleInvoiceLayout.Render(model, context);
}
