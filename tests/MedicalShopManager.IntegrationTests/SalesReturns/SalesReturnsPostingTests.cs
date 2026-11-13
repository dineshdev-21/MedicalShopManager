using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Modules.Billing;
using MedicalShopManager.Infrastructure.Modules.SalesReturns;
using Xunit;

namespace MedicalShopManager.IntegrationTests.SalesReturns;

/// <summary>Sale returns posted, refunded and cancelled through SaleReturnService / SaleReturnRepository on a real database.</summary>
public class SalesReturnsPostingTests : SalesReturnsDbTestBase
{
    private static SaleReturnRequest Against(long saleId, long saleItemId, long batchId, long units,
        SaleRefundMode mode = SaleRefundMode.Cash, SaleReturnDisposition disposition = SaleReturnDisposition.Restock) => new()
    {
        SaleId = saleId,
        ReturnDate = Today,
        RefundMode = mode,
        RefundMethod = PaymentMethod.Cash,
        Reason = "Excess quantity",
        Lines = new[] { new SaleReturnLineRequest { SaleItemId = saleItemId, MedicineId = 0, BatchId = batchId, Units = units, Disposition = disposition } }
    };

    [Fact]
    public async Task Migration_CreatesTables_ReturnsColumns_AndSetting()
    {
        foreach (var table in new[] { "SaleReturns", "SaleReturnItems", "SaleReturnRefunds" })
            (await LongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @N", ("@N", table))).Should().Be(1, table);
        (await LongAsync("SELECT COUNT(*) FROM pragma_table_info('Returns') WHERE name IN ('SaleReturnId', 'SaleReturnItemId')")).Should().Be(2);
        (await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'SalesReturns.RoundOffToRupee'")).Should().Be("1");
        (await Db.GetAppliedVersionsAsync()).Should().Contain(11);
    }

    [Fact]
    public async Task CashReturn_AgainstBill_RestocksBatch_AllocatesNumbers_WritesLedgerRefundAndAudit()
    {
        var (medicineId, batchId) = await SeedItemAsync(units: 100);
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);
        (await StockAsync(batchId)).Should().Be(80);
        var (expectedReturn, expectedCredit) = await Service().PeekNumbersAsync(Today);

        var posted = await Service().PostAsync(Against(saleId, itemId, batchId, 5));

        posted.ReturnNumber.Should().Be(expectedReturn);
        posted.CreditNoteNumber.Should().Be(expectedCredit);
        posted.ReturnNumber.Should().StartWith("SR/");
        posted.CreditNoteNumber.Should().StartWith("CN/");
        posted.NetAmountPaisa.Should().Be(6300); // 6280 rounded to the rupee
        posted.RefundedPaisa.Should().Be(6300);
        posted.CreditBalancePaisa.Should().Be(0);
        posted.Lines.Should().ContainSingle().Which.TaxablePaisa.Should().Be(5607);
        posted.Refunds.Should().ContainSingle().Which.Method.Should().Be(PaymentMethod.Cash);

        (await StockAsync(batchId)).Should().Be(85);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE BatchId = @B AND MovementType = @T AND QuantityIn = 5 AND BalanceAfter = 85",
            ("@B", batchId), ("@T", (int)MovementType.SaleReturn))).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Returns WHERE ReturnType = @T AND OriginalTransactionId = @S AND SaleReturnId = @R AND ReturnedToStock = 1",
            ("@T", (int)ReturnType.SalesReturn), ("@S", saleId), ("@R", posted.Id))).Should().Be(1);
        (await LongAsync("SELECT SUM(AmountPaisa) FROM Payments WHERE PaymentType = @T AND TransactionId = @R",
            ("@T", (int)PaymentType.CustomerRefund), ("@R", posted.Id))).Should().Be(6300);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'CreateSaleReturn' AND EntityId = @R", ("@R", posted.Id))).Should().Be(1);
        (await new BillingSaleRepository(Db).HasReturnsAsync(saleId)).Should().BeTrue("Billing must block editing a bill with returns");
    }

    [Fact]
    public async Task ReturnableQuantity_IsSoldMinusReturned_AndOverReturnsAreRefused()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);
        var service = Service();

        await service.PostAsync(Against(saleId, itemId, batchId, 15));
        var sale = (await service.GetReturnableSaleAsync(saleId))!;
        var line = sale.Lines.Single();
        line.ReturnedUnits.Should().Be(15);
        line.ReturnableUnits.Should().Be(5);
        sale.PreviouslyReturnedPaisa.Should().Be(18800); // 18840 rounded
        sale.PreviouslyRefundedPaisa.Should().Be(18800);

        var tooMany = () => service.PostAsync(Against(saleId, itemId, batchId, 6));
        (await tooMany.Should().ThrowAsync<SaleReturnValidationException>()).Which.Errors.Single().Should().Contain("only 0 + 5");

        // The last 5 units: what is left of the bill (25100 − 18800 = 6300), so the total credited equals the bill.
        var last = await service.PostAsync(Against(saleId, itemId, batchId, 5));
        last.NetAmountPaisa.Should().Be(6300);
        (await LongAsync("SELECT SUM(TaxablePaisa) FROM SaleReturnItems")).Should().Be(22429);
        (await StockAsync(batchId)).Should().Be(100);
    }

    [Fact]
    public async Task StaleDocument_IsRefusedInsideTheTransaction_AndRollsBackEverything()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);
        var service = Service();
        var sale = (await service.GetReturnableSaleAsync(saleId))!;
        var (doc, errors) = service.Preview(Against(saleId, itemId, batchId, 20), sale, new Dictionary<long, SaleReturnBatchInfo>(),
            RoundOffMode.NearestRupee);
        errors.Should().BeEmpty();

        await Repository().InsertAsync(doc);   // another till returned everything first
        var stockAfterFirst = await StockAsync(batchId);
        var numbers = Numbers();
        var nextReturn = await numbers.PeekNextAsync(DocumentType.SaleReturn, Today);
        var nextCredit = await numbers.PeekNextAsync(DocumentType.CreditNote, Today);

        var again = () => Repository().InsertAsync(doc);

        (await again.Should().ThrowAsync<SaleReturnValidationException>()).Which.Message.Should().Contain("another return was saved");
        (await StockAsync(batchId)).Should().Be(stockAfterFirst);
        (await LongAsync("SELECT COUNT(*) FROM SaleReturns")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM SaleReturnItems")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Returns")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(1);
        (await numbers.PeekNextAsync(DocumentType.SaleReturn, Today)).Should().Be(nextReturn);
        (await numbers.PeekNextAsync(DocumentType.CreditNote, Today)).Should().Be(nextCredit);
    }

    [Fact]
    public async Task MissingBatchOnAWalkInReturn_RollsBackTheNumbersAndRows()
    {
        var numbers = Numbers();
        var before = await numbers.PeekNextAsync(DocumentType.CreditNote, Today);
        var doc = new SaleReturnDocument
        {
            ReturnDate = Today, RefundMode = SaleRefundMode.Cash, NetAmountPaisa = 100,
            Lines = { new SaleReturnDocumentLine { MedicineId = 1, MedicineName = "Ghost", BatchId = 999, BatchNumber = "X", Units = 1, LineTotalPaisa = 100 } }
        };

        await FluentActions.Invoking(() => Repository().InsertAsync(doc)).Should().ThrowAsync<SaleReturnValidationException>();

        (await numbers.PeekNextAsync(DocumentType.CreditNote, Today)).Should().Be(before);
        (await LongAsync("SELECT COUNT(*) FROM SaleReturns")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(0);
    }

    [Fact]
    public async Task ExpiredBatch_TakesTheUnitsBack_AsExpiredStock()
    {
        var expiry = Today.AddDays(-10);
        var (medicineId, batchId) = await SeedItemAsync(units: 40, expiry: expiry);
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId, expiry: expiry);

        var posted = await Service().PostAsync(Against(saleId, itemId, batchId, 15));

        posted.Lines.Single().Disposition.Should().Be(SaleReturnDisposition.ExpiredStock);
        (await StockAsync(batchId)).Should().Be(35);
        ((string?)await ScalarAsync("SELECT Reason FROM StockMovements WHERE BatchId = @B", ("@B", batchId))).Should().Contain("expired stock");
    }

    [Fact]
    public async Task DamagedUnits_AreCreditedButNotRestocked()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);

        var posted = await Service().PostAsync(Against(saleId, itemId, batchId, 5, disposition: SaleReturnDisposition.Damaged));

        posted.Lines.Single().Disposition.Should().Be(SaleReturnDisposition.Damaged);
        posted.NetAmountPaisa.Should().Be(6300);
        (await StockAsync(batchId)).Should().Be(80);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements")).Should().Be(0);
        (await LongAsync("SELECT ReturnedToStock FROM Returns WHERE SaleReturnId = @R", ("@R", posted.Id))).Should().Be(0);
    }

    [Fact]
    public async Task CreditToCustomer_LeavesOpenCredit_ThatCanBeRefundedInParts()
    {
        var customerId = await SeedCustomerAsync();
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId, customerId: customerId, paidPaisa: 0, method: PaymentMethod.Credit);
        var service = Service();

        var cash = () => service.PostAsync(Against(saleId, itemId, batchId, 5));
        (await cash.Should().ThrowAsync<SaleReturnValidationException>()).Which.Message.Should().Contain("Credit to customer");

        var note = await service.PostAsync(Against(saleId, itemId, batchId, 5, SaleRefundMode.CreditToCustomer));
        note.CustomerId.Should().Be(customerId);
        note.CreditBalancePaisa.Should().Be(6300);
        (await service.GetCustomerCreditAsync(customerId)).Should().Be(6300);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(0);

        var after = await service.RefundCreditAsync(new CreditRefundRequest
        {
            SaleReturnId = note.Id, AmountPaisa = 2000, Method = PaymentMethod.UPI, Reference = "UTR42", RefundDate = Today
        });
        after.CreditBalancePaisa.Should().Be(4300);
        after.Refunds.Should().ContainSingle().Which.Reference.Should().Be("UTR42");
        (await service.GetCustomerCreditAsync(customerId)).Should().Be(4300);
        (await LongAsync("SELECT COUNT(*) FROM Payments WHERE PaymentType = @T AND PaymentMethod = @M AND AmountPaisa = 2000",
            ("@T", (int)PaymentType.CustomerRefund), ("@M", (int)PaymentMethod.UPI))).Should().Be(1);

        var tooMuch = () => service.RefundCreditAsync(new CreditRefundRequest { SaleReturnId = note.Id, AmountPaisa = 4301, RefundDate = Today });
        await tooMuch.Should().ThrowAsync<SaleReturnValidationException>();
        // The repository re-checks the open balance inside its transaction too.
        await FluentActions.Invoking(() => Repository().AddRefundAsync(
                new CreditRefundRequest { SaleReturnId = note.Id, AmountPaisa = 4301, RefundDate = Today }, note.CreditNoteNumber))
            .Should().ThrowAsync<SaleReturnValidationException>();

        var open = await service.ListAsync(new SaleReturnFilter { OpenCreditOnly = true, From = Today.AddYears(-5), To = Today.AddYears(-5) });
        open.Rows.Should().ContainSingle().Which.CreditBalancePaisa.Should().Be(4300);
    }

    [Fact]
    public async Task Cancel_TakesRestockedUnitsOut_RemovesLedgerAndRefundRows_AndUnblocksBilling()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);
        var service = Service();
        var posted = await service.PostAsync(Against(saleId, itemId, batchId, 5));
        (await StockAsync(batchId)).Should().Be(85);

        await service.CancelAsync(posted.Id, "Entered against the wrong bill", null);

        var cancelled = (await service.GetAsync(posted.Id))!;
        cancelled.IsCancelled.Should().BeTrue();
        cancelled.CancelReason.Should().Be("Entered against the wrong bill");
        cancelled.CreditBalancePaisa.Should().Be(0);
        (await StockAsync(batchId)).Should().Be(80);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE BatchId = @B AND QuantityOut = 5 AND BalanceAfter = 80", ("@B", batchId))).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Returns")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(0);
        (await new BillingSaleRepository(Db).HasReturnsAsync(saleId)).Should().BeFalse();
        (await service.GetReturnableSaleAsync(saleId))!.Lines.Single().ReturnableUnits.Should().Be(20);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'CancelSaleReturn'")).Should().Be(1);

        await FluentActions.Invoking(() => service.CancelAsync(posted.Id, "again", null)).Should().ThrowAsync<SaleReturnValidationException>();
    }

    [Fact]
    public async Task Cancel_IsRefused_WhenTheRestockedUnitsWereSoldSince_AndChangesNothing()
    {
        var (medicineId, batchId) = await SeedItemAsync(units: 20);
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId);
        var service = Service();
        var posted = await service.PostAsync(Against(saleId, itemId, batchId, 10));
        await SetStockAsync(batchId, 4); // sold again meanwhile

        var act = () => service.CancelAsync(posted.Id, "Mistake", null);

        (await act.Should().ThrowAsync<SaleReturnValidationException>()).Which.Message.Should().Contain("sold or adjusted since");
        (await StockAsync(batchId)).Should().Be(4);
        (await service.GetAsync(posted.Id))!.IsCancelled.Should().BeFalse();
        (await LongAsync("SELECT COUNT(*) FROM Returns")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(1);
    }

    [Fact]
    public async Task WalkInReturn_RestocksTheChosenBatch_AndCreditNeedsACustomer()
    {
        var (medicineId, batchId) = await SeedItemAsync(units: 10);
        var customerId = await SeedCustomerAsync("Sita", "9000000001");
        var service = Service();
        var batches = await service.GetItemBatchesAsync(medicineId);
        batches.Should().ContainSingle().Which.DefaultRatePaisa.Should().Be(18840);
        (await service.SearchItemsAsync("dolo")).Should().ContainSingle();
        (await service.SearchCustomersAsync("900000")).Should().ContainSingle().Which.Id.Should().Be(customerId);

        var request = new SaleReturnRequest
        {
            ReturnDate = Today,
            RefundMode = SaleRefundMode.CreditToCustomer,
            Lines = new[] { new SaleReturnLineRequest { MedicineId = medicineId, BatchId = batchId, Units = 16, RatePerPackPaisa = 18840, DiscountPercent = 10m } }
        };
        await FluentActions.Invoking(() => service.PostAsync(request)).Should().ThrowAsync<SaleReturnValidationException>();

        var posted = await service.PostAsync(request with { CustomerId = customerId, CustomerName = "Sita" });

        posted.SaleId.Should().BeNull();
        posted.CustomerName.Should().Be("Sita");
        posted.Lines.Single().LineTotalPaisa.Should().Be(18086); // 16956 × 16 / 15
        (await StockAsync(batchId)).Should().Be(26);
        (await LongAsync("SELECT COUNT(*) FROM Returns WHERE OriginalTransactionId IS NULL AND SaleReturnId = @R", ("@R", posted.Id))).Should().Be(1);
    }

    [Fact]
    public async Task CancelledBill_CannotBeReturned()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var (saleId, itemId) = await SeedSaleAsync(medicineId, batchId, status: 2);

        var act = () => Service().PostAsync(Against(saleId, itemId, batchId, 1));

        (await act.Should().ThrowAsync<SaleReturnValidationException>()).Which.Message.Should().Contain("cancelled");
    }

    [Fact]
    public async Task ReturnsRegister_FiltersAndTotals()
    {
        var customerId = await SeedCustomerAsync();
        var (medicineId, batchId) = await SeedItemAsync();
        var (cashSale, cashItem) = await SeedSaleAsync(medicineId, batchId, "INV-A");
        var (creditSale, creditItem) = await SeedSaleAsync(medicineId, batchId, "INV-B", customerId: customerId, paidPaisa: 0, method: PaymentMethod.Credit);
        var service = Service();
        var first = await service.PostAsync(Against(cashSale, cashItem, batchId, 5));
        await service.PostAsync(Against(creditSale, creditItem, batchId, 15, SaleRefundMode.CreditToCustomer));
        await service.CancelAsync(first.Id, "Duplicate entry", null);

        var all = await service.ListAsync(new SaleReturnFilter { From = Today, To = Today });
        all.Rows.Should().HaveCount(2);
        all.Totals.Count.Should().Be(1);
        all.Totals.NetAmountPaisa.Should().Be(18800);
        all.Totals.OpenCreditPaisa.Should().Be(18800);

        (await service.ListAsync(new SaleReturnFilter { From = Today, To = Today, IncludeCancelled = false })).Rows.Should().ContainSingle();
        (await service.ListAsync(new SaleReturnFilter { From = Today, To = Today, RefundMode = SaleRefundMode.Cash })).Rows.Should().ContainSingle()
            .Which.IsCancelled.Should().BeTrue();
        (await service.ListAsync(new SaleReturnFilter { From = Today, To = Today, SearchText = "INV-B" })).Rows.Should().ContainSingle()
            .Which.SaleInvoiceNumber.Should().Be("INV-B");
        (await service.ListAsync(new SaleReturnFilter { From = Today.AddDays(-30), To = Today.AddDays(-1) })).Rows.Should().BeEmpty();
    }
}
