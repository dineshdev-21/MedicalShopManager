using MedicalShopManager.Core.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>Supplier invoice CSV parsing, column mapping suggestions and row conversion.</summary>
public class PurchasesImportParserTests
{
    private static readonly DateTime Today = new(2026, 11, 4);

    private const string Csv =
        "Item Name,HSN,Batch No.,Exp.,Pack,Qty,Free Qty,MRP,Rate,Disc %,GST %\r\n" +
        "\"Dolo 650, strip\",30049099,DL123,03/28,15's,10,1,\"34.00\",25.50,5,12\r\n" +
        "Crocin,3004,CR9,Mar-27,1x10,5,,20,15,,12\r\n" +
        ",,,,,,,,,,\r\n";

    [Fact]
    public void ParseDelimited_HandlesQuotesCommasAndBlankRows()
    {
        var table = PurchaseImportParser.ParseDelimited(Csv);
        Assert.Equal(11, table.Headers.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Dolo 650, strip", table.Rows[0][0]);
        Assert.Equal("34.00", table.Rows[0][7]);
    }

    [Fact]
    public void ParseDelimited_DetectsSemicolonsAndTabs()
    {
        Assert.Equal(new[] { "a", "b" }, PurchaseImportParser.ParseDelimited("a;b\n1;2").Headers);
        Assert.Equal("2", PurchaseImportParser.ParseDelimited("a\tb\n1\t2").Rows[0][1]);
        Assert.Equal("a", PurchaseImportParser.ParseDelimited("﻿a,b\n1,2").Headers[0]);
    }

    [Fact]
    public void SuggestMapping_RecognisesCommonHeaders_AndDoesNotConfuseFreeWithQty()
    {
        var table = PurchaseImportParser.ParseDelimited(Csv);
        var mapping = PurchaseImportParser.SuggestMapping(table.Headers);
        Assert.Equal(new[]
        {
            PurchaseImportField.ProductName, PurchaseImportField.HsnCode, PurchaseImportField.BatchNumber, PurchaseImportField.Expiry,
            PurchaseImportField.UnitsPerPack, PurchaseImportField.Quantity, PurchaseImportField.FreeQuantity, PurchaseImportField.Mrp,
            PurchaseImportField.Rate, PurchaseImportField.DiscountPercent, PurchaseImportField.GstPercent
        }, mapping);
    }

    [Fact]
    public void MapRows_ConvertsMoneyPacksAndExpiry()
    {
        var table = PurchaseImportParser.ParseDelimited(Csv);
        var rows = PurchaseImportParser.MapRows(table, PurchaseImportParser.SuggestMapping(table.Headers), Today);

        Assert.Equal(2, rows.Count);
        var dolo = rows[0];
        Assert.True(dolo.IsValid, string.Join("; ", dolo.Errors));
        Assert.Equal(2, dolo.RowNumber);
        Assert.Equal(15, dolo.UnitsPerPack);
        Assert.Equal(10m, dolo.QuantityPacks);
        Assert.Equal(1m, dolo.FreePacks);
        Assert.Equal(3_400, dolo.MrpPaisa);
        Assert.Equal(2_550, dolo.RatePaisa);
        Assert.Equal(5m, dolo.DiscountPercent);
        Assert.Equal(12m, dolo.GstPercent);
        Assert.Equal(new DateTime(2028, 3, 31), dolo.ExpiryDate);

        var crocin = rows[1];
        Assert.True(crocin.IsValid, string.Join("; ", crocin.Errors));
        Assert.Equal(10, crocin.UnitsPerPack);
        Assert.Equal(new DateTime(2027, 3, 31), crocin.ExpiryDate);
        Assert.Equal(0m, crocin.FreePacks);
    }

    [Fact]
    public void MapRows_FlagsBadRows()
    {
        var table = PurchaseImportParser.ParseDelimited(
            "Item,Batch,Expiry,Qty,MRP,Rate\nX,B1,13/27,abc,10,5\nY,B2,01/26,1,0,5\nZ,B3,02/28,1.5,10,5");
        var rows = PurchaseImportParser.MapRows(table, PurchaseImportParser.SuggestMapping(table.Headers), Today);

        Assert.Contains(rows[0].Errors, e => e.StartsWith("Expiry '13/27'", StringComparison.Ordinal));
        Assert.Contains(rows[0].Errors, e => e.StartsWith("Qty 'abc'", StringComparison.Ordinal));
        Assert.Contains("Batch is expired.", rows[1].Errors);
        Assert.Contains("MRP is missing.", rows[1].Errors);
        Assert.Contains("Qty must be whole packs.", rows[2].Errors);
    }

    [Fact]
    public void MapRows_RequiresTheEssentialColumns()
    {
        var table = PurchaseImportParser.ParseDelimited("Item,Qty\nX,1");
        var ex = Assert.Throws<PurchaseRuleException>(() =>
            PurchaseImportParser.MapRows(table, PurchaseImportParser.SuggestMapping(table.Headers), Today));
        Assert.Contains("Batch no", ex.Message);
    }

    [Theory]
    [InlineData("03/28", 2028, 3)]
    [InlineData("3/28", 2028, 3)]
    [InlineData("03-2028", 2028, 3)]
    [InlineData("Mar-28", 2028, 3)]
    [InlineData("MAR 2028", 2028, 3)]
    [InlineData("0328", 2028, 3)]
    [InlineData("15-03-2028", 2028, 3)]
    [InlineData("2028-03-15", 2028, 3)]
    public void ParseExpiry_GivesTheLastDayOfTheMonth(string text, int year, int month) =>
        Assert.Equal(new DateTime(year, month, DateTime.DaysInMonth(year, month)), PurchaseImportParser.ParseExpiry(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("13/28")]
    public void ParseExpiry_RejectsNonDates(string text) => Assert.Null(PurchaseImportParser.ParseExpiry(text));

    [Theory]
    [InlineData("10", 10)]
    [InlineData("15's", 15)]
    [InlineData("1x10", 10)]
    [InlineData("1 X 15 TAB", 15)]
    public void ParsePack(string text, int expected) => Assert.Equal(expected, PurchaseImportParser.ParsePack(text));

    [Theory]
    [InlineData("")]
    [InlineData("strip")]
    [InlineData("0")]
    public void ParsePack_Invalid(string text) => Assert.Null(PurchaseImportParser.ParsePack(text));
}
