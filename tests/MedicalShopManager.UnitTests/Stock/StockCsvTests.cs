using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;
using Xunit;

namespace MedicalShopManager.UnitTests.Stock;

/// <summary>Opening-stock / item-master CSV parsing, mapping and row validation (<see cref="StockCsv"/>).</summary>
public class StockCsvTests
{
    private const string Header =
        "Item Name,Manufacturer,Composition,Category,Pack,Units Per Pack,HSN,GST,Schedule,Barcode,Rack,Min Stock,Max Stock,Batch No,Expiry,MRP,Purchase Rate,PTR,Sale Rate,Qty,Loose";

    private static IReadOnlyList<StockImportColumn> Mapping() => StockCsv.SuggestMapping(StockCsv.Parse(Header)[0]);

    private static StockImportRow Row(string line) => StockCsv.BuildRow(StockCsv.Parse(Header + "\n" + line)[1], Mapping(), 2);

    // ───────── records ─────────

    [Fact]
    public void Parse_HandlesQuotesEmbeddedDelimitersNewlinesAndBom()
    {
        var records = StockCsv.Parse("﻿Name,Notes\r\n\"Dolo, 650\",\"He said \"\"ok\"\"\nline 2\"\r\n\r\nCrocin,plain\r\n");
        records.Should().HaveCount(3);
        records[0].Should().Equal("Name", "Notes");
        records[1].Should().Equal("Dolo, 650", "He said \"ok\"\nline 2");
        records[2].Should().Equal("Crocin", "plain");
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    public void DetectDelimiter_UsesTheHeaderLine(string text, char expected)
    {
        StockCsv.DetectDelimiter(text).Should().Be(expected);
        StockCsv.Parse(text)[1].Should().Equal("1", "2", "3");
    }

    [Fact]
    public void Parse_EmptyTextHasNoRecords() => StockCsv.Parse(string.Empty).Should().BeEmpty();

    // ───────── mapping ─────────

    [Fact]
    public void SuggestMapping_RecognisesTheTemplateHeaders()
    {
        Mapping().Select(c => c.Field).Should().Equal(
            StockImportField.Name, StockImportField.Manufacturer, StockImportField.Composition, StockImportField.Category,
            StockImportField.PackLabel, StockImportField.UnitsPerPack, StockImportField.HsnCode, StockImportField.GstPercent,
            StockImportField.Schedule, StockImportField.Barcode, StockImportField.RackLocation, StockImportField.MinStock,
            StockImportField.MaxStock, StockImportField.BatchNumber, StockImportField.Expiry, StockImportField.Mrp,
            StockImportField.PurchaseRate, StockImportField.Ptr, StockImportField.SaleRate, StockImportField.QuantityPacks,
            StockImportField.LooseUnits);
    }

    [Fact]
    public void SuggestMapping_UsesEachFieldOnce_AndIgnoresUnknownHeaders()
    {
        var columns = StockCsv.SuggestMapping(new[] { "Product Name", "Item", "Batch", "Exp. Date", "M.R.P.", "Whatever" });
        columns.Select(c => c.Field).Should().Equal(StockImportField.Name, StockImportField.Ignore, StockImportField.BatchNumber,
            StockImportField.Expiry, StockImportField.Mrp, StockImportField.Ignore);
    }

    [Fact]
    public void ValidateMapping_RequiresNameAndCompleteBatchColumns()
    {
        StockCsv.ValidateMapping(Mapping()).Should().BeEmpty();
        StockCsv.ValidateMapping(new[] { new StockImportColumn(0, "Mfr", StockImportField.Manufacturer) })
            .Should().ContainSingle(e => e.Contains("Name"));
        StockCsv.ValidateMapping(new[]
        {
            new StockImportColumn(0, "Name", StockImportField.Name), new StockImportColumn(1, "Batch", StockImportField.BatchNumber)
        }).Should().HaveCount(3, "expiry, MRP and quantity are missing");
        StockCsv.ValidateMapping(new[]
        {
            new StockImportColumn(0, "A", StockImportField.Name), new StockImportColumn(1, "B", StockImportField.Name)
        }).Should().ContainSingle(e => e.Contains("more than one column"));
    }

    // ───────── values ─────────

    [Theory]
    [InlineData("188.40", true, 18840)]
    [InlineData("₹1,234.5", true, 123450)]
    [InlineData("Rs. 35.505", true, 3551)]
    [InlineData("", true, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("-5", false, 0)]
    public void Money_RupeesToPaisa(string text, bool ok, long paisa)
    {
        StockCsv.TryParseMoney(text, out var value).Should().Be(ok);
        if (ok) value.Should().Be(paisa);
    }

    [Theory]
    [InlineData("10", true, 10)]
    [InlineData("1,200", true, 1200)]
    [InlineData("10.0", true, 10)]
    [InlineData("10.5", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("", true, 0)]
    public void Whole_AcceptsSpreadsheetIntegers(string text, bool ok, long expected)
    {
        StockCsv.TryParseWhole(text, out var value).Should().Be(ok);
        if (ok) value.Should().Be(expected);
    }

    [Theory]
    [InlineData("08/27", 8, 2027)]
    [InlineData("8/27", 8, 2027)]
    [InlineData("08/2027", 8, 2027)]
    [InlineData("08-27", 8, 2027)]
    [InlineData("Aug-27", 8, 2027)]
    [InlineData("2027-08-15", 8, 2027)]
    [InlineData("15/08/2027", 8, 2027)]
    [InlineData("0827", 8, 2027)]
    public void Expiry_AcceptsCommonFormats(string text, int month, int year)
    {
        StockCsv.TryParseExpiry(text, out var m, out var y).Should().BeTrue();
        (m, y).Should().Be((month, year));
    }

    [Theory]
    [InlineData("")]
    [InlineData("13/27")]
    [InlineData("soon")]
    public void Expiry_RejectsNonsense(string text) => StockCsv.TryParseExpiry(text, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("", DrugSchedule.None)]
    [InlineData("OTC", DrugSchedule.None)]
    [InlineData("H", DrugSchedule.H)]
    [InlineData("Sch H1", DrugSchedule.H1)]
    [InlineData("schedule x", DrugSchedule.X)]
    [InlineData("NDPS", DrugSchedule.NRx)]
    [InlineData("Rx", DrugSchedule.Rx)]
    [InlineData("-", DrugSchedule.None)]
    public void Schedule_ParsesLabels(string text, DrugSchedule expected)
    {
        StockCsv.TryParseSchedule(text, out var schedule).Should().BeTrue();
        schedule.Should().Be(expected);
    }

    [Fact]
    public void Schedule_UnknownIsRejected() => StockCsv.TryParseSchedule("Z9", out _).Should().BeFalse();

    [Theory]
    [InlineData("15's", 15)]
    [InlineData("10 x 10", 10)]
    [InlineData("strip of 10", 10)]
    [InlineData("100ml", 1)]
    [InlineData("30 gm", 1)]
    [InlineData("", 1)]
    [InlineData("bottle", 1)]
    public void UnitsFromPackLabel(string label, int expected) => StockCsv.UnitsFromPackLabel(label).Should().Be(expected);

    // ───────── rows ─────────

    [Fact]
    public void ValidOpeningStockRow_IsParsedToPaisaAndUnits()
    {
        var row = Row("Dolo 650,Micro Labs,Paracetamol 650mg,Tablets,15's,15,30049099,12,OTC,8901,A-1,30,150,dl24051,08/27,30.91,22.10,24.50,30.91,10,5");
        row.Errors.Should().BeEmpty();
        row.Warnings.Should().BeEmpty();
        row.Name.Should().Be("Dolo 650");
        row.BatchNumber.Should().Be("DL24051");
        row.MrpPaisa.Should().Be(3091);
        row.PurchaseRatePaisa.Should().Be(2210);
        row.PtrPaisa.Should().Be(2450);
        row.Units.Should().Be(155);
        row.ExpiryLabel.Should().Be("08/27");
        row.QuantityLabel.Should().Be("10 + 5");
        row.HasBatch.Should().BeTrue();
        row.ActionLabel.Should().Be("New item + batch");
    }

    [Fact]
    public void ItemMasterRow_WithoutBatch_IsValidAndWarnsAboutIgnoredQuantity()
    {
        var row = Row("Crocin,GSK,,,10's,,,5,,,,,,,,45,,,,3,");
        row.IsValid.Should().BeTrue();
        row.HasBatch.Should().BeFalse();
        row.UnitsPerPack.Should().Be(10, "taken from the pack label");
        row.Warnings.Should().ContainSingle(w => w.Contains("only the item master"));
        row.ActionLabel.Should().Be("New item");
    }

    [Fact]
    public void MissingGst_AssumesTheDefaultWithAWarning()
    {
        var columns = new[] { new StockImportColumn(0, "Name", StockImportField.Name) };
        var row = StockCsv.BuildRow(new[] { "Zinc" }, columns, 2, defaultGstPercent: 18m);
        row.GstPercent.Should().Be(18m);
        row.Warnings.Should().ContainSingle(w => w.Contains("18%"));
    }

    [Fact]
    public void InvalidRow_CollectsEveryProblem()
    {
        var row = Row(",X,,,15's,0,123,40,Z9,,,50,10,B1,99/99,abc,500,,600,0,0");
        row.IsValid.Should().BeFalse();
        row.ActionLabel.Should().Be("Error");
        row.Errors.Should().Contain(e => e.Contains("name is empty"));
        row.Errors.Should().Contain(e => e.Contains("Units per pack"));
        row.Errors.Should().Contain(e => e.Contains("HSN"));
        row.Errors.Should().Contain(e => e.Contains("GST"));
        row.Errors.Should().Contain(e => e.Contains("Schedule"));
        row.Errors.Should().Contain(e => e.Contains("Max stock"));
        row.Errors.Should().Contain(e => e.Contains("Expiry"));
        row.Errors.Should().Contain(e => e.Contains("MRP"));
        row.Errors.Should().Contain(e => e.Contains("quantity is zero"));
    }

    [Fact]
    public void BatchRow_RatesAboveMrpAndBadLooseAreErrors()
    {
        var row = Row("Dolo,Micro,,,15's,15,,12,,,,,,B1,08/27,100,120,130,140,1,15");
        row.Errors.Should().Contain(e => e.Contains("Sale rate is more than the MRP"));
        row.Errors.Should().Contain(e => e.Contains("Purchase rate is more than the MRP"));
        row.Errors.Should().Contain(e => e.Contains("PTR is more than the MRP"));
        row.Errors.Should().Contain(e => e.Contains("Loose units"));
    }

    [Fact]
    public void RowWithoutPackSize_TakesTheGivenPackSizeAndThenChecksLoose()
    {
        var columns = StockCsv.SuggestMapping(new[] { "Name", "Batch", "Expiry", "MRP", "Qty", "Loose" });
        var row = StockCsv.BuildRow(new[] { "Dolo", "B1", "08/27", "188.40", "2", "5" }, columns, 2);
        row.PackSizeGiven.Should().BeFalse();
        row.IsValid.Should().BeTrue("loose units wait for the pack size");

        StockCsv.ApplyPackSize(row, 15);
        row.UnitsPerPack.Should().Be(15);
        row.Units.Should().Be(35);
        row.IsValid.Should().BeTrue();

        var single = StockCsv.BuildRow(new[] { "Dolo", "B1", "08/27", "188.40", "2", "5" }, columns, 2);
        StockCsv.ApplyPackSize(single, 1);
        single.Errors.Should().ContainSingle(e => e.Contains("Pack size is 1"));

        var given = Row("Dolo,Micro,,,10's,,,12,,,,,,B1,08/27,100,50,,,1,");
        StockCsv.ApplyPackSize(given, 15);
        given.UnitsPerPack.Should().Be(10, "the row's own pack size wins");
    }

    [Fact]
    public void DuplicateBatchesInTheFile_AreFlaggedAfterTheFirst()
    {
        var rows = new[]
        {
            Row("Dolo,Micro,,,15's,15,,12,,,,,,B1,08/27,100,50,,,1,"),
            Row("DOLO, micro ,,,15's,15,,12,,,,,,b1,09/27,100,50,,,2,"),
            Row("Dolo,Micro,,,15's,15,,12,,,,,,B2,08/27,100,50,,,1,")
        };
        StockCsv.FlagDuplicateBatches(rows);
        rows[0].IsValid.Should().BeTrue();
        rows[1].Errors.Should().ContainSingle(e => e.Contains("more than once"));
        rows[2].IsValid.Should().BeTrue();
    }

    [Fact]
    public void Preview_CountsDistinctNewItemsAndBatches()
    {
        var rows = new[]
        {
            Row("Dolo,Micro,,,15's,15,,12,,,,,,B1,08/27,100,50,,,1,"),
            Row("Dolo,Micro,,,15's,15,,12,,,,,,B2,08/27,100,50,,,1,"),
            Row("Crocin,GSK,,,10's,10,,12,,,,,,,,45,,,,,"),
            Row(",bad,,,,,,,,,,,,,,,,,,,")
        };
        var preview = new StockImportPreview(Mapping(), rows, Array.Empty<string>());
        preview.ValidCount.Should().Be(3);
        preview.InvalidCount.Should().Be(1);
        preview.NewItemCount.Should().Be(2);
        preview.BatchCount.Should().Be(2);
        preview.CanImport.Should().BeTrue();
        (preview with { FileErrors = new[] { "x" } }).CanImport.Should().BeFalse();
    }
}
