using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// A realistic sample bill for the Print Layout preview, priced with <see cref="PricingCalculator"/> so its totals
/// add up exactly like a real bill. Line 1 is the Localwell golden vector (1 pack + 5 loose of 15's at ₹188.40 MRP,
/// 10% off). Nothing is saved.
/// </summary>
public static class SampleInvoiceFactory
{
    private sealed record SampleLine(string Name, string Hsn, string Batch, int UnitsPerPack, long MrpPaisa, long Units, decimal Discount, decimal Gst);

    private static readonly SampleLine[] Lines =
    {
        new("Pan-D Capsule 15's", "30049099", "PD24117", 15, 18840, 20, 10m, 12m),
        new("Dolo 650 Tablet 15's", "30049063", "DOB4512", 15, 3364, 15, 0m, 12m),
        new("Volini Gel 30g", "30049099", "VG2309", 1, 15500, 1, 5m, 18m)
    };

    public static Sale Create(DateTime invoiceDate, string invoiceNumber, RoundOffMode roundOff = RoundOffMode.NearestRupee)
    {
        var amounts = Lines.Select(l => PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = l.MrpPaisa,
            MrpPerPackPaisa = l.MrpPaisa,
            UnitsPerPack = l.UnitsPerPack,
            Units = l.Units,
            DiscountPercent = l.Discount,
            GstPercent = l.Gst
        })).ToList();
        var totals = PricingCalculator.SummarizeBill(amounts, roundOff);

        var sale = new Sale
        {
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            CustomerName = "Sample Patient",
            CustomerPhone = "9876543210",
            PrescriptionRef = "Dr. A. Sharma",
            SubtotalPaisa = totals.GrossPaisa,
            DiscountAmountPaisa = totals.DiscountPaisa,
            TaxableAmountPaisa = totals.TaxablePaisa,
            CgstAmountPaisa = totals.CgstPaisa,
            SgstAmountPaisa = totals.SgstPaisa,
            IgstAmountPaisa = totals.IgstPaisa,
            GrandTotalPaisa = totals.NetPayablePaisa,
            AmountPaidPaisa = totals.NetPayablePaisa,
            BalanceDuePaisa = 0,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Paid
        };

        for (var i = 0; i < Lines.Length; i++)
        {
            var l = Lines[i];
            var a = amounts[i];
            sale.Items.Add(new SaleItem
            {
                MedicineName = l.Name,
                HsnCode = l.Hsn,
                BatchNumber = l.Batch,
                ExpiryDate = new DateTime(invoiceDate.Year + 1, 6, 30),
                Quantity = (int)l.Units,
                UnitPricePaisa = PricingCalculator.RoundPaisa(PricingCalculator.UnitRatePaisa(l.MrpPaisa, l.UnitsPerPack)),
                DiscountPercent = l.Discount,
                DiscountAmountPaisa = a.LineDiscountPaisa,
                TaxableValuePaisa = a.TaxablePaisa,
                GstRatePercent = l.Gst,
                CgstAmountPaisa = a.CgstPaisa,
                SgstAmountPaisa = a.SgstPaisa,
                IgstAmountPaisa = a.IgstPaisa,
                TaxAmountPaisa = a.TaxPaisa,
                LineTotalPaisa = a.TotalPaisa
            });
        }
        return sale;
    }
}
