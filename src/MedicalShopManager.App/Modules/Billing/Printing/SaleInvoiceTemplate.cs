using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.Printing;

/// <summary>
/// Sale invoice for the shared <see cref="Sale"/> model (Sales History reprint, Control Center ▸ Print Layout
/// preview). Converts the sale into a <see cref="BillingInvoice"/> and prints it with the same
/// <see cref="SaleInvoiceLayout"/> as the billing screen, using the default invoice toggles (templates render
/// synchronously). Pre-6.2 lines are per unit, so they print with a pack of 1. Title: TAX INVOICE when the shop has
/// a GSTIN, else BILL OF SUPPLY.
/// </summary>
public sealed class SaleInvoiceTemplate : IDocumentTemplate<Sale>
{
    public const string TemplateName = "SaleInvoice";

    public string Name => TemplateName;

    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } =
        new[] { PaperSize.A4, PaperSize.A5, PaperSize.Thermal80mm, PaperSize.Thermal58mm };

    /// <summary>Localwell defaults laser/inkjet bills to A5.</summary>
    public PaperSize DefaultPaperSize => PaperSize.A5;

    public FlowDocument Render(Sale sale, PrintContext context) =>
        SaleInvoiceLayout.Render(new SaleInvoicePrintModel(ToInvoice(sale, context.Shop.IsGstRegistered), new InvoicePrintSettings()), context);

    /// <summary>Maps the shared sale model onto the billing invoice the layout prints.</summary>
    public static BillingInvoice ToInvoice(Sale sale, bool shopIsGstRegistered)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var invoice = new BillingInvoice
        {
            Id = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            InvoiceDate = sale.InvoiceDate,
            IsCounterSale = sale.CustomerId is null,
            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,
            CustomerPhone = sale.CustomerPhone,
            CustomerAddress = sale.Customer?.Address,
            DoctorName = sale.Customer?.DoctorName,
            PrescriptionNumber = sale.PrescriptionRef,
            WithGst = true,
            IsInterState = sale.IgstAmountPaisa != 0,
            IsTaxInvoice = shopIsGstRegistered,
            SubtotalPaisa = sale.SubtotalPaisa,
            DiscountPaisa = sale.DiscountAmountPaisa,
            TaxablePaisa = sale.TaxableAmountPaisa,
            CgstPaisa = sale.CgstAmountPaisa,
            SgstPaisa = sale.SgstAmountPaisa,
            IgstPaisa = sale.IgstAmountPaisa,
            GrandTotalPaisa = sale.GrandTotalPaisa,
            AmountPaidPaisa = sale.AmountPaidPaisa,
            BalanceDuePaisa = sale.BalanceDuePaisa,
            CashReceivedPaisa = sale.CashReceivedPaisa,
            ChangeReturnedPaisa = sale.ChangeReturnedPaisa,
            PaymentMethod = sale.PaymentMethod,
            PaymentStatus = sale.PaymentStatus,
            Notes = sale.Notes,
            CreatedByUserId = sale.CreatedByUserId,
            CreatedAt = sale.CreatedAt
        };
        foreach (var item in sale.Items)
        {
            invoice.Lines.Add(new BillingInvoiceLine
            {
                SaleItemId = item.Id,
                MedicineId = item.MedicineId,
                MedicineName = item.MedicineName,
                HsnCode = item.HsnCode,
                BatchId = item.BatchId,
                BatchNumber = item.BatchNumber,
                ExpiryDate = item.ExpiryDate,
                UnitsPerPack = 1,
                Packs = item.Quantity,
                Quantity = item.Quantity,
                MrpPerPackPaisa = item.UnitPricePaisa,
                RatePerPackPaisa = item.UnitPricePaisa,
                DiscountPercent = item.DiscountPercent,
                GstPercent = item.GstRatePercent,
                DiscountPaisa = item.DiscountAmountPaisa,
                TaxablePaisa = item.TaxableValuePaisa,
                CgstPaisa = item.CgstAmountPaisa,
                SgstPaisa = item.SgstAmountPaisa,
                IgstPaisa = item.IgstAmountPaisa,
                TaxPaisa = item.TaxAmountPaisa,
                LineTotalPaisa = item.LineTotalPaisa,
                CogsPaisa = item.CogsPaisa
            });
        }
        invoice.MrpTotalPaisa = invoice.Lines.Sum(l => PricingCalculator.AmountForUnits(l.MrpPerPackPaisa, l.Quantity, 1));
        invoice.SavingsPaisa = Math.Max(invoice.MrpTotalPaisa - invoice.GrandTotalPaisa, 0);
        return invoice;
    }
}
