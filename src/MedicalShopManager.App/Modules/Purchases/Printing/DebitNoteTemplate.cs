using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Purchases.Printing;

/// <summary>
/// Debit note for goods returned to a distributor (purchase return or expiry return): shop and distributor GSTINs,
/// against which bill, reason, the batches with qty / rate / GST, and totals with the GST split (input-GST reversal).
/// </summary>
public sealed class DebitNoteTemplate : IDocumentTemplate<PurchaseReturnDocument>
{
    public const string TemplateName = "DebitNote";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A5;

    public FlowDocument Render(PurchaseReturnDocument note, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(note);
        var size = context.BaseFontSize;
        var doc = PurchasePrintBlocks.NewDocument();
        PurchasePrintBlocks.AddHeader(doc, note.IsCancelled ? "DEBIT NOTE (CANCELLED)" : "DEBIT NOTE", context);

        doc.Blocks.Add(Text($"Debit Note No: {note.ReturnNumber}   Date: {note.ReturnDate:dd-MM-yyyy}   {note.KindLabel}", size, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"To: {note.SupplierName}" + (string.IsNullOrWhiteSpace(note.SupplierGstin) ? "" : $"   GSTIN: {note.SupplierGstin}"), size));
        var against = string.IsNullOrWhiteSpace(note.PurchaseReference) ? "" : $"Against purchase: {note.PurchaseReference}   ";
        doc.Blocks.Add(Text($"{against}Reason: {note.ReasonLabel}", size));

        var table = Table(size - 1, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("PRODUCT", 3.0, TextAlignment.Left), ("HSN", 0.9, TextAlignment.Left),
            ("BATCH", 1.1, TextAlignment.Left), ("EXP", 0.7, TextAlignment.Left), ("QTY", 0.8, TextAlignment.Right),
            ("RATE", 1.0, TextAlignment.Right), ("TAXABLE", 1.1, TextAlignment.Right), ("GST%", 0.6, TextAlignment.Right),
            ("GST", 0.9, TextAlignment.Right), ("AMOUNT", 1.1, TextAlignment.Right)
        });
        var row = 0;
        foreach (var line in note.Lines)
        {
            AddRow(table, new[]
            {
                ((++row).ToString(), TextAlignment.Left), (line.MedicineName, TextAlignment.Left), (line.HsnCode ?? "", TextAlignment.Left),
                (line.BatchNumber, TextAlignment.Left), (line.ExpiryLabel, TextAlignment.Left), (line.QuantityDisplay, TextAlignment.Right),
                (Money(line.RatePaisa), TextAlignment.Right), (Money(line.TaxablePaisa), TextAlignment.Right),
                (line.GstRatePercent.ToString("0.##"), TextAlignment.Right), (Money(line.TaxPaisa), TextAlignment.Right),
                (Money(line.LineTotalPaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());

        var rows = new List<(string, string, bool)> { ("Taxable", Money(note.TaxablePaisa), false) };
        if (note.IgstPaisa > 0) rows.Add(("IGST", Money(note.IgstPaisa), false));
        else
        {
            rows.Add(("CGST", Money(note.CgstPaisa), false));
            rows.Add(("SGST", Money(note.SgstPaisa), false));
        }
        rows.Add(("Round off", PurchasePrintBlocks.Signed(note.RoundOffPaisa), false));
        rows.Add(("Debit note value", Money(note.NetAmountPaisa), true));
        if (note.RefundReceivedPaisa > 0)
        {
            rows.Add(("Refund received", Money(note.RefundReceivedPaisa), false));
            rows.Add(("Adjust against bills", Money(note.OpenCreditPaisa), true));
        }
        doc.Blocks.Add(PurchasePrintBlocks.Totals(size, rows));

        if (!string.IsNullOrWhiteSpace(note.Notes)) doc.Blocks.Add(Text($"Notes: {note.Notes}", size - 1));
        doc.Blocks.Add(Text("Goods returned in good faith. Please issue a credit note or adjust against our dues.", size - 1,
            margin: new Thickness(0, 8, 0, 0)));
        doc.Blocks.Add(Text("For " + context.Shop.Name + "                    Received by (distributor): ____________________", size,
            margin: new Thickness(0, 18, 0, 0)));
        return doc;
    }
}
