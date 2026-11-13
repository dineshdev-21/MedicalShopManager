using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Receive payment from customers (Payment In, oldest dues first) and pay suppliers (Payment Out), cancel vouchers,
/// receivable / payable ageing and party ledgers. Validates with <see cref="FinanceRules"/> before anything is written;
/// the repository re-checks the dues inside the posting transaction. Every posting and cancellation is audited.
/// </summary>
public sealed class PartyPaymentService : IPartyPaymentService
{
    private readonly IFinancePartyRepository _parties;
    private readonly IFinanceVoucherRepository _vouchers;
    private readonly IFinanceAccountRepository _accounts;
    private readonly IDocumentNumberService _numbers;
    private readonly IAuditService _audit;
    private readonly Func<DateTime> _today;

    public PartyPaymentService(IFinancePartyRepository parties, IFinanceVoucherRepository vouchers, IFinanceAccountRepository accounts,
        IDocumentNumberService numbers, IAuditService audit, Func<DateTime>? today = null)
    {
        _parties = parties;
        _vouchers = vouchers;
        _accounts = accounts;
        _numbers = numbers;
        _audit = audit;
        _today = today ?? (() => DateTime.Today);
    }

    public Task<IReadOnlyList<FinanceParty>> SearchPartiesAsync(PartyType partyType, string? text) =>
        _parties.SearchAsync(RequireParty(partyType), text?.Trim());

    public Task<FinanceParty?> GetPartyAsync(PartyType partyType, long partyId) => _parties.GetAsync(RequireParty(partyType), partyId);

    public Task<IReadOnlyList<DueDocument>> GetDuesAsync(PartyType partyType, long partyId) =>
        _parties.GetDuesAsync(RequireParty(partyType), partyId);

    public async Task<FinanceVoucher> PostAsync(VoucherRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = FinanceRules.ValidateVoucher(request, _today()).ToList();
        if (errors.Count == 0 && request.Allocations is { Count: > 0 } explicitAllocations
            && explicitAllocations.Sum(a => a.AmountPaisa) > request.AmountPaisa)
            errors.Add($"The bills selected add up to more than the amount of {FinanceRules.Rupees(request.AmountPaisa)}.");
        if (errors.Count == 0 && request.Kind == FinanceVoucherKind.Payment && request.Mode != FinancePaymentMode.Cheque
            && request.AccountId is { } accountId)
        {
            var account = (await _accounts.ListAsync(_today(), includeInactive: true)).FirstOrDefault(a => a.Id == accountId);
            if (account is null) errors.Add("The chosen account was not found. Reload and choose again.");
            else errors.AddRange(FinanceRules.ValidateCashAvailable(account, request.AmountPaisa));
        }
        if (errors.Count > 0) throw new FinanceValidationException(errors);

        var voucher = await _vouchers.PostAsync(request with
        {
            Reference = request.Reference?.Trim(),
            Notes = request.Notes?.Trim(),
            Cheque = request.Cheque is { } c ? c with { ChequeNumber = c.ChequeNumber.Trim(), BankName = c.BankName?.Trim() } : null
        });
        await FinanceAudit.LogAsync(_audit, voucher.Kind == FinanceVoucherKind.Receipt ? "FinanceReceiptPosted" : "FinancePaymentPosted",
            "FinanceVoucher", voucher.Id, null, new
            {
                voucher.VoucherNumber, voucher.PartyType, voucher.PartyId, voucher.PartyName, Date = voucher.VoucherDate.ToString("yyyy-MM-dd"),
                voucher.Mode, voucher.AccountId, voucher.AmountPaisa, voucher.ChequeNumber,
                Allocations = voucher.Allocations.Select(a => new { a.Target, a.DocumentId, a.Number, a.AmountPaisa })
            });
        return voucher;
    }

    public Task<FinanceVoucher?> GetVoucherAsync(long voucherId) => _vouchers.GetAsync(voucherId);

    public Task<IReadOnlyList<FinanceVoucher>> GetVouchersAsync(VoucherFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.From.Date > filter.To.Date) throw new FinanceValidationException("The from date is after the to date.");
        return _vouchers.ListAsync(filter with { SearchText = filter.SearchText?.Trim() });
    }

    public async Task CancelVoucherAsync(long voucherId, string reason, long? userId)
    {
        var errors = FinanceRules.ValidateReason(reason, "receipt / payment");
        if (errors.Count > 0) throw new FinanceValidationException(errors);
        var voucher = await _vouchers.GetAsync(voucherId) ?? throw new FinanceValidationException("The receipt / payment was not found.");
        await _vouchers.CancelAsync(voucherId, reason.Trim());
        await FinanceAudit.LogAsync(_audit, "FinanceVoucherCancelled", "FinanceVoucher", voucherId,
            new { voucher.VoucherNumber, voucher.PartyName, voucher.AmountPaisa, voucher.Mode, Allocations = voucher.AllocationSummary },
            null, reason.Trim());
    }

    public Task<IReadOnlyList<PartyBalanceRow>> GetBalancesAsync(PartyType partyType, DateTime asOf, bool includeSettled = false) =>
        _parties.GetBalancesAsync(RequireParty(partyType), asOf.Date, includeSettled);

    public async Task<PartyLedger> GetLedgerAsync(PartyType partyType, long partyId, DateTime from, DateTime to)
    {
        CashBankService.ValidateRange(from, to);
        return await _parties.GetLedgerAsync(RequireParty(partyType), partyId, from.Date, to.Date);
    }

    public Task<string> PeekNumberAsync(FinanceVoucherKind kind, DateTime date) =>
        _numbers.PeekNextAsync(kind == FinanceVoucherKind.Receipt ? DocumentType.Receipt : DocumentType.Payment, date);

    private static PartyType RequireParty(PartyType partyType) => partyType is PartyType.Customer or PartyType.Supplier
        ? partyType
        : throw new FinanceValidationException("Receipts, payments and ledgers are kept for customers and suppliers only.");
}
