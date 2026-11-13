using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Cash &amp; bank: accounts with opening balances, the payment-method → account map, transfers and add / reduce money
/// entries, and account statements. Validates with <see cref="FinanceRules"/> and audits every change.
/// </summary>
public sealed class CashBankService : ICashBankService
{
    /// <summary>Longest statement range (keeps the derived ledger query bounded).</summary>
    public const int MaxStatementDays = 366 * 5;

    private readonly IFinanceAccountRepository _accounts;
    private readonly IAuditService _audit;
    private readonly Func<DateTime> _today;

    public CashBankService(IFinanceAccountRepository accounts, IAuditService audit, Func<DateTime>? today = null)
    {
        _accounts = accounts;
        _audit = audit;
        _today = today ?? (() => DateTime.Today);
    }

    public Task<IReadOnlyList<FinanceAccount>> GetAccountsAsync(bool includeInactive = false) =>
        _accounts.ListAsync(_today(), includeInactive);

    public async Task<FinanceAccount> SaveAccountAsync(FinanceAccount account, long? userId)
    {
        ArgumentNullException.ThrowIfNull(account);
        var errors = FinanceRules.ValidateAccount(account).ToList();
        if (account.OpeningDate.Date > _today().Date) errors.Add("The opening balance date cannot be in the future.");
        if (!string.IsNullOrWhiteSpace(account.Name) && await _accounts.NameExistsAsync(account.Name, account.Id))
            errors.Add($"An account named \"{account.Name.Trim()}\" already exists.");
        FinanceAccount? existing = null;
        if (account.Id > 0)
        {
            existing = await _accounts.GetAsync(account.Id);
            if (existing is null) errors.Add("The account was not found. It may have been removed.");
            else if (existing.AccountType != account.AccountType && existing.AccountType == FinanceAccountType.Cash)
                errors.Add("Cash in hand cannot be changed to another account type.");
        }
        if (errors.Count > 0) throw new FinanceValidationException(errors);

        if (account.Id > 0)
        {
            await _accounts.UpdateAsync(account);
            await FinanceAudit.LogAsync(_audit, "FinanceAccountUpdated", "FinanceAccount", account.Id, Snapshot(existing!), Snapshot(account));
            return await _accounts.GetAsync(account.Id) ?? account;
        }

        var id = await _accounts.InsertAsync(account);
        await FinanceAudit.LogAsync(_audit, "FinanceAccountCreated", "FinanceAccount", id, null, Snapshot(account));
        return await _accounts.GetAsync(id) ?? throw new InvalidOperationException("The account was saved but could not be read back.");
    }

    public async Task SetAccountActiveAsync(long accountId, bool isActive, long? userId)
    {
        await _accounts.SetActiveAsync(accountId, isActive);
        await FinanceAudit.LogAsync(_audit, isActive ? "FinanceAccountActivated" : "FinanceAccountDeactivated", "FinanceAccount", accountId);
    }

    public Task<IReadOnlyList<PaymentMethodAccount>> GetMethodAccountsAsync() => _accounts.GetMethodAccountsAsync();

    public async Task SetMethodAccountAsync(PaymentMethod method, long accountId, long? userId)
    {
        var before = (await _accounts.GetMethodAccountsAsync()).FirstOrDefault(m => m.Method == method);
        await _accounts.SetMethodAccountAsync(method, accountId);
        await FinanceAudit.LogAsync(_audit, "FinanceMethodAccountChanged", "FinanceMethodAccount", (long)method,
            before is null ? null : new { before.Method, before.AccountId }, new { Method = method, AccountId = accountId });
    }

    public async Task<AccountStatement> GetStatementAsync(long accountId, DateTime from, DateTime to)
    {
        ValidateRange(from, to);
        return await _accounts.GetStatementAsync(accountId, from.Date, to.Date);
    }

    public async Task<long> RecordMovementAsync(FinanceMovement movement, long? userId)
    {
        ArgumentNullException.ThrowIfNull(movement);
        var errors = FinanceRules.ValidateMovement(movement, _today()).ToList();
        if (errors.Count == 0 && movement.FromAccountId is { } fromId)
        {
            var accounts = await _accounts.ListAsync(_today(), includeInactive: true);
            var from = accounts.FirstOrDefault(a => a.Id == fromId);
            if (from is null) errors.Add("The account to take money from was not found.");
            else errors.AddRange(FinanceRules.ValidateCashAvailable(from, movement.AmountPaisa));
        }
        if (errors.Count > 0) throw new FinanceValidationException(errors);

        movement.CreatedByUserId = userId;
        var id = await _accounts.InsertMovementAsync(movement);
        await FinanceAudit.LogAsync(_audit, "FinanceMovementRecorded", "FinanceMovement", id, null, new
        {
            movement.Kind, Date = movement.MovementDate.ToString("yyyy-MM-dd"), movement.FromAccountId, movement.ToAccountId,
            movement.AmountPaisa, movement.Reference, movement.Notes
        });
        return id;
    }

    public async Task CancelMovementAsync(long movementId, string reason, long? userId)
    {
        var errors = FinanceRules.ValidateReason(reason, "entry");
        if (errors.Count > 0) throw new FinanceValidationException(errors);
        var movement = await _accounts.GetMovementAsync(movementId) ?? throw new FinanceValidationException("The entry was not found.");
        await _accounts.CancelMovementAsync(movementId, reason);
        await FinanceAudit.LogAsync(_audit, "FinanceMovementCancelled", "FinanceMovement", movementId,
            new { movement.Kind, movement.AmountPaisa, movement.FromAccountId, movement.ToAccountId }, null, reason.Trim());
    }

    public async Task<IReadOnlyList<FinanceMovement>> GetMovementsAsync(DateTime from, DateTime to)
    {
        ValidateRange(from, to);
        return await _accounts.ListMovementsAsync(from.Date, to.Date);
    }

    internal static void ValidateRange(DateTime from, DateTime to)
    {
        if (from.Date > to.Date) throw new FinanceValidationException("The from date is after the to date.");
        if ((to.Date - from.Date).TotalDays > MaxStatementDays) throw new FinanceValidationException("Choose a period of at most 5 years.");
    }

    private static object Snapshot(FinanceAccount a) => new
    {
        a.Name, a.AccountType, a.BankName, a.AccountNumber, a.Ifsc, a.HolderName, a.UpiId, a.OpeningBalancePaisa,
        OpeningDate = a.OpeningDate.ToString("yyyy-MM-dd"), a.Notes
    };
}
