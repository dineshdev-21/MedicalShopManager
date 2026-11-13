using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Reports;

/// <summary>Every report against the seeded October-2026 scenario (see <see cref="ReportsTestDatabase"/>).</summary>
public class ReportsProvidersTests : IClassFixture<ReportsTestDatabase>
{
    private readonly ReportsTestDatabase _db;

    public ReportsProvidersTests(ReportsTestDatabase db) => _db = db;

    [Fact]
    public void EveryCatalogueReport_HasExactlyOneProvider()
    {
        var provided = _db.Providers().SelectMany(p => p.ReportIds).ToList();
        provided.Should().OnlyHaveUniqueItems();
        provided.Should().BeEquivalentTo(ReportCatalog.All.Select(d => d.Id));
    }

    [Fact]
    public async Task EveryReport_RunsWithItsDefaultFilters()
    {
        foreach (var definition in ReportCatalog.All)
        {
            var result = await _db.Runner().RunAsync(definition.Id, ReportParameters.DefaultsFor(definition, new DateTime(2026, 10, 31)));
            result.Definition.Id.Should().Be(definition.Id);
            result.Sheets.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task SalesRegister_ListsActiveBills_WithGstFromTheLines()
    {
        var result = await _db.RunAsync(ReportIds.SalesRegister);
        var sheet = result.Sheets.Single();
        sheet.Rows.Should().HaveCount(4, "the cancelled bill is excluded by default");
        sheet.Total("Bill amount").Should().Be(11200 + 22400 + 11_200_000 + 11200);
        sheet.Total("IGST").Should().Be(1_200_000);
        sheet.Total("Due").Should().Be(11_200_000);
        sheet.Value(0, "GSTIN").Should().Be("29AAACA1111A1Z1");
        sheet.Value(1, "Customer").Should().Be("Walk-in");

        var cancelled = await _db.RunAsync(ReportIds.SalesRegister, choices: (ReportChoiceKeys.Status, ReportChoiceKeys.Cancelled));
        cancelled.Sheets.Single().Rows.Should().ContainSingle().Which.Values[1].Should().Be("INV-5");
        (await _db.RunAsync(ReportIds.SalesRegister, choices: (ReportChoiceKeys.Status, ReportChoiceKeys.All))).Sheets.Single().Rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task SaleReturns_ShowCreditNoteRefundAndOpenCredit()
    {
        var sheet = (await _db.RunAsync(ReportIds.SaleReturns)).Sheets.Single();
        sheet.Rows.Should().ContainSingle();
        sheet.Value(0, "Credit note").Should().Be("CN-1");
        sheet.Value(0, "Against bill").Should().Be("INV-1");
        sheet.Total("Credit note value").Should().Be(1120);
        sheet.Total("Open credit").Should().Be(0);
    }

    [Fact]
    public async Task PurchaseRegister_ConfirmedOnly_AndBalanceFilter()
    {
        var sheet = (await _db.RunAsync(ReportIds.PurchaseRegister)).Sheets.Single();
        sheet.Rows.Should().HaveCount(2, "drafts are not purchases yet");
        sheet.Total("Bill amount").Should().Be(98400);
        sheet.Total("IGST").Should().Be(2400);
        var unpaid = (await _db.RunAsync(ReportIds.PurchaseRegister, choices: (ReportChoiceKeys.Status, ReportChoiceKeys.Unpaid))).Sheets.Single();
        unpaid.Rows.Should().ContainSingle().Which.Values[1].Should().Be("PUR-1");
    }

    [Fact]
    public async Task PurchaseReturns_ShowDebitNotePending()
    {
        var sheet = (await _db.RunAsync(ReportIds.PurchaseReturns)).Sheets.Single();
        sheet.Rows.Should().ContainSingle();
        sheet.Value(0, "Reason").Should().Be("Damaged / Breakage");
        sheet.Value(0, "Against bill").Should().Be("PD-101");
        sheet.Total("Pending").Should().Be(5600);
    }

    [Fact]
    public async Task Gstr1_ClassifiesTheScenario()
    {
        var result = await _db.RunAsync(ReportIds.Gstr1);
        var b2b = result.Sheet("b2b");
        b2b.Rows.Should().ContainSingle();
        b2b.Value(0, "Invoice Number").Should().Be("INV-1");
        b2b.Value(0, "Place Of Supply").Should().Be("29-Karnataka");
        b2b.Value(0, "Taxable Value").Should().Be(10000L);

        var b2cl = result.Sheet("b2cl");
        b2cl.Rows.Should().ContainSingle();
        b2cl.Value(0, "Invoice Number").Should().Be("INV-3");
        b2cl.Value(0, "Invoice Value").Should().Be(11_200_000L);

        var b2cs = result.Sheet("b2cs");
        b2cs.Rows.Should().ContainSingle();
        b2cs.Value(0, "Taxable Value").Should().Be(30000L);

        result.Sheet("cdnr").Rows.Should().ContainSingle().Which.Values[2].Should().Be("CN-1");
        result.Sheet("cdnur").Rows.Should().BeEmpty();

        var hsnB2b = result.Sheet("hsn(b2b)");
        hsnB2b.Rows.Should().ContainSingle();
        hsnB2b.Value(0, "Total Quantity").Should().Be(27L, "30 sold − 3 returned");
        hsnB2b.Value(0, "Taxable Value").Should().Be(9000L);
        result.Sheet("hsn(b2c)").Rows.Select(r => r.Values[0]).Should().Equal("30042019", "30049011", "30049099");

        var docs = result.Sheet("docs");
        docs.Value(0, "Total Number").Should().Be(5L);
        docs.Value(0, "Cancelled").Should().Be(1L);
        docs.Value(0, "Sr. No. From").Should().Be("INV-1");
        docs.Value(0, "Sr. No. To").Should().Be("INV-5");
        docs.Value(1, "Nature of Document").Should().Be("Credit Note");
        result.Notes.Should().Contain(n => n.Contains("INV-3"), "the inter-state walk-in buyer's state is unknown");
    }

    [Fact]
    public async Task Gstr3b_NetsReturnsAndItc()
    {
        var result = await _db.RunAsync(ReportIds.Gstr3b);
        var summary = result.Sheet(Gstr3bBuilder.Summary);
        summary.Value(0, "Total taxable value").Should().Be(10_039_000L);
        summary.Value(0, "Integrated tax").Should().Be(1_200_000L);
        summary.Value(0, "Central tax").Should().Be(2340L);
        summary.Value(5, "Central tax").Should().Be(3000L);
        summary.Value(5, "Integrated tax").Should().Be(2400L);
        summary.Value(6, "Central tax").Should().Be(300L);
        summary.Value(8, "Integrated tax").Should().Be(1_197_600L);
        summary.Value(8, "Central tax").Should().Be(-360L);
        result.Sheet(Gstr3bBuilder.InterState).Rows.Should().ContainSingle().Which.Values[1].Should().Be(10_000_000L);
        result.Sheet(Gstr3bBuilder.ExemptInward).Value(0, "Intra-State supplies").Should().Be(20000L);
    }

    [Fact]
    public async Task HsnSummary_HasSalesNetOfReturnsAndPurchases()
    {
        var result = await _db.RunAsync(ReportIds.HsnSummary);
        result.Sheets[0].Total("Taxable Value").Should().Be(10_039_000L);
        var purchases = result.Sheets[1];
        purchases.Total("Taxable Value").Should().Be(50000 + 20000 + 20000 - 5000);
        purchases.Rows.Select(r => r.Values[0]).Should().Contain("30051090");
    }

    [Fact]
    public async Task ProfitReports_DeductReturnedSalesAndCost()
    {
        var items = (await _db.RunAsync(ReportIds.ItemProfit)).Sheets.Single();
        var dolo = items.Rows.Single(r => (string?)r.Values[0] == "Dolo 650");
        dolo.Values[items.IndexOf("Units sold")].Should().Be(630L);
        dolo.Values[items.IndexOf("Units returned")].Should().Be(3L);
        dolo.Values[items.IndexOf("Net sales")].Should().Be(10_009_000L);
        dolo.Values[items.IndexOf("Cost")].Should().Be(6_005_400L, "cost of 3 of 30 units = round(6000 × 3 / 30) = 600 back");
        dolo.Values[items.IndexOf("Profit")].Should().Be(4_003_600L);
        items.Total("Profit").Should().Be(4_003_600 + 12000 + 0);

        var bills = (await _db.RunAsync(ReportIds.BillProfit)).Sheets.Single();
        bills.Value(0, "Bill no.").Should().Be("INV-1");
        bills.Value(0, "Net sales").Should().Be(9000L);
        bills.Value(0, "Profit").Should().Be(3600L);
        bills.Value(0, "Margin %").Should().Be(40m);

        var pnl = await _db.RunAsync(ReportIds.ProfitLoss);
        var statement = pnl.Sheet("Statement");
        statement.Value(2, "Amount").Should().Be(10_039_000L);
        statement.Value(5, "Amount").Should().Be(6_023_400L);
        statement.Value(6, "Amount").Should().Be(4_015_600L);
        statement.Value(7, "Amount").Should().Be(-1_100_000L, "rent ₹10,000 + electricity ₹1,000 excluding GST");
        statement.Value(8, "Amount").Should().Be(2_915_600L);
    }

    [Fact]
    public async Task StockValuation_ValuesEachBatchAtItsPackRates_AndWorksBackFromTheLedger()
    {
        var today = await _db.RunAsync(ReportIds.StockValuation, asOf: new DateTime(2026, 11, 5));
        var sheet = today.Sheets.Single();
        sheet.Rows.Should().HaveCount(4);
        sheet.Total("Value @ purchase rate").Should().Be(90000 + 16000 + 20000 + 20000);
        sheet.Total("Value @ MRP").Should().Be(150000 + 24000 + 36000 + 40000);
        sheet.Value(0, "Item").Should().Be("Azithral 500");
        sheet.Value(0, "Stock (packs + loose)").Should().Be("2", "10 units of 5's");

        var october = (await _db.RunAsync(ReportIds.StockValuation, asOf: new DateTime(2026, 10, 31))).Sheets.Single();
        october.Rows.Single(r => (string?)r.Values[0] == "Dolo 650").Values[october.IndexOf("Units")].Should().Be(180L,
            "30 units sold on 4 Nov are added back");

        var withoutExpired = (await _db.RunAsync(ReportIds.StockValuation, asOf: new DateTime(2026, 11, 5),
            choices: (ReportChoiceKeys.ExpiryState, ReportChoiceKeys.ExcludeExpired))).Sheets.Single();
        withoutExpired.Rows.Should().HaveCount(3);
        var byItem = (await _db.RunAsync(ReportIds.StockValuation, asOf: new DateTime(2026, 11, 5),
            choices: (ReportChoiceKeys.GroupBy, ReportChoiceKeys.Item))).Sheets.Single();
        byItem.Name.Should().Be("Item-wise stock");
        byItem.Total("Value @ MRP").Should().Be(250000);
    }

    [Fact]
    public async Task ExpiryBySupplier_GroupsExpiredAndNearExpiryBatches()
    {
        var result = await _db.RunAsync(ReportIds.ExpiryBySupplier, asOf: new DateTime(2026, 11, 5), number: 90);
        var bySupplier = result.Sheet("By supplier");
        bySupplier.Rows.Should().HaveCount(2);
        bySupplier.Total("Expired (cost)").Should().Be(20000);
        bySupplier.Total("Near expiry (cost)").Should().Be(16000);
        result.Sheet("Batches").Rows.Select(r => r.Values[0]).Should().Equal("MH Traders", "Pharma Distributors");

        var expiredOnly = await _db.RunAsync(ReportIds.ExpiryBySupplier, asOf: new DateTime(2026, 11, 5), number: 90,
            choices: (ReportChoiceKeys.ExpiryState, ReportChoiceKeys.Expired));
        expiredOnly.Sheet("Batches").Rows.Should().ContainSingle().Which.Values[1].Should().Be("Codeine syrup");
    }

    [Fact]
    public async Task StockMovement_ClassifiesFastSlowAndNonMoving()
    {
        var sheet = (await _db.RunAsync(ReportIds.StockMovement)).Sheets.Single();
        string ClassOf(string item) => (string)sheet.Rows.Single(r => (string?)r.Values[0] == item).Values[2]!;
        ClassOf("Dolo 650").Should().Be("Fast");
        ClassOf("Azithral 500").Should().Be("Slow");
        ClassOf("Codeine syrup").Should().Be("Slow");
        ClassOf("Plain bandage").Should().Be("Non-moving");

        var nonMoving = (await _db.RunAsync(ReportIds.StockMovement, choices: (ReportChoiceKeys.Movement, ReportChoiceKeys.NonMoving))).Sheets.Single();
        nonMoving.Rows.Should().ContainSingle();
        nonMoving.Value(0, "Stock value (cost)").Should().Be(20000L);
    }

    [Fact]
    public async Task H1Register_HasPatientPrescriberAndQuantity()
    {
        var result = await _db.RunAsync(ReportIds.H1Register);
        var sheet = result.Sheets.Single();
        sheet.Rows.Should().ContainSingle();
        sheet.Value(0, "Sr. no.").Should().Be(1L);
        sheet.Value(0, "Patient name").Should().Be("Sita");
        sheet.Value(0, "Patient address").Should().Be("Basavanagudi");
        sheet.Value(0, "Prescriber").Should().Be("Dr. Rao");
        sheet.Value(0, "Prescriber reg. no.").Should().Be("KMC123");
        sheet.Value(0, "Prescriber address").Should().Be("Jayanagar");
        sheet.Value(0, "Drug name").Should().Be("Azithral 500");
        sheet.Value(0, "Qty supplied (units)").Should().Be(5L);
        sheet.Value(0, "Qty (packs + loose)").Should().Be("1");
        result.Notes.Should().NotContain(n => n.Contains("no patient"));
    }

    [Fact]
    public async Task NarcoticRegister_RunsTheStockBalanceFromTheLedger()
    {
        var sheet = (await _db.RunAsync(ReportIds.NarcoticRegister)).Sheets.Single();
        sheet.Rows.Should().HaveCount(4);
        sheet.Value(0, "Particulars").Should().Be("Opening balance");
        sheet.Value(1, "Particulars").Should().Be("Received (purchase)");
        sheet.Value(1, "Voucher no.").Should().Be("MH-55");
        sheet.Value(1, "Patient / supplier").Should().Be("MH Traders");
        sheet.Value(1, "Balance").Should().Be(6L);
        sheet.Value(2, "Voucher no.").Should().Be("INV-4");
        sheet.Value(2, "Patient / supplier").Should().Be("Ram");
        sheet.Value(2, "Prescriber").Should().Be("Dr. Iyer");
        sheet.Value(2, "Balance").Should().Be(4L);
        sheet.Value(3, "Balance").Should().Be(4L);

        var nrxOnly = (await _db.RunAsync(ReportIds.NarcoticRegister, choices: (ReportChoiceKeys.Schedule, ReportChoiceKeys.Narcotic))).Sheets.Single();
        nrxOnly.Rows.Should().BeEmpty();
        var november = (await _db.RunAsync(ReportIds.NarcoticRegister, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30))).Sheets.Single();
        november.Value(0, "Balance").Should().Be(4L, "the opening balance carries October's movements");
    }

    [Fact]
    public async Task Outstanding_DerivesDuesFromSourceRows()
    {
        var customers = (await _db.RunAsync(ReportIds.CustomerOutstanding)).Sheets.Single();
        customers.Rows.Should().ContainSingle("the clinic's credit note was refunded, so it owes nothing");
        customers.Value(0, "Customer").Should().Be("Ravi Kumar");
        customers.Value(0, "Net due").Should().Be(11_250_000L);
        customers.Value(0, "Open bills").Should().Be(2L, "opening balance + INV-3");

        var suppliers = (await _db.RunAsync(ReportIds.SupplierOutstanding)).Sheets.Single();
        suppliers.Rows.Should().ContainSingle();
        suppliers.Value(0, "Total due").Should().Be(50000L);
        suppliers.Value(0, "Debit notes / advance").Should().Be(5600L);
        suppliers.Value(0, "Net due").Should().Be(44400L);
    }

    [Fact]
    public async Task CashBook_HasOpeningRunningAndClosingBalance()
    {
        var result = await _db.RunAsync(ReportIds.CashBook);
        var sheet = result.Sheets.Single();
        sheet.Rows.First().Style.Should().Be(ReportRowStyle.Opening);
        sheet.Value(0, "Balance").Should().Be(5000L, "September's cash receipt");
        var closing = sheet.Rows.Last();
        closing.Style.Should().Be(ReportRowStyle.Subtotal);
        closing.Values[sheet.IndexOf("Cash in")].Should().Be(22400L);
        closing.Values[sheet.IndexOf("Cash out")].Should().Be(26000L + 1120 + 1_000_000);
        closing.Values[sheet.IndexOf("Balance")].Should().Be(5000L + 22400 - 1_027_120);
        sheet.Rows.Should().NotContain(r => r.Values.Contains("BESCOM"), "UPI expenses are not cash");
    }

    [Fact]
    public async Task DayBook_ListsDocumentsAndMoneyMovements()
    {
        var result = await _db.RunAsync(ReportIds.DayBook);
        var sheet = result.Sheets.Single();
        sheet.Total("Money in").Should().Be(44800);
        sheet.Total("Money out").Should().Be(26000 + 22400 + 1120 + 1_000_000 + 118000);
        sheet.Rows.Count(r => (string?)r.Values[1] == "Sale").Should().Be(4);
        sheet.Rows.Count(r => (string?)r.Values[1] == "Purchase").Should().Be(2);
        sheet.Rows.Should().Contain(r => (string?)r.Values[1] == "Purchase return");
        result.Summary.Single(s => s.Label == "Sales").Value.Should().Be(11_244_800L);
    }

    [Fact]
    public async Task DoctorSales_AndExpenseSummary()
    {
        var doctors = (await _db.RunAsync(ReportIds.DoctorSales)).Sheets.Single();
        doctors.Rows.Should().HaveCount(2);
        var rao = doctors.Rows.Single(r => (string?)r.Values[0] == "Dr. Rao");
        rao.Values[doctors.IndexOf("Reg. no.")].Should().Be("KMC123");
        rao.Values[doctors.IndexOf("Specialization")].Should().Be("Physician");
        rao.Values[doctors.IndexOf("Profit")].Should().Be(12000L);

        var expenses = await _db.RunAsync(ReportIds.ExpenseSummary);
        var byCategory = expenses.Sheet("By category");
        byCategory.Rows.Select(r => r.Values[0]).Should().Equal("Rent", "Electricity");
        byCategory.Total("Amount").Should().Be(1_118_000);
        byCategory.Total("GST").Should().Be(18000);
        expenses.Sheet("Expense vouchers").Rows.Should().HaveCount(2, "the cancelled expense is excluded");
    }
}
