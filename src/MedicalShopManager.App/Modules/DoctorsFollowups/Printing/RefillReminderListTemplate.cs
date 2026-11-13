using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.Printing;

/// <summary>
/// Refill reminder call list: patient, mobile, medicine, last purchase, consumption, due date and status, with a
/// blank "Remarks" column for the person calling. A4 / A5; thermal paper gets a compact one-column list.
/// </summary>
public sealed class RefillReminderListTemplate : IDocumentTemplate<RefillReminderList>
{
    public string Name => "RefillReminderList";
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5, PaperSize.Thermal80mm };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(RefillReminderList list, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        doc.Blocks.Add(Text(context.Shop.Name, size + (context.IsThermal ? 2 : 4), FontWeights.Bold));
        doc.Blocks.Add(Text(list.Title, size + 1, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Generated {list.GeneratedAt:dd-MM-yyyy HH:mm}  ·  {list.Items.Count} reminder(s)", size - 1));
        doc.Blocks.Add(Rule());

        if (context.IsThermal)
        {
            foreach (var f in list.Items)
            {
                doc.Blocks.Add(Text($"{f.PatientDisplay}  {f.PhoneDisplay}", size, FontWeights.SemiBold));
                doc.Blocks.Add(Text($"{f.MedicineName}  ·  {f.DueDate:dd-MM-yy}  ·  {f.DueLabel}", size - 1));
                doc.Blocks.Add(Rule(0.5));
            }
            return doc;
        }

        var table = DocumentBuilder.Table(size - 1, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("Patient", 2.0, TextAlignment.Left), ("Mobile", 1.2, TextAlignment.Left),
            ("Medicine", 2.4, TextAlignment.Left), ("Last bought", 1.3, TextAlignment.Left), ("Per day", 0.7, TextAlignment.Right),
            ("Due", 0.9, TextAlignment.Left), ("Status", 1.3, TextAlignment.Left), ("Remarks", 1.8, TextAlignment.Left)
        });
        var n = 1;
        foreach (var f in list.Items)
        {
            AddRow(table, new[]
            {
                ((n++).ToString(), TextAlignment.Left), (f.PatientDisplay, TextAlignment.Left), (f.PhoneDisplay, TextAlignment.Left),
                (f.ItemDisplay, TextAlignment.Left), ($"{f.QuantityUnits} on {f.SaleDate:dd-MM-yy}", TextAlignment.Left),
                (f.UnitsPerDay.ToString("0.##"), TextAlignment.Right), (f.DueDate.ToString("dd-MM-yy"), TextAlignment.Left),
                (f.DueLabel, TextAlignment.Left), ("", TextAlignment.Left)
            });
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Text("Units left and due dates are estimates from the quantity bought and the daily consumption.", size - 2));
        return doc;
    }
}
