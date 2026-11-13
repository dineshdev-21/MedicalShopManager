using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.Infrastructure.Modules.SalesReturns;

/// <summary>Sales register with validated filters and footer totals.</summary>
public sealed class SalesRegisterService : ISalesRegisterService
{
    private readonly ISalesRegisterRepository _repository;

    public SalesRegisterService(ISalesRegisterRepository repository) => _repository = repository;

    public async Task<(IReadOnlyList<SalesRegisterRow> Rows, SalesRegisterTotals Totals)> QueryAsync(SalesRegisterFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (SalesRegisterRules.Validate(filter) is { } error) throw new SaleReturnValidationException(error);
        var rows = await _repository.QueryAsync(filter);
        return (rows, SalesRegisterRules.Summarize(rows));
    }

    public Task<IReadOnlyList<SalesRegisterRow>> SearchBillsAsync(string text) => _repository.SearchBillsAsync(text);
}
