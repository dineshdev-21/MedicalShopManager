using MedicalShopManager.Core.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>Save validations (spec 04 "Save validations", messages as the web app shows them).</summary>
public class PurchasesValidatorTests
{
    private static readonly DateTime Today = new(2026, 11, 4);

    private static PurchaseBill ValidBill()
    {
        var bill = new PurchaseBill
        {
            SupplierId = 1, SupplierName = "Alpha Pharma", SupplierInvoiceNumber = "INV-1", InvoiceDate = Today,
            Lines =
            {
                new PurchaseBillLine
                {
                    MedicineId = 7, MedicineName = "Dolo 650", HsnCode = "30049099", BatchNumber = "DL123",
                    ExpiryDate = new DateTime(2028, 1, 31), UnitsPerPack = 15, Quantity = 150, EnteredRatePaisa = 2_500,
                    MrpPaisa = 3_400, SaleRatePaisa = 3_400, GstRatePercent = 12
                }
            }
        };
        PurchaseCalculator.Recalculate(bill);
        return bill;
    }

    [Fact]
    public void ValidBill_HasNoErrors() => Assert.Empty(PurchaseValidator.ValidatePurchase(ValidBill(), Today));

    [Fact]
    public void HeaderErrors_InSpecOrder()
    {
        var bill = new PurchaseBill { InvoiceDate = Today };
        var errors = PurchaseValidator.ValidatePurchase(bill, Today, initialPaymentPaisa: -1);
        Assert.Equal(new[] { "Please add Products", "Please add Invoice No", "Please add Distributor", "Payment Amount cannot be negative" }, errors);
    }

    [Fact]
    public void FutureInvoiceDate_AndDueBeforeInvoice_AreRejected()
    {
        var bill = ValidBill();
        bill.InvoiceDate = Today.AddDays(1);
        bill.DueDate = Today;
        var errors = PurchaseValidator.ValidatePurchase(bill, Today);
        Assert.Contains("Invoice Date cannot be in the future.", errors);
        Assert.Contains("Payment Due Date cannot be before the Invoice Date.", errors);
    }

    [Fact]
    public void OtherCharges_NeedAType()
    {
        var bill = ValidBill();
        bill.OtherChargesPaisa = 500;
        Assert.Contains("Please add custom charge type", PurchaseValidator.ValidatePurchase(bill, Today));
    }

    [Fact]
    public void RowErrors_NameTheRow()
    {
        var bill = ValidBill();
        var line = bill.Lines[0];
        line.HsnCode = "12";
        line.BatchNumber = " ";
        line.MrpPaisa = 0;
        line.SaleRatePaisa = 0;
        line.ExpiryDate = Today.AddDays(-1);
        line.GstRatePercent = 40;
        var errors = PurchaseValidator.ValidatePurchase(bill, Today);

        Assert.Contains("Please fill a valid HSN Code on Row: 1", errors);
        Assert.Contains("Please fill Batch No on Row: 1", errors);
        Assert.Contains("Please fill Batch MRP on Row: 1", errors);
        Assert.Contains("Please fill Batch Sale Rate on Row: 1", errors);
        Assert.Contains("Batch is Expired on Row: 1", errors);
        Assert.Contains("Please fill Batch GST Rate on Row: 1", errors);
    }

    [Fact]
    public void SaleRateAboveMrp_AndRateAboveMrp_AreRejected()
    {
        var bill = ValidBill();
        bill.Lines[0].SaleRatePaisa = 3_500;
        bill.Lines[0].EnteredRatePaisa = 3_600;
        PurchaseCalculator.Recalculate(bill);
        var errors = PurchaseValidator.ValidatePurchase(bill, Today);
        Assert.Contains("Sale Rate cannot be more than MRP on Row: 1", errors);
        Assert.Contains("Purchase Rate is more than MRP on Row: 1", errors);
    }

    [Fact]
    public void ZeroRate_IsAllowedOnlyForFreeOnlyRows()
    {
        var bill = ValidBill();
        bill.Lines[0].EnteredRatePaisa = 0;
        Assert.Contains("Please fill PTR on Row: 1", PurchaseValidator.ValidatePurchase(bill, Today));

        bill.Lines[0].Quantity = 0;
        bill.Lines[0].FreeQuantity = 15;
        Assert.DoesNotContain("Please fill PTR on Row: 1", PurchaseValidator.ValidatePurchase(bill, Today));
    }

    [Fact]
    public void ZeroQuantity_IsRejected()
    {
        var bill = ValidBill();
        bill.Lines[0].Quantity = 0;
        Assert.Contains("Please fill Quantity on Row: 1", PurchaseValidator.ValidatePurchase(bill, Today));
    }

    [Fact]
    public void RepeatedBatchRows_AreRejected()
    {
        var bill = ValidBill();
        var copy = bill.Lines[0];
        bill.Lines.Add(new PurchaseBillLine
        {
            MedicineId = copy.MedicineId, MedicineName = copy.MedicineName, HsnCode = copy.HsnCode, BatchNumber = "dl123",
            ExpiryDate = copy.ExpiryDate, UnitsPerPack = 15, Quantity = 15, EnteredRatePaisa = 2_500, MrpPaisa = 3_400,
            SaleRatePaisa = 3_400, GstRatePercent = 12
        });
        Assert.Contains(PurchaseValidator.ValidatePurchase(bill, Today), e => e.StartsWith("Row: 2 repeats", StringComparison.Ordinal));
    }

    [Fact]
    public void EditOfAnOldBill_ChecksExpiryAgainstTheInvoiceDate()
    {
        var bill = ValidBill();
        bill.InvoiceDate = new DateTime(2025, 1, 10);
        bill.Lines[0].ExpiryDate = new DateTime(2026, 6, 30); // expired today, valid when bought
        Assert.Contains("Batch is Expired on Row: 1", PurchaseValidator.ValidatePurchase(bill, Today));
        Assert.Empty(PurchaseValidator.ValidatePurchase(bill, Today, expiryCutoff: bill.InvoiceDate));
    }

    [Theory]
    [InlineData("29ABCDE1234F1Z5", true)]
    [InlineData("29abcde1234f1z5", true)]
    [InlineData("29ABCDE1234F1X5", false)]
    [InlineData("ABCDE1234F1Z5", false)]
    [InlineData("", false)]
    public void Gstin_Format(string gstin, bool valid) => Assert.Equal(valid, PurchaseValidator.IsValidGstin(gstin));

    [Theory]
    [InlineData("9876543210", true)]
    [InlineData("+91 98765 43210", true)]
    [InlineData("098765-43210", true)]
    [InlineData("12345", false)]
    public void Phone_Format(string phone, bool valid) => Assert.Equal(valid, PurchaseValidator.IsValidPhone(phone));

    [Fact]
    public void Distributor_Rules()
    {
        Assert.Empty(PurchaseValidator.ValidateDistributor(new Distributor { Name = "Alpha", Gstin = "29ABCDE1234F1Z5", StateCode = "29", CreditDays = 30, Pincode = "560001" }));

        var errors = PurchaseValidator.ValidateDistributor(new Distributor
        {
            Name = " ", Gstin = "29ABCDE1234F1Z5", StateCode = "27", Phone = "123", Pincode = "0123", CreditDays = 400, Email = "x"
        });
        Assert.Contains("Please add Distributor Name", errors);
        Assert.Contains("State does not match the first two digits of the GSTIN.", errors);
        Assert.Contains("Please enter a valid 10-digit phone number.", errors);
        Assert.Contains("Please enter a valid 6-digit pincode.", errors);
        Assert.Contains("Credit period must be between 0 and 365 days.", errors);
        Assert.Contains("Please enter a valid email address.", errors);
    }

    [Fact]
    public void Return_Rules()
    {
        var doc = new PurchaseReturnDocument { ReturnDate = Today.AddDays(1), RefundReceivedPaisa = -1 };
        var errors = PurchaseValidator.ValidateReturn(doc, Today);
        Assert.Contains("Please add Distributor", errors);
        Assert.Contains("Return Date cannot be in the future.", errors);
        Assert.Contains("Please add Products to return", errors);
        Assert.Contains("Refund amount cannot be negative.", errors);

        var ok = new PurchaseReturnDocument
        {
            SupplierId = 1, ReturnDate = Today,
            Lines = { new PurchaseReturnLine { BatchId = 5, MedicineId = 1, MedicineName = "Dolo", BatchNumber = "B", UnitsPerPack = 15, Quantity = 20, RatePaisa = 100, GstRatePercent = 12 } }
        };
        Assert.Empty(PurchaseValidator.ValidateReturn(ok, Today));
        Assert.Contains("Only 10 units of Dolo (Batch B) can be returned.",
            PurchaseValidator.ValidateReturn(ok, Today, new Dictionary<long, int> { [5] = 10 }));
    }
}
