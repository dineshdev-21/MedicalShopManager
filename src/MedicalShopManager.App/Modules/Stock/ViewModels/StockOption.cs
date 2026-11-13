namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>A value with its display label for combo boxes (filters, schedules, reasons).</summary>
public sealed record StockOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
