using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.App.Printing;

/// <summary>Physical page geometry per <see cref="PaperSize"/>, in WPF device-independent units (1/96 inch).</summary>
public static class PaperLayout
{
    private const double DipPerMm = 96.0 / 25.4;

    /// <summary>
    /// Page width × height in mm. Thermal rolls are continuous; a long page keeps a receipt on one page and
    /// the printer driver cuts after the content.
    /// </summary>
    public static (double WidthMm, double HeightMm, double MarginMm) Dimensions(PaperSize size) => size switch
    {
        PaperSize.A4 => (210, 297, 12),
        PaperSize.A5 => (148, 210, 8),
        PaperSize.Thermal80mm => (80, 1000, 3),
        PaperSize.Thermal58mm => (58, 1000, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    public static double BaseFontSize(PaperSize size) => size switch
    {
        PaperSize.A4 => 11,
        PaperSize.A5 => 9.5,
        PaperSize.Thermal80mm => 9,
        _ => 8
    };

    /// <summary>Applies page size, margins and a single full-width column so the paginator lays out for the paper.</summary>
    public static void Apply(FlowDocument document, PaperSize size)
    {
        var (width, height, margin) = Dimensions(size);
        document.PageWidth = width * DipPerMm;
        document.PageHeight = height * DipPerMm;
        document.PagePadding = new Thickness(margin * DipPerMm);
        document.ColumnWidth = document.PageWidth;
        document.ColumnGap = 0;
        document.FontSize = BaseFontSize(size);
    }
}
