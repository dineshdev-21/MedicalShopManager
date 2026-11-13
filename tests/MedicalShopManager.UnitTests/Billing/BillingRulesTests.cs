using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.UnitTests.Billing;

/// <summary>Payment wizard steps, invoice text, customer rules and settings mapping of the Billing module.</summary>
public class BillingRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    // ───────────── Payment wizard (spec 02 §M) ─────────────

    [Fact]
    public void Step1_PaidInFull_IsValid_WithoutDueDate() =>
        BillingPaymentRules.ValidateStep1(10000, false, true, 10000, null, Today).Should().BeEmpty();

    [Fact]
    public void Step1_PartialPayment_NeedsDueDateNotBeforeInvoice()
    {
        BillingPaymentRules.ValidateStep1(10000, false, true, 4000, null, Today).Should().Equal("Please provide a valid payment due date");
        BillingPaymentRules.ValidateStep1(10000, false, true, 4000, Today.AddDays(-1), Today).Should().Equal("Please provide a valid payment due date");
        BillingPaymentRules.ValidateStep1(10000, false, true, 4000, Today, Today).Should().BeEmpty();
    }

    [Fact]
    public void Step1_Messages_ForZeroAndOverPayment_AndCounterSaleCredit()
    {
        BillingPaymentRules.ValidateStep1(10000, false, true, 0, Today, Today).Should().Contain("Please Fill Payment Amount");
        BillingPaymentRules.ValidateStep1(10000, false, true, 10001, Today, Today).Should().Contain("Payment Amount > Payable Amount");
        BillingPaymentRules.ValidateStep1(10000, true, false, 0, Today, Today).Should().Contain(e => e.StartsWith("Cash/Counter sale must be paid in full"));
        BillingPaymentRules.ValidateStep1(10000, false, false, 0, Today.AddDays(30), Today).Should().BeEmpty();
    }

    [Fact]
    public void Step2_TendersMustAddUpExactly()
    {
        var split = new[] { new SaleTender { Mode = TenderMode.Cash, AmountPaisa = 6000 }, new SaleTender { Mode = TenderMode.Upi, AmountPaisa = 4000 } };
        BillingPaymentRules.ValidateStep2(10000, split).Should().BeEmpty();
        BillingPaymentRules.Remaining(10000, split.Take(1)).Should().Be(4000);
        BillingPaymentRules.ValidateStep2(10000, split.Take(1)).Should().ContainSingle(e => e.StartsWith("Allocate ₹40.00 more"));
        BillingPaymentRules.ValidateStep2(5000, split).Should().ContainSingle(e => e.StartsWith("Payment modes exceed"));
    }

    [Fact]
    public void Step3_IsSkippedForCardOnly_AndChangeIsNeverNegative()
    {
        BillingPaymentRules.NeedsStep3(new[] { new SaleTender { Mode = TenderMode.Card, AmountPaisa = 100 } }).Should().BeFalse();
        BillingPaymentRules.NeedsStep3(new[] { new SaleTender { Mode = TenderMode.Upi, AmountPaisa = 100 } }).Should().BeTrue();
        BillingPaymentRules.NeedsStep3(new[] { new SaleTender { Mode = TenderMode.Cash, AmountPaisa = 100 } }).Should().BeTrue();
        BillingPaymentRules.Change(50000, 45100).Should().Be(4900);
        BillingPaymentRules.Change(40000, 45100).Should().Be(0);
        BillingPaymentRules.Label(TenderMode.Card).Should().Be("Card / POS");
    }

    // ───────────── Invoice text ─────────────

    [Theory]
    [InlineData(0L, "Rupees Zero Only")]
    [InlineData(25100L, "Rupees Two Hundred Fifty One Only")]
    [InlineData(125050L, "Rupees One Thousand Two Hundred Fifty and Fifty Paise Only")]
    [InlineData(1234567800L, "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight Only")]
    public void AmountInWords_UsesIndianSystem(long paisa, string expected) => BillingInvoiceText.AmountInWords(paisa).Should().Be(expected);

    [Fact]
    public void GstByRate_GroupsAndSums()
    {
        var lines = new[]
        {
            new BillingInvoiceLine { GstPercent = 12m, TaxablePaisa = 1000, CgstPaisa = 60, SgstPaisa = 60 },
            new BillingInvoiceLine { GstPercent = 5m, TaxablePaisa = 2000, CgstPaisa = 50, SgstPaisa = 50 },
            new BillingInvoiceLine { GstPercent = 12m, TaxablePaisa = 500, CgstPaisa = 30, SgstPaisa = 30 }
        };
        var summary = BillingInvoiceText.GstByRate(lines);
        summary.Select(s => s.GstPercent).Should().Equal(5m, 12m);
        summary[1].TaxablePaisa.Should().Be(1500);
        summary[1].TaxPaisa.Should().Be(180);
    }

    [Fact]
    public void InvoiceTitle_TaxInvoiceOrBillOfSupply()
    {
        new BillingInvoice { IsTaxInvoice = true }.DocumentTitle.Should().Be("TAX INVOICE");
        new BillingInvoice { IsTaxInvoice = false }.DocumentTitle.Should().Be("BILL OF SUPPLY");
    }

    [Fact]
    public void InvoiceLine_QuantityDisplay_ShowsPacksLooseAndFree()
    {
        var line = new BillingInvoiceLine { UnitsPerPack = 15, Quantity = 23, FreeUnits = 3, RatePerPackPaisa = 18840, DiscountPercent = 10m };
        line.QuantityDisplay.Should().Be(BillingFormat.Quantity(20, 15) + " +3 free");
        line.NetRatePerPackPaisa.Should().Be(16956);
    }

    // ───────────── Customers (spec 02 §D) ─────────────

    [Fact]
    public void Customer_RequiresName_TenDigitMobile_ValidGstin()
    {
        var errors = BillingCustomerRules.Validate(new BillingCustomer { Kind = CustomerKind.Business, Name = " ", Phone = "12345", Gstin = "27ABCDE1234F1Z" });
        errors.Should().Contain(new[] { "Please fill Customer Name", "Mobile number must be 10 digits", "Customer GSTIN must be 15 characters long" });
        BillingCustomerRules.Validate(new BillingCustomer { Name = "Asha", Phone = "9876543210" }).Should().BeEmpty();
    }

    [Fact]
    public void Customer_OpeningBalanceNeedsDate_DiscountInRange()
    {
        BillingCustomerRules.Validate(new BillingCustomer { Name = "A", OpeningBalancePaisa = 500 }).Should().Contain("Please fill Opening Balance Date");
        BillingCustomerRules.Validate(new BillingCustomer { Name = "A", DefaultDiscountPercent = 120m }).Should().Contain("Sales Discount must be between 0% and 100%");
    }

    [Fact]
    public void Normalize_DerivesPan_AndDropsFieldsOfOtherKind()
    {
        var business = BillingCustomerRules.Normalize(new BillingCustomer
        {
            Kind = CustomerKind.Business, Name = " Rao Clinic ", Gstin = "27abcde1234f1z5", Gender = PersonGender.Male, DateOfBirth = Today.AddYears(-30)
        });
        business.Name.Should().Be("Rao Clinic");
        business.Gstin.Should().Be("27ABCDE1234F1Z5");
        business.Pan.Should().Be("ABCDE1234F");
        business.Gender.Should().Be(PersonGender.Unspecified);
        business.DateOfBirth.Should().BeNull();

        var person = BillingCustomerRules.Normalize(new BillingCustomer { Kind = CustomerKind.Individual, Name = "Asha", Gstin = "27ABCDE1234F1Z5" });
        person.Gstin.Should().BeNull();
        person.Pan.Should().BeNull();
    }

    [Fact]
    public void Gstin_FormatCheck_AndAgeHelpers()
    {
        BillingCustomerRules.IsGstinFormat("27ABCDE1234F1Z5").Should().BeTrue();
        BillingCustomerRules.IsGstinFormat("27ABCDE1234F1X5").Should().BeFalse();
        BillingCustomerRules.DateOfBirthFromAge(30, 6, Today).Should().Be(new DateTime(1996, 5, 5));
        BillingCustomerRules.AgeInYears(new DateTime(1996, 11, 6), Today).Should().Be(29);
    }

    [Fact]
    public void Customer_BalanceLabel()
    {
        new BillingCustomer { OutstandingBalancePaisa = 12345 }.BalanceLabel.Should().Be($"To Receive ₹{123.45m:N2}");
        new BillingCustomer { OutstandingBalancePaisa = -500 }.BalanceLabel.Should().Be($"To Pay ₹{5m:N2}");
        new BillingCustomer().BalanceLabel.Should().Be("-");
    }

    // ───────────── Settings from Control Center ─────────────

    [Fact]
    public void Settings_ComeFromControlCenterSaleSettingsAndProfile()
    {
        var settings = BillingSettings.From(
            new SaleSettings
            {
                NewSaleIsCredit = true, DefaultDueDays = 15, ExpiryCutoffMonths = 2, NearExpiryMonths = 3, RoundOffMode = RoundOffMode.None,
                AllowNegativeStock = true, DefaultDiscountPercent = 2m, MinDiscountPercent = 1m, MaxDiscountPercent = 20m,
                RequireDoctorForScheduleH = true, RequirePrescriptionReference = true, BarcodeQtyInPacks = false, DefaultDoctorName = " Dr Rao "
            },
            new BusinessProfile { GstRegistrationType = GstRegistrationType.Regular, Gstin = "27ABCDE1234F1Z5" },
            draftRetentionDays: 7, BatchPickOrder.Lifo);

        settings.NewSaleIsCredit.Should().BeTrue();
        settings.DefaultDueDays.Should().Be(15);
        settings.ExpiryCutoffMonths.Should().Be(2);
        settings.NearExpiryMonths.Should().Be(3);
        settings.RoundOff.Should().Be(RoundOffMode.None);
        settings.AllowNegativeStock.Should().BeTrue();
        settings.MaxDiscountPercent.Should().Be(20m);
        settings.BarcodeQtyInPacks.Should().BeFalse();
        settings.DefaultDoctorName.Should().Be("Dr Rao");
        settings.BatchOrder.Should().Be(BatchPickOrder.Lifo);
        settings.ShopStateCode.Should().Be("27");
        settings.ShopChargesGst.Should().BeTrue();
    }

    [Theory]
    [InlineData(GstRegistrationType.Composition, false)]
    [InlineData(GstRegistrationType.Unregistered, false)]
    [InlineData(GstRegistrationType.Regular, true)]
    public void Settings_BillOfSupplyForNonRegularShops(GstRegistrationType type, bool chargesGst) =>
        BillingSettings.From(new SaleSettings(), new BusinessProfile { GstRegistrationType = type, StateCode = "29" }, 7, BatchPickOrder.Fefo)
            .ShopChargesGst.Should().Be(chargesGst);
}
