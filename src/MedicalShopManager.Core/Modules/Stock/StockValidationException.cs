namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>A Stock rule was broken; <see cref="Exception.Message"/> is written for the user and shown as-is.</summary>
public sealed class StockValidationException : Exception
{
    public StockValidationException(string message) : base(message) { }
}
