using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Billing.Printing;

/// <summary>
/// The sale invoice layout (spec 02 "Print/export outputs"), shared by the BillingInvoice and legacy Sale templates.
/// A4 / A5: full column table (# · DESCRIPTION · PACK · SCH · RACK · HSN · BATCH · EXP · QTY · MRP · RATE · DIS% · GST% ·
/// AMT, each optional per Control Center ▸ Print Layout), GST rate summary, totals with round-off and savings, amount
/// in words, bank / UPI, terms, signatures. Thermal 80 / 58 mm: a compact receipt. Title is TAX INVOICE for a
/// GST-registered shop billing with GST, else BILL OF SUPPLY.
/// </summary>
public static class SaleInvoiceLayout
{
    private sealed record Column(string Header, double Width, TextAlignment Align, Func<BillingInvoiceLine, int, string> Value);

    public static FlowDocument Render(SaleInvoicePrintModel model, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        var invoice = model.Invoice;
        var settings = model.Settings;
        var size = context.BaseFontSize;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };

        var title = invoice.Status == SaleStatus.Cancelled ? $"{invoice.DocumentTitle} (CANCELLED)" : invoice.DocumentTitle;
        if (context.CopyLabel is { } copy && (settings.IsEnabled(InvoiceField.PrintCopyLabels) || copy == "SAMPLE")) title += $"  ({copy})";
        doc.Blocks.Add(Text(title, size, FontWeights.SemiBold, TextAlignment.Center));

        AddShopHeader(doc, model, context);
        doc.Blocks.Add(Rule());
        AddPartyBlock(doc, model, size);

        doc.Blocks.Add(context.IsThermal ? ThermalItems(invoice, settings, size) : FullItems(invoice, settings, size));
        doc.Blocks.Add(Rule());

        if (!context.IsThermal && invoice.IsTaxInvoice && settings.IsEnabled(InvoiceField.ShowGstSummary) && invoice.TaxPaisa > 0)
            doc.Blocks.Add(GstSummary(invoice, size - 1));

        doc.Blocks.Add(Totals(invoice, settings, size, context.IsThermal));
        if (settings.IsEnabled(InvoiceField.ShowAmountInWords))
            doc.Blocks.Add(Text(BillingInvoiceText.AmountInWords(invoice.GrandTotalPaisa), size - 1, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Products: {invoice.Lines.Count}   Total Qty: {invoice.TotalUnits}", size - 1));
        if (!string.IsNullOrWhiteSpace(invoice.Notes)) doc.Blocks.Add(Text($"Remarks: {invoice.Notes}", size - 1));

        AddFooter(doc, model, context);
        return doc;
    }

    private static void AddShopHeader(FlowDocument doc, SaleInvoicePrintModel model, PrintContext context)
    {
        var shop = context.Shop;
        var settings = model.Settings;
        var size = context.BaseFontSize;
        var align = context.IsThermal ? TextAlignment.Center : TextAlignment.Left;
        doc.Blocks.Add(Text(shop.Name, size + (context.IsThermal ? 3 : 5), FontWeights.Bold, align));
        if (!string.IsNullOrWhiteSpace(shop.Address)) doc.Blocks.Add(Text(shop.Address, size - (context.IsThermal ? 1 : 0), align: align));

        var phone = string.IsNullOrWhiteSpace(settings.ContactNumber) ? shop.Phone : settings.ContactNumber;
        var ids = new List<string?>
        {
            string.IsNullOrWhiteSpace(phone) ? null : $"Ph: {phone}",
            settings.IsEnabled(InvoiceField.ShowEmail) && !string.IsNullOrWhiteSpace(model.Business?.Email) ? $"Email: {model.Business!.Email}" : null,
            settings.IsEnabled(InvoiceField.ShowGstin) && shop.IsGstRegistered ? $"GSTIN: {shop.Gstin}" : null,
            settings.IsEnabled(InvoiceField.ShowPan) && !string.IsNullOrWhiteSpace(model.Business?.Pan) ? $"PAN: {model.Business!.Pan}" : null,
            settings.IsEnabled(InvoiceField.ShowDrugLicense) && !string.IsNullOrWhiteSpace(shop.DrugLicenseNumber) ? $"DL: {shop.DrugLicenseNumber}" : null,
            settings.IsEnabled(InvoiceField.ShowFssai) && !string.IsNullOrWhiteSpace(model.Business?.Fssai) ? $"FSSAI: {model.Business!.Fssai}" : null
        };
        var line = string.Join("  |  ", ids.Where(s => s is not null));
        if (line.Length > 0) doc.Blocks.Add(Text(line, size - 1, align: align));
    }

    private static void AddPartyBlock(FlowDocument doc, SaleInvoicePrintModel model, double size)
    {
        var invoice = model.Invoice;
        var settings = model.Settings;
        doc.Blocks.Add(Text($"Invoice: {invoice.InvoiceNumber}    Date: {invoice.InvoiceDate:dd-MM-yyyy HH:mm}", size, FontWeights.SemiBold));

        var customer = string.IsNullOrWhiteSpace(invoice.CustomerName) ? "Cash / Counter Sale" : invoice.CustomerName;
        var patient = string.IsNullOrWhiteSpace(invoice.PatientName) ? customer : invoice.PatientName;
        var ageGender = settings.IsEnabled(InvoiceField.ShowPatientAgeGender)
            ? string.Join(" | ", new[]
            {
                invoice.PatientGender == PersonGender.Unspecified ? null : invoice.PatientGender.ToString(),
                string.IsNullOrWhiteSpace(invoice.PatientAge) ? null : $"{invoice.PatientAge} yrs"
            }.Where(s => s is not null))
            : string.Empty;
        doc.Blocks.Add(Text($"Patient: {patient}" + (ageGender.Length > 0 ? $" ({ageGender})" : string.Empty)
                            + (string.IsNullOrWhiteSpace(invoice.CustomerPhone) ? string.Empty : $"    Mob: {invoice.CustomerPhone}"), size));
        if (!string.Equals(patient, customer, StringComparison.Ordinal) && !invoice.IsCounterSale)
            doc.Blocks.Add(Text($"Customer: {customer}", size));
        var address = invoice.PatientAddress ?? invoice.CustomerAddress;
        if (settings.IsEnabled(InvoiceField.ShowPatientAddress) && !string.IsNullOrWhiteSpace(address))
            doc.Blocks.Add(Text($"Address: {address}", size - 1));
        if (settings.IsEnabled(InvoiceField.ShowCustomerGstin) && !string.IsNullOrWhiteSpace(invoice.CustomerGstin))
            doc.Blocks.Add(Text($"Customer GSTIN: {invoice.CustomerGstin}", size - 1));
        if (settings.IsEnabled(InvoiceField.ShowPlaceOfSupply) && model.Business?.State is { } state)
        {
            var pos = invoice.IsInterState && invoice.CustomerGstin is { Length: >= 2 } g ? IndianStates.Find(g[..2]) ?? state : state;
            doc.Blocks.Add(Text($"Place of Supply: {pos.Code} - {pos.Name}", size - 1));
        }
        if (settings.IsEnabled(InvoiceField.ShowDoctor) && !string.IsNullOrWhiteSpace(invoice.DoctorName))
            doc.Blocks.Add(Text($"Doctor: {invoice.DoctorName}" + (string.IsNullOrWhiteSpace(invoice.DoctorRegNo) ? string.Empty : $" (Reg. {invoice.DoctorRegNo})"), size));
        if (!string.IsNullOrWhiteSpace(invoice.PrescriptionNumber)) doc.Blocks.Add(Text($"Rx No: {invoice.PrescriptionNumber}", size - 1));
        if (invoice.BalanceDuePaisa > 0 && invoice.DueDate is { } due)
            doc.Blocks.Add(Text($"Due: ₹{Money(invoice.BalanceDuePaisa)} by {due:dd-MM-yyyy}", size, FontWeights.SemiBold));
    }

    private static string Quantity(BillingInvoiceLine line, InvoicePrintSettings settings)
    {
        var paid = settings.IsEnabled(InvoiceField.QtyInPacks) ? BillingFormat.Quantity(line.PaidUnits, line.UnitsPerPack) : line.PaidUnits.ToString();
        return line.FreeUnits > 0 ? $"{paid}+{line.FreeUnits}F" : paid;
    }

    private static Table FullItems(BillingInvoice invoice, InvoicePrintSettings s, double size)
    {
        var columns = new List<Column>
        {
            new("#", 0.4, TextAlignment.Left, (_, i) => i.ToString()),
            new("DESCRIPTION", 3.0, TextAlignment.Left, (l, _) => l.MedicineName
                + (s.IsEnabled(InvoiceField.ShowManufacturer) && !string.IsNullOrWhiteSpace(l.Manufacturer) ? $"\n{l.Manufacturer}" : string.Empty))
        };
        if (s.IsEnabled(InvoiceField.ShowPacking)) columns.Add(new("PACK", 0.6, TextAlignment.Left, (l, _) => $"{l.UnitsPerPack}'S"));
        if (s.IsEnabled(InvoiceField.ShowSchedule)) columns.Add(new("SCH", 0.5, TextAlignment.Left, (l, _) => l.Schedule == DrugSchedule.None ? "" : l.ScheduleLabel));
        if (s.IsEnabled(InvoiceField.ShowRackLocation)) columns.Add(new("RACK", 0.6, TextAlignment.Left, (l, _) => l.RackLocation ?? ""));
        if (s.IsEnabled(InvoiceField.ShowHsn)) columns.Add(new("HSN", 0.9, TextAlignment.Left, (l, _) => l.HsnCode ?? ""));
        if (s.IsEnabled(InvoiceField.ShowBatch)) columns.Add(new("BATCH", 1.1, TextAlignment.Left, (l, _) => l.BatchNumber));
        if (s.IsEnabled(InvoiceField.ShowExpiry)) columns.Add(new("EXP", 0.6, TextAlignment.Left, (l, _) => l.ExpiryDate.ToString("MM/yy")));
        columns.Add(new("QTY", 0.8, TextAlignment.Right, (l, _) => Quantity(l, s)));
        if (s.IsEnabled(InvoiceField.ShowMrp)) columns.Add(new("MRP", 0.9, TextAlignment.Right, (l, _) => Money(l.MrpPerPackPaisa)));
        columns.Add(new("RATE", 0.9, TextAlignment.Right, (l, _) => Money(l.NetRatePerPackPaisa)));
        if (s.IsEnabled(InvoiceField.ShowDiscount)) columns.Add(new("DIS%", 0.6, TextAlignment.Right, (l, _) => l.DiscountPercent.ToString("0.##")));
        if (invoice.IsTaxInvoice && s.IsEnabled(InvoiceField.ShowGstRate)) columns.Add(new("GST%", 0.6, TextAlignment.Right, (l, _) => l.GstPercent.ToString("0.##")));
        columns.Add(new("AMT", 1.1, TextAlignment.Right, (l, _) => Money(l.LineTotalPaisa)));

        var table = DocumentBuilder.Table(size - 0.5, columns.Select(c => (c.Header, c.Width, c.Align)).ToList());
        var n = 1;
        foreach (var line in invoice.Lines)
        {
            var index = n++;
            AddRow(table, columns.Select(c => (c.Value(line, index), c.Align)).ToList());
        }
        return table;
    }

    private static Table ThermalItems(BillingInvoice invoice, InvoicePrintSettings s, double size)
    {
        var table = DocumentBuilder.Table(size, new[]
        {
            ("Item / Batch / Exp", 3.0, TextAlignment.Left), ("Qty x Rate", 1.7, TextAlignment.Right), ("Amt", 1.3, TextAlignment.Right)
        });
        foreach (var line in invoice.Lines)
        {
            var schedule = line.Schedule == DrugSchedule.None ? string.Empty : $" [{line.ScheduleLabel}]";
            AddRow(table, new[]
            {
                ($"{line.MedicineName}{schedule}\n{line.BatchNumber}  {line.ExpiryDate:MM/yy}", TextAlignment.Left),
                ($"{Quantity(line, s)} x {Money(line.NetRatePerPackPaisa)}", TextAlignment.Right),
                (Money(line.LineTotalPaisa), TextAlignment.Right)
            });
        }
        return table;
    }

    private static Table GstSummary(BillingInvoice invoice, double size)
    {
        var table = DocumentBuilder.Table(size, invoice.IsInterState
            ? new[] { ("GST %", 1.0, TextAlignment.Left), ("Taxable", 1.5, TextAlignment.Right), ("IGST", 1.5, TextAlignment.Right), ("Total Tax", 1.5, TextAlignment.Right) }
            : new[] { ("GST %", 1.0, TextAlignment.Left), ("Taxable", 1.5, TextAlignment.Right), ("CGST", 1.5, TextAlignment.Right), ("SGST", 1.5, TextAlignment.Right), ("Total Tax", 1.5, TextAlignment.Right) });
        foreach (var row in BillingInvoiceText.GstByRate(invoice.Lines))
        {
            var cells = new List<(string, TextAlignment)> { (row.GstPercent.ToString("0.##"), TextAlignment.Left), (Money(row.TaxablePaisa), TextAlignment.Right) };
            if (invoice.IsInterState) cells.Add((Money(row.IgstPaisa), TextAlignment.Right));
            else
            {
                cells.Add((Money(row.CgstPaisa), TextAlignment.Right));
                cells.Add((Money(row.SgstPaisa), TextAlignment.Right));
            }
            cells.Add((Money(row.TaxPaisa), TextAlignment.Right));
            AddRow(table, cells);
        }
        return table;
    }

    private static Table Totals(BillingInvoice invoice, InvoicePrintSettings s, double size, bool thermal)
    {
        var table = DocumentBuilder.Table(size, new[] { ("", thermal ? 2.2 : 3.0, TextAlignment.Right), ("", 1.2, TextAlignment.Right) });
        void Line(string label, long paisa, FontWeight? weight = null) =>
            AddRow(table, new[] { (label, TextAlignment.Right), (Money(paisa), TextAlignment.Right) }, weight);

        if (invoice.MrpTotalPaisa > 0 && s.IsEnabled(InvoiceField.ShowMrp)) Line("MRP Total", invoice.MrpTotalPaisa);
        Line("Subtotal", invoice.SubtotalPaisa);
        if (invoice.DiscountPaisa != 0) Line("(-) Discount", invoice.DiscountPaisa);
        if (invoice.IsTaxInvoice)
        {
            Line("Taxable Value", invoice.TaxablePaisa);
            if (invoice.IgstPaisa != 0) Line("IGST", invoice.IgstPaisa);
            else
            {
                Line("CGST", invoice.CgstPaisa);
                Line("SGST", invoice.SgstPaisa);
            }
        }
        if (invoice.CustomChargePaisa != 0) Line($"(+) {invoice.CustomChargeName ?? "Charges"}", invoice.CustomChargePaisa);
        if (invoice.RoundOffPaisa != 0 && s.IsEnabled(InvoiceField.ShowRoundOff)) Line("Round Off", invoice.RoundOffPaisa);
        Line("TO PAY (₹)", invoice.GrandTotalPaisa, FontWeights.Bold);
        if (s.IsEnabled(InvoiceField.ShowSavings) && invoice.SavingsPaisa > 0) Line("You Saved", invoice.SavingsPaisa);
        if (s.IsEnabled(InvoiceField.ShowPaymentMode))
        {
            if (invoice.AmountPaidPaisa != 0) Line($"Paid ({PaymentLabel(invoice)})", invoice.AmountPaidPaisa);
            if (invoice.ChangeReturnedPaisa > 0) Line("Change Returned", invoice.ChangeReturnedPaisa);
        }
        if (invoice.BalanceDuePaisa != 0) Line("Balance Due", invoice.BalanceDuePaisa, FontWeights.SemiBold);
        return table;
    }

    private static string PaymentLabel(BillingInvoice invoice) =>
        invoice.Payments.Count > 1
            ? string.Join(" + ", invoice.Payments.Select(p => p.Method.ToString()).Distinct())
            : invoice.PaymentMethod.ToString();

    private static void AddFooter(FlowDocument doc, SaleInvoicePrintModel model, PrintContext context)
    {
        var settings = model.Settings;
        var size = context.BaseFontSize - 1;
        var business = model.Business;
        if (!context.IsThermal && settings.IsEnabled(InvoiceField.ShowBankDetails) && business is { HasBankDetails: true })
            doc.Blocks.Add(Text($"Bank: {business.BankName}  A/C: {business.BankAccountNumber}  IFSC: {business.BankIfsc}  Holder: {business.BankAccountName}", size));
        if (settings.IsEnabled(InvoiceField.ShowUpiQr) && !string.IsNullOrWhiteSpace(business?.UpiId))
            doc.Blocks.Add(Text($"Pay by UPI: {business!.UpiId}", size, FontWeights.SemiBold, context.IsThermal ? TextAlignment.Center : TextAlignment.Left));
        if (settings.IsEnabled(InvoiceField.ShowTerms) && !string.IsNullOrWhiteSpace(settings.TermsText))
            doc.Blocks.Add(Text(settings.TermsText, size - 0.5, margin: new Thickness(0, 4, 0, 0)));
        if (!string.IsNullOrWhiteSpace(settings.FooterNote))
            doc.Blocks.Add(Text(settings.FooterNote + "  E & OE", size, align: TextAlignment.Center));

        var signature = new List<string>();
        if (settings.IsEnabled(InvoiceField.ShowPharmacist) && !string.IsNullOrWhiteSpace(context.Shop.PharmacistName))
            signature.Add($"Pharmacist: {context.Shop.PharmacistName}" + (string.IsNullOrWhiteSpace(business?.PharmacistRegistrationNo) ? string.Empty : $" (Reg. {business!.PharmacistRegistrationNo})"));
        if (settings.IsEnabled(InvoiceField.ShowBillingUser))
        {
            var by = model.Invoice.CreatedByName ?? model.PrintedBy;
            if (!string.IsNullOrWhiteSpace(by)) signature.Add($"Made by {by} at {model.Invoice.CreatedAt.ToLocalTime():dd/MMM HH:mm}");
        }
        if (!context.IsThermal && settings.IsEnabled(InvoiceField.ShowSignature)) signature.Add("Auth. Sign");
        if (signature.Count > 0)
            doc.Blocks.Add(Text(string.Join("     ", signature), size, align: TextAlignment.Right, margin: new Thickness(0, 10, 0, 0)));
    }
}
