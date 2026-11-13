using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Expenses with categories, GST input (purchase side: CGST + SGST, or IGST for a payee in another state) and the
/// account they were paid from. GST maths is <see cref="FinanceRules.ComputeExpense"/> (PricingCalculator underneath);
/// numbers come from DocumentSequences (EXP) inside the insert transaction. Every change is audited.
/// </summary>
public sealed class ExpenseService : IExpenseService
{
    /// <summary>AppSettings keys holding the shop's GSTIN / state (written by Control Center); read-only here.</summary>
    internal const string ShopGstinKey = "ShopGstin";
    internal const string ShopStateCodeKey = "ControlCenter.Business.StateCode";

    private readonly IFinanceExpenseRepository _expenses;
    private readonly IFinanceAccountRepository _accounts;
    private readonly IDocumentNumberService _numbers;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly Func<DateTime> _today;

    public ExpenseService(IFinanceExpenseRepository expenses, IFinanceAccountRepository accounts, IDocumentNumberService numbers,
        IAppSettingsRepository settings, IAuditService audit, Func<DateTime>? today = null)
    {
        _expenses = expenses;
        _accounts = accounts;
        _numbers = numbers;
        _settings = settings;
        _audit = audit;
        _today = today ?? (() => DateTime.Today);
    }

    // ───────────────────────── Categories ─────────────────────────

    public Task<IReadOnlyList<FinanceExpenseCategory>> GetCategoriesAsync(bool includeInactive = false) =>
        _expenses.ListCategoriesAsync(includeInactive);

    public async Task<FinanceExpenseCategory> AddCategoryAsync(string name, long? userId)
    {
        var errors = FinanceRules.ValidateCategoryName(name);
        if (errors.Count > 0) throw new FinanceValidationException(errors);
        var id = await _expenses.InsertCategoryAsync(name.Trim());
        await FinanceAudit.LogAsync(_audit, "FinanceExpenseCategoryCreated", "FinanceExpenseCategory", id, null, new { Name = name.Trim() });
        return (await _expenses.ListCategoriesAsync(true)).First(c => c.Id == id);
    }

    public async Task RenameCategoryAsync(long categoryId, string name, long? userId)
    {
        var errors = FinanceRules.ValidateCategoryName(name);
        if (errors.Count > 0) throw new FinanceValidationException(errors);
        var before = (await _expenses.ListCategoriesAsync(true)).FirstOrDefault(c => c.Id == categoryId)
            ?? throw new FinanceValidationException("The category was not found.");
        await _expenses.RenameCategoryAsync(categoryId, name.Trim());
        await FinanceAudit.LogAsync(_audit, "FinanceExpenseCategoryRenamed", "FinanceExpenseCategory", categoryId,
            new { before.Name }, new { Name = name.Trim() });
    }

    public async Task SetCategoryActiveAsync(long categoryId, bool isActive, long? userId)
    {
        await _expenses.SetCategoryActiveAsync(categoryId, isActive);
        await FinanceAudit.LogAsync(_audit, isActive ? "FinanceExpenseCategoryActivated" : "FinanceExpenseCategoryDeactivated",
            "FinanceExpenseCategory", categoryId);
    }

    // ───────────────────────── Expenses ─────────────────────────

    public async Task<(IReadOnlyList<FinanceExpense> Rows, ExpenseTotals Totals)> GetExpensesAsync(ExpenseFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        CashBankService.ValidateRange(filter.From, filter.To);
        var rows = await _expenses.ListAsync(filter with { SearchText = filter.SearchText?.Trim() });
        return (rows, Totals(rows));
    }

    /// <summary>Totals of a list (cancelled rows excluded).</summary>
    public static ExpenseTotals Totals(IEnumerable<FinanceExpense> rows)
    {
        var live = rows.Where(e => !e.IsCancelled).ToList();
        return new ExpenseTotals(live.Count, live.Sum(e => e.TaxablePaisa), live.Sum(e => e.TaxPaisa), live.Sum(e => e.AmountPaisa));
    }

    public Task<FinanceExpense?> GetExpenseAsync(long expenseId) => _expenses.GetAsync(expenseId);

    public async Task<ExpenseAmounts> PreviewAmountsAsync(long amountPaisa, decimal gstPercent, bool amountIncludesGst, string? payeeGstin) =>
        FinanceRules.ComputeExpense(Math.Max(0, amountPaisa), gstPercent, amountIncludesGst, await IsInterStateAsync(payeeGstin));

    public async Task<FinanceExpense> SaveAsync(ExpenseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = FinanceRules.ValidateExpense(request, _today()).ToList();
        FinanceExpense? existing = null;
        if (request.Id > 0)
        {
            existing = await _expenses.GetAsync(request.Id);
            if (existing is null) errors.Add("The expense was not found. It may have been removed.");
            else if (existing.IsCancelled) errors.Add("A cancelled expense cannot be edited.");
        }
        if (errors.Count == 0)
        {
            var account = (await _accounts.ListAsync(_today(), includeInactive: true)).FirstOrDefault(a => a.Id == request.AccountId);
            if (account is null) errors.Add("The chosen account was not found. Reload and choose again.");
            else if (!account.IsActive) errors.Add("The chosen account is inactive. Reactivate it or choose another.");
            else
            {
                var amounts = FinanceRules.ComputeExpense(request.AmountPaisa, request.GstRatePercent, request.AmountIncludesGst, false);
                // Editing gives the old amount back to the account it came from before checking what is available.
                var refund = existing is { AccountId: { } oldAccount } && oldAccount == account.Id ? existing.AmountPaisa : 0;
                var available = new FinanceAccount
                {
                    Id = account.Id, Name = account.Name, AccountType = account.AccountType, BalancePaisa = account.BalancePaisa + refund
                };
                errors.AddRange(FinanceRules.ValidateCashAvailable(available, amounts.TotalPaisa));
            }
        }
        if (errors.Count > 0) throw new FinanceValidationException(errors);

        var isInterState = request.GstRatePercent > 0 && await IsInterStateAsync(request.PayeeGstin);
        var split = FinanceRules.ComputeExpense(request.AmountPaisa, request.GstRatePercent, request.AmountIncludesGst, isInterState);
        var category = (await _expenses.ListCategoriesAsync(true)).FirstOrDefault(c => c.Id == request.CategoryId);
        var expense = new FinanceExpense
        {
            Id = request.Id,
            ExpenseDate = request.ExpenseDate.Date,
            CategoryId = request.CategoryId,
            CategoryName = category?.Name ?? string.Empty,
            Description = request.Description?.Trim(),
            PayeeName = request.PayeeName?.Trim(),
            PayeeGstin = request.PayeeGstin?.Trim().ToUpperInvariant(),
            InvoiceNumber = request.InvoiceNumber?.Trim(),
            GstRatePercent = request.GstRatePercent,
            IsInterState = isInterState,
            TaxablePaisa = split.TaxablePaisa,
            CgstPaisa = split.CgstPaisa,
            SgstPaisa = split.SgstPaisa,
            IgstPaisa = split.IgstPaisa,
            AmountPaisa = split.TotalPaisa,
            AccountId = request.AccountId,
            Mode = request.Mode,
            Reference = request.Reference?.Trim(),
            Notes = request.Notes?.Trim(),
            CreatedByUserId = request.UserId
        };

        if (existing is not null)
        {
            await _expenses.UpdateAsync(expense);
            await FinanceAudit.LogAsync(_audit, "FinanceExpenseUpdated", "Expense", expense.Id, Snapshot(existing), Snapshot(expense));
            return await _expenses.GetAsync(expense.Id) ?? expense;
        }

        var saved = await _expenses.InsertAsync(expense);
        await FinanceAudit.LogAsync(_audit, "FinanceExpenseRecorded", "Expense", saved.Id, null, Snapshot(saved));
        return saved;
    }

    public async Task CancelAsync(long expenseId, string reason, long? userId)
    {
        var errors = FinanceRules.ValidateReason(reason, "expense");
        if (errors.Count > 0) throw new FinanceValidationException(errors);
        var expense = await _expenses.GetAsync(expenseId) ?? throw new FinanceValidationException("The expense was not found.");
        await _expenses.CancelAsync(expenseId, reason.Trim());
        await FinanceAudit.LogAsync(_audit, "FinanceExpenseCancelled", "Expense", expenseId, Snapshot(expense), null, reason.Trim());
    }

    public async Task<IReadOnlyList<ExpenseCategoryTotal>> GetCategoryTotalsAsync(DateTime from, DateTime to)
    {
        CashBankService.ValidateRange(from, to);
        return await _expenses.GetCategoryTotalsAsync(from.Date, to.Date);
    }

    public Task<string> PeekNumberAsync(DateTime date) => _numbers.PeekNextAsync(DocumentType.Expense, date);

    /// <summary>A payee GSTIN from another state than the shop's makes the expense inter-state (IGST).</summary>
    private async Task<bool> IsInterStateAsync(string? payeeGstin)
    {
        if (string.IsNullOrWhiteSpace(payeeGstin)) return false;
        var shop = await _settings.GetValueAsync(ShopGstinKey);
        if (string.IsNullOrWhiteSpace(shop)) shop = await _settings.GetValueAsync(ShopStateCodeKey);
        return PricingCalculator.IsInterStateSupply(shop, payeeGstin.Trim().ToUpperInvariant());
    }

    private static object Snapshot(FinanceExpense e) => new
    {
        e.ExpenseNumber, Date = e.ExpenseDate.ToString("yyyy-MM-dd"), e.CategoryId, e.CategoryName, e.PayeeName, e.PayeeGstin, e.InvoiceNumber,
        e.GstRatePercent, e.TaxablePaisa, e.CgstPaisa, e.SgstPaisa, e.IgstPaisa, e.AmountPaisa, e.AccountId, e.Mode, e.Description
    };
}
