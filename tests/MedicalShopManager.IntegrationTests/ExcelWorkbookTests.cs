using ClosedXML.Excel;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

public class ExcelWorkbookTests : IDisposable
{
    private readonly string _tempExcelPath;

    public ExcelWorkbookTests()
    {
        _tempExcelPath = Path.Combine(Path.GetTempPath(), $"MSM_ExcelTest_{Guid.NewGuid():N}.xlsx");
    }

    [Fact]
    public void ClosedXml_CanCreate14Worksheets_WithStylesAndFormatting()
    {
        string[] sheetNames =
        {
            "Medicines", "Batches", "Sales", "SaleItems",
            "Purchases", "PurchaseItems", "Suppliers", "Customers",
            "Payments", "Expenses", "StockMovements", "Returns",
            "AuditLog", "AppSettings"
        };

        using (var wb = new XLWorkbook())
        {
            foreach (var name in sheetNames)
            {
                var ws = wb.Worksheets.Add(name);
                ws.Cell(1, 1).Value = "ID";
                ws.Cell(1, 2).Value = "Sample Column";
                ws.Cell(2, 1).Value = 1;
                ws.Cell(2, 2).Value = "Sample Value";

                // Format header
                var header = ws.Range(1, 1, 1, 2);
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.FromArgb(0, 120, 212);
                header.Style.Font.FontColor = XLColor.White;
                ws.SheetView.FreezeRows(1);
            }

            wb.SaveAs(_tempExcelPath);
        }

        // Verify file was written and can be read back
        Assert.True(File.Exists(_tempExcelPath));

        using (var readWb = new XLWorkbook(_tempExcelPath))
        {
            Assert.Equal(14, readWb.Worksheets.Count);
            foreach (var name in sheetNames)
            {
                var ws = readWb.Worksheet(name);
                Assert.NotNull(ws);
                Assert.Equal("ID", ws.Cell(1, 1).GetString());
            }
        }
    }

    public void Dispose()
    {
        if (File.Exists(_tempExcelPath))
        {
            try { File.Delete(_tempExcelPath); } catch { /* ignore */ }
        }
    }
}
