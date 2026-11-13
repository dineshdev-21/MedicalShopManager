using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Cheque register: deposit, clear (money enters / leaves the account on the cleared date), bounce or cancel (the
/// receipt / payment is reversed and the bills are due again). The state machine is <see cref="FinanceRules.CanTransition"/>;
/// the repository validates against the row read inside its transaction. Every status change is audited.
/// </summary>
public sealed class ChequeService : IChequeService
{
    private readonly IFinanceChequeRepository _cheques;
    private readonly IAuditService _audit;
    private readonly Func<DateTime> _today;

    public ChequeService(IFinanceChequeRepository cheques, IAuditService audit, Func<DateTime>? today = null)
    {
        _cheques = cheques;
        _audit = audit;
        _today = today ?? (() => DateTime.Today);
    }

    public Task<IReadOnlyList<FinanceCheque>> GetChequesAsync(ChequeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.OpenOnly && filter.From.Date > filter.To.Date) throw new FinanceValidationException("The from date is after the to date.");
        return _cheques.ListAsync(filter with { SearchText = filter.SearchText?.Trim() });
    }

    public async Task ChangeStatusAsync(ChequeStatusChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var cheque = await _cheques.GetAsync(change.ChequeId) ?? throw new FinanceValidationException("The cheque was not found.");
        var errors = FinanceRules.ValidateChequeChange(cheque, change).ToList();
        if (change.Date.Date > _today().Date) errors.Add("The date cannot be in the future.");
        if (errors.Count > 0) throw new FinanceValidationException(errors);

        await _cheques.ChangeStatusAsync(change with { Note = change.Note?.Trim() });
        await FinanceAudit.LogAsync(_audit, "FinanceCheque" + change.NewStatus, "FinanceCheque", cheque.Id,
            new { cheque.ChequeNumber, cheque.Status, cheque.AmountPaisa, cheque.PartyName, cheque.VoucherNumber },
            new { change.NewStatus, Date = change.Date.ToString("yyyy-MM-dd"), AccountId = change.AccountId ?? cheque.AccountId },
            change.Note?.Trim());
    }
}
