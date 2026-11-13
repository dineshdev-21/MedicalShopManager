using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using static MedicalShopManager.App.Printing.DocumentBuilder;
using static MedicalShopManager.App.Modules.Finance.Printing.FinancePrintBlocks;

namespace MedicalShopManager.App.Modules.Finance.Printing;

/// <summary>
/// Payment receipt (Payment In from a customer) or payment voucher (Payment Out to a supplier): number, date, party,
/// mode with cheque / reference details, the bills settled and any amount kept on account, the amount in words and a
/// signature line. A cancelled or bounced voucher prints with its status in the title. A5 by default; thermal compact.
/// </summary>
public sealed class FinanceVoucherTemplate : IDocumentTemplate<FinanceVoucher>
{
    public const string TemplateName = "FinanceVoucher";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } =
        new[] { PaperSize.A4, PaperSize.A5, PaperSize.Thermal80mm, PaperSize.Thermal58mm };
    public PaperSize DefaultPaperSize => PaperSize.A5;

    public FlowDocument Render(FinanceVoucher voucher, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = NewDocument();
        var title = voucher.Kind == FinanceVoucherKind.Receipt ? "PAYMENT RECEIPT" : "PAYMENT VOUCHER";
        if (!voucher.IsActive) title += $" ({voucher.StatusLabel.ToUpperInvariant()})";
        AddHeader(doc, title, context);

        doc.Blocks.Add(Text($"No: {voucher.VoucherNumber}   Date: {voucher.VoucherDate:dd-MM-yyyy}", size, FontWeights.SemiBold));
        var partyLabel = voucher.Kind == FinanceVoucherKind.Receipt ? "Received from" : "Paid to";
        doc.Blocks.Add(Text($"{partyLabel}: {voucher.PartyName}" + (string.IsNullOrWhiteSpace(voucher.PartyPhone) ? "" : $"   Ph: {voucher.PartyPhone}"), size));
        doc.Blocks.Add(Text($"Mode: {voucher.ModeLabel}" + (voucher.AccountName is { } account ? $"   Account: {account}" : ""), size));
        if (voucher.ChequeNumber is { } cheque)
            doc.Blocks.Add(Text($"Cheque No: {cheque}   Dated: {voucher.ChequeDate:dd-MM-yyyy}" +
                                (string.IsNullOrWhiteSpace(voucher.ChequeBank) ? "" : $"   Bank: {voucher.ChequeBank}") +
                                (voucher.ChequeStatus is { } status ? $"   Status: {status.ToLabel()}" : ""), size));
        if (!string.IsNullOrWhiteSpace(voucher.Reference)) doc.Blocks.Add(Text($"Reference: {voucher.Reference}", size));

        if (voucher.Allocations.Count > 0)
        {
            var table = Table(size - 1, new[]
            {
                ("AGAINST", 2.4, TextAlignment.Left), ("TYPE", 1.2, TextAlignment.Left), ("AMOUNT", 1.2, TextAlignment.Right)
            });
            foreach (var a in voucher.Allocations)
                AddRow(table, new[]
                {
                    (a.Number, TextAlignment.Left),
                    (a.Target switch { AllocationTarget.Sale => "Sale bill", AllocationTarget.Purchase => "Purchase bill", _ => "Opening bal." }, TextAlignment.Left),
                    (Money(a.AmountPaisa), TextAlignment.Right)
                });
            doc.Blocks.Add(table);
        }
        doc.Blocks.Add(Rule());

        var onAccount = voucher.AmountPaisa - voucher.Allocations.Sum(a => a.AmountPaisa);
        var rows = new List<(string, string, bool)>();
        if (voucher.Allocations.Count > 0) rows.Add(("Against bills", Money(voucher.AmountPaisa - onAccount), false));
        if (onAccount > 0) rows.Add(("On account (advance)", Money(onAccount), false));
        rows.Add((voucher.Kind == FinanceVoucherKind.Receipt ? "Amount received" : "Amount paid", Money(voucher.AmountPaisa), true));
        doc.Blocks.Add(Totals(size, context.IsThermal, rows));
        doc.Blocks.Add(Text(FinanceAmountWords.Rupees(voucher.AmountPaisa), size - 1, FontWeights.SemiBold));

        if (!string.IsNullOrWhiteSpace(voucher.Notes)) doc.Blocks.Add(Text($"Notes: {voucher.Notes}", size - 1));
        if (!voucher.IsActive && voucher.ReversedAt is { } reversed)
            doc.Blocks.Add(Text($"{voucher.StatusLabel} on {reversed:dd-MM-yyyy}: {voucher.ReversalReason}", size, FontWeights.SemiBold));
        if (voucher.Mode == FinancePaymentMode.Cheque && voucher.IsActive)
            doc.Blocks.Add(Text("Subject to realisation of the cheque.", size - 1, margin: new Thickness(0, 6, 0, 0)));
        AddSignature(doc, context, voucher.Kind == FinanceVoucherKind.Receipt ? "Received by" : "Receiver's signature");
        return doc;
    }
}
