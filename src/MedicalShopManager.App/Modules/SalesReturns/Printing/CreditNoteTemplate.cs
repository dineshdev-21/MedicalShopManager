using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.SalesReturns.Printing;

/// <summary>
/// Credit note for a sale return (GST: credit note against the original tax invoice, reducing output tax): shop and
/// customer details, credit note / return numbers, original invoice reference, the returned batches with qty, rate,
/// taxable value and GST, totals with the CGST / SGST (or IGST) reversal and round-off, and how it was settled.
/// A4 / A5 tabular; 80 / 58 mm thermal in a compact two-column form.
/// </summary>
public sealed class CreditNoteTemplate : IDocumentTemplate<SaleReturnDocument>
{
    public const string TemplateName = "CreditNote";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } =
        new[] { PaperSize.A4, PaperSize.A5, PaperSize.Thermal80mm, PaperSize.Thermal58mm };
    public PaperSize DefaultPaperSize => PaperSize.A5;

    public FlowDocument Render(SaleReturnDocument note, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        AddHeader(doc, note.IsCancelled ? "CREDIT NOTE (CANCELLED)" : "CREDIT NOTE", context);

        doc.Blocks.Add(Text($"Credit Note No: {note.CreditNoteNumber}   Date: {note.ReturnDate:dd-MM-yyyy}", size, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Return No: {note.ReturnNumber}   Against: {note.AgainstLabel}", size));
        var customer = $"Customer: {note.CustomerDisplay}" +
                       (string.IsNullOrWhiteSpace(note.CustomerPhone) ? "" : $"   Ph: {note.CustomerPhone}") +
                       (string.IsNullOrWhiteSpace(note.CustomerGstin) ? "" : $"   GSTIN: {note.CustomerGstin}");
        doc.Blocks.Add(Text(customer, size));
        if (!string.IsNullOrWhiteSpace(note.Reason)) doc.Blocks.Add(Text($"Reason: {note.Reason}", size));

        if (context.IsThermal) AddThermalLines(doc, note, size);
        else AddLinesTable(doc, note, size);
        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Totals(size, context.IsThermal, TotalRows(note)));

        if (!string.IsNullOrWhiteSpace(note.Notes)) doc.Blocks.Add(Text($"Notes: {note.Notes}", size - 1));
        if (note.IsCancelled)
            doc.Blocks.Add(Text($"Cancelled on {note.CancelledAt:dd-MM-yyyy}: {note.CancelReason}", size, FontWeights.SemiBold));
        doc.Blocks.Add(Text("Returned goods are accepted against the original invoice. GST charged on them is reversed by this credit note.",
            size - 1, margin: new Thickness(0, 8, 0, 0)));
        doc.Blocks.Add(Text(context.IsThermal
                ? $"For {context.Shop.Name}"
                : $"Customer signature: ____________________                    For {context.Shop.Name}",
            size, margin: new Thickness(0, 18, 0, 0)));
        return doc;
    }

    private static IEnumerable<(string Label, string Value, bool Bold)> TotalRows(SaleReturnDocument note)
    {
        yield return ("Taxable value", Money(note.TaxablePaisa), false);
        if (note.IgstPaisa > 0) yield return ("IGST", Money(note.IgstPaisa), false);
        else
        {
            yield return ("CGST", Money(note.CgstPaisa), false);
            yield return ("SGST", Money(note.SgstPaisa), false);
        }
        if (note.RoundOffPaisa != 0) yield return ("Round off", Signed(note.RoundOffPaisa), false);
        yield return ("Credit note value", Money(note.NetAmountPaisa), true);
        foreach (var refund in note.Refunds)
            yield return ($"Refunded {refund.RefundDate:dd-MM-yy} ({refund.MethodLabel})", Money(refund.AmountPaisa), false);
        if (!note.IsCancelled && note.CreditBalancePaisa > 0)
            yield return ("Credit available to customer", Money(note.CreditBalancePaisa), true);
    }

    private static void AddHeader(FlowDocument doc, string title, PrintContext context)
    {
        var size = context.BaseFontSize;
        var shop = context.Shop;
        var heading = context.CopyLabel is { } copy ? $"{title}  ({copy})" : title;
        doc.Blocks.Add(Text(heading, size + 1, FontWeights.SemiBold, TextAlignment.Center));
        doc.Blocks.Add(Text(shop.Name, size + 4, FontWeights.Bold, context.IsThermal ? TextAlignment.Center : TextAlignment.Left));
        if (!string.IsNullOrWhiteSpace(shop.Address)) doc.Blocks.Add(Text(shop.Address, size));
        var ids = new[]
        {
            string.IsNullOrWhiteSpace(shop.Phone) ? null : $"Ph: {shop.Phone}",
            shop.IsGstRegistered ? $"GSTIN: {shop.Gstin}" : null,
            string.IsNullOrWhiteSpace(shop.DrugLicenseNumber) ? null : $"DL: {shop.DrugLicenseNumber}"
        }.Where(s => s is not null);
        var line = string.Join("  |  ", ids);
        if (line.Length > 0) doc.Blocks.Add(Text(line, size - 1));
        doc.Blocks.Add(Rule());
    }

    private static void AddLinesTable(FlowDocument doc, SaleReturnDocument note, double size)
    {
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
                (Money(line.RatePerPackPaisa), TextAlignment.Right), (Money(line.TaxablePaisa), TextAlignment.Right),
                (line.GstPercent.ToString("0.##"), TextAlignment.Right), (Money(line.TaxPaisa), TextAlignment.Right),
                (Money(line.LineTotalPaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
    }

    private static void AddThermalLines(FlowDocument doc, SaleReturnDocument note, double size)
    {
        var table = Table(size - 1, new[] { ("ITEM", 3.0, TextAlignment.Left), ("QTY", 0.9, TextAlignment.Right), ("AMT", 1.3, TextAlignment.Right) });
        foreach (var line in note.Lines)
        {
            AddRow(table, new[]
            {
                ($"{line.MedicineName} ({line.BatchNumber} {line.ExpiryLabel}) GST {line.GstPercent:0.##}%", TextAlignment.Left),
                (line.QuantityDisplay, TextAlignment.Right),
                (Money(line.LineTotalPaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
    }

    private static Table Totals(double size, bool thermal, IEnumerable<(string Label, string Value, bool Bold)> rows)
    {
        var table = Table(size, new[] { ("", thermal ? 2.0 : 3.0, TextAlignment.Right), ("", 1.0, TextAlignment.Right) });
        table.RowGroups[0].Rows.Clear();
        foreach (var (label, value, bold) in rows)
            AddRow(table, new[] { (label, TextAlignment.Right), (value, TextAlignment.Right) }, bold ? FontWeights.SemiBold : null);
        return table;
    }

    private static string Signed(long paisa) => paisa == 0 ? "0.00" : (paisa > 0 ? "+" : "-") + Money(Math.Abs(paisa));
}
