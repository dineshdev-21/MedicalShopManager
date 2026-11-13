using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Stock;

/// <summary>Opening-stock / item-master CSV import end to end: mapping, preview against the database, then import.</summary>
public class StockImportTests : StockDbTestBase
{
    private const string Csv =
        "Item Name,Manufacturer,Composition,Pack,GST,Schedule,Rack,Batch No,Expiry,MRP,Purchase Rate,Qty,Loose\n" +
        "Dolo 650,Micro Labs,Paracetamol 650mg,15's,12,OTC,A-1,DL1,08/27,30.91,22.10,10,5\n" +
        "Dolo 650,Micro Labs,Paracetamol 650mg,15's,12,OTC,A-1,DL2,10/27,30.91,22.10,4,\n" +
        "Azithral 500,Alembic,Azithromycin 500mg,5's,12,H,B-2,AZ9,03/28,120.50,80,6,\n" +
        "Crocin,GSK,Paracetamol 500mg,10's,12,,C-3,,,25,,,\n" +
        ",Nobody,,,,,,X1,08/27,10,5,1,\n";

    [Fact]
    public async Task Preview_ValidatesRowsWithoutWriting()
    {
        var import = Import();
        var mapping = import.SuggestMapping(Csv);
        var preview = await import.PreviewAsync(Csv, mapping);

        preview.FileErrors.Should().BeEmpty();
        preview.Rows.Should().HaveCount(5);
        preview.ValidCount.Should().Be(4);
        preview.NewItemCount.Should().Be(3);
        preview.BatchCount.Should().Be(3);
        (await CountAsync("Medicines")).Should().Be(0);
        (await CountAsync("MedicineBatches")).Should().Be(0);
    }

    [Fact]
    public async Task Import_CreatesItemsOnceAndOpeningBatchesWithMovements()
    {
        var import = Import();
        var preview = await import.PreviewAsync(Csv, import.SuggestMapping(Csv));
        var result = await import.ImportAsync(preview);

        result.ItemsCreated.Should().Be(3);
        result.BatchesCreated.Should().Be(3);
        result.RowsSkipped.Should().Be(1);
        result.Errors.Should().BeEmpty();

        (await LongAsync("SELECT COUNT(*) FROM Medicines WHERE Name = 'Dolo 650'")).Should().Be(1);
        (await LongAsync("SELECT UnitsPerPack FROM Medicines WHERE Name = 'Dolo 650'")).Should().Be(15);
        (await LongAsync("SELECT Schedule FROM Medicines WHERE Name = 'Azithral 500'")).Should().Be((int)DrugSchedule.H);
        (await LongAsync("SELECT DefaultMrpPaisa FROM Medicines WHERE Name = 'Azithral 500'")).Should().Be(12050);
        (await LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE BatchNumber = 'DL1'")).Should().Be(155);
        (await LongAsync("SELECT PurchaseRatePaisa FROM MedicineBatches WHERE BatchNumber = 'DL1'")).Should().Be(2210);
        (await ScalarAsync("SELECT ExpiryDate FROM MedicineBatches WHERE BatchNumber = 'AZ9'")).Should().Be("2028-03-31");
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE MovementType = @T", ("@T", (int)MovementType.OpeningStock))).Should().Be(3);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'StockImport'")).Should().Be(1);

        var list = await Service().GetStockListAsync(new StockListQuery());
        list.Items.Single(i => i.Name == "Dolo 650").StockUnits.Should().Be(155 + 60);
        list.Items.Single(i => i.Name == "Crocin").IsInStock.Should().BeFalse();
    }

    [Fact]
    public async Task ReImport_AttachesToExistingItems_AndFlagsBatchesAlreadyInStock()
    {
        var import = Import();
        await import.ImportAsync(await import.PreviewAsync(Csv, import.SuggestMapping(Csv)));

        var again = "Item Name,Manufacturer,Batch No,Expiry,MRP,Qty\n" +
                    "dolo 650,micro labs,DL1,08/27,30.91,1\n" +
                    "Dolo 650,Micro Labs,DL3,12/27,30.91,2\n";
        var preview = await import.PreviewAsync(again, import.SuggestMapping(again));
        preview.Rows[0].Errors.Should().ContainSingle(e => e.Contains("already exists"));
        preview.Rows[1].IsValid.Should().BeTrue();
        preview.Rows[1].CreatesItem.Should().BeFalse();
        preview.Rows[1].UnitsPerPack.Should().Be(15, "the file has no pack column, so the existing item's pack size is used");
        preview.NewItemCount.Should().Be(0);

        var result = await import.ImportAsync(preview);
        result.ItemsCreated.Should().Be(0);
        result.BatchesCreated.Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM Medicines WHERE Name = 'Dolo 650'")).Should().Be(1);
        (await LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE BatchNumber = 'DL3'")).Should().Be(30);
        (await LongAsync("SELECT UnitsPerPack FROM MedicineBatches WHERE BatchNumber = 'DL3'")).Should().Be(15);
    }

    [Fact]
    public async Task Preview_ReportsMappingProblems()
    {
        var csv = "Manufacturer,Batch\nCipla,B1\n";
        var import = Import();
        var preview = await import.PreviewAsync(csv, import.SuggestMapping(csv));
        preview.CanImport.Should().BeFalse();
        preview.FileErrors.Should().Contain(e => e.Contains("Name"));
        var act = () => import.ImportAsync(preview);
        await act.Should().ThrowAsync<StockValidationException>();
    }

    [Fact]
    public async Task Preview_EmptyFileIsAnError()
    {
        var import = Import();
        var preview = await import.PreviewAsync("Item Name\n", import.SuggestMapping("Item Name\n"));
        preview.FileErrors.Should().Contain(e => e.Contains("no data rows"));
    }
}
