using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Purchases.Printing;

/// <summary>
/// Purchase voucher (goods received note) of one distributor bill: header, every line with batch, expiry, pack, qty +
/// free, rates per pack, discounts, GST and amount, then the totals with GST split, round-off, paid and balance.
/// </summary>
public sealed class PurchaseVoucherTemplate : IDocumentTemplate<PurchaseBill>
{
    public const string TemplateName = "PurchaseVoucher";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(PurchaseBill bill, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var size = context.BaseFontSize;
        var doc = PurchasePrintBlocks.NewDocument();
        var title = bill.Status switch
        {
            PurchaseStatus.Cancelled => "PURCHASE VOUCHER (CANCELLED)",
            PurchaseStatus.Draft => "PURCHASE VOUCHER (DRAFT)",
            _ => "PURCHASE VOUCHER"
        };
        PurchasePrintBlocks.AddHeader(doc, title, context);

        doc.Blocks.Add(Text($"Distributor: {bill.SupplierName}" + (string.IsNullOrWhiteSpace(bill.SupplierGstin) ? "" : $"   GSTIN: {bill.SupplierGstin}"),
            size, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Bill No: {bill.SupplierInvoiceNumber}   Bill Date: {bill.InvoiceDate:dd-MM-yyyy}   " +
                            $"Purchase No: {bill.PurchaseNumber ?? "-"}" + (bill.DueDate is { } due ? $"   Due: {due:dd-MM-yyyy}" : ""), size));
        if (!bill.WithGst) doc.Blocks.Add(Text("Bill without GST column: GST not added to the total.", size - 1));

        var table = Table(size - 1, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("PRODUCT", 3.0, TextAlignment.Left), ("HSN", 0.9, TextAlignment.Left),
            ("BATCH", 1.1, TextAlignment.Left), ("EXP", 0.7, TextAlignment.Left), ("PACK", 0.6, TextAlignment.Right),
            ("QTY", 0.8, TextAlignment.Right), ("FREE", 0.7, TextAlignment.Right), ("MRP", 1.0, TextAlignment.Right),
            ("RATE", 1.0, TextAlignment.Right), ("DIS%", 0.6, TextAlignment.Right), ("GST%", 0.6, TextAlignment.Right),
            ("AMOUNT", 1.2, TextAlignment.Right)
        });
        foreach (var line in bill.Lines)
        {
            var discount = line.DiscountPercent + line.SchemePercent;
            AddRow(table, new[]
            {
                (line.LineNo.ToString(), TextAlignment.Left), (line.MedicineName, TextAlignment.Left), (line.HsnCode ?? "", TextAlignment.Left),
                (line.BatchNumber, TextAlignment.Left), (line.ExpiryLabel, TextAlignment.Left), ($"1x{line.UnitsPerPack}", TextAlignment.Right),
                (line.QuantityDisplay, TextAlignment.Right), (line.FreeQuantity > 0 ? line.FreeQuantityDisplay : "", TextAlignment.Right),
                (Money(line.MrpPaisa), TextAlignment.Right), (Money(line.PurchaseRatePaisa), TextAlignment.Right),
                (discount > 0 ? discount.ToString("0.##") : "", TextAlignment.Right), (line.GstRatePercent.ToString("0.##"), TextAlignment.Right),
                (Money(line.LineTotalPaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());

        var rows = new List<(string, string, bool)>
        {
            ("Gross", Money(bill.SubtotalPaisa), false),
            ("Discount + scheme", Money(bill.DiscountAmountPaisa), false),
            ("Taxable", Money(bill.TaxablePaisa), false)
        };
        if (bill.IgstPaisa > 0) rows.Add(("IGST", Money(bill.IgstPaisa), false));
        else
        {
            rows.Add(("CGST", Money(bill.CgstPaisa), false));
            rows.Add(("SGST", Money(bill.SgstPaisa), false));
        }
        if (bill.OtherChargesPaisa > 0) rows.Add((bill.OtherChargesLabel ?? "Other charges", Money(bill.OtherChargesPaisa), false));
        rows.Add(("Round off", PurchasePrintBlocks.Signed(bill.RoundOffPaisa), false));
        rows.Add(("Invoice amount", Money(bill.GrandTotalPaisa), true));
        rows.Add(("Paid", Money(bill.AmountPaidPaisa), false));
        rows.Add(("Balance due", Money(bill.BalancePaisa), true));
        doc.Blocks.Add(PurchasePrintBlocks.Totals(size, rows));

        doc.Blocks.Add(Text($"Products: {bill.Lines.Count}   Total units: {bill.TotalUnits}", size - 1));
        if (!string.IsNullOrWhiteSpace(bill.Notes)) doc.Blocks.Add(Text($"Notes: {bill.Notes}", size - 1));
        doc.Blocks.Add(Text("Received by: ____________________", size, margin: new Thickness(0, 16, 0, 0)));
        return doc;
    }
}
