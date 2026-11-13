using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>An open bill (or the opening balance) of the party on the receive / pay screen, with the amount being paid now.</summary>
public sealed partial class FinanceDueRow : ObservableObject
{
    private readonly Action<FinanceDueRow> _onEdited;
    private bool _silent;

    public FinanceDueRow(DueDocument due, DateTime today, Action<FinanceDueRow> onEdited)
    {
        Due = due;
        AgeDays = due.AgeDays(today);
        IsOverdue = due.DueDate is { } dueDate ? dueDate.Date < today.Date : AgeDays > 90;
        _onEdited = onEdited;
    }

    public DueDocument Due { get; }
    public string Number => Due.Number;
    public string TypeLabel => Due.TargetLabel;
    public DateTime Date => Due.Date;
    public DateTime? DueDate => Due.DueDate;
    public long TotalPaisa => Due.TotalPaisa;
    public long PaidPaisa => Due.PaidPaisa;
    public long BalancePaisa => Due.BalancePaisa;
    public int AgeDays { get; }
    public bool IsOverdue { get; }

    /// <summary>Rupees applied to this document now (typed by the user or filled oldest first).</summary>
    [ObservableProperty] private decimal _payNow;

    public long PayNowPaisa => FinanceUi.Paisa(PayNow);
    public bool IsSelectedForPayment => PayNow > 0;

    partial void OnPayNowChanged(decimal value)
    {
        OnPropertyChanged(nameof(IsSelectedForPayment));
        OnPropertyChanged(nameof(PayNowPaisa));
        if (!_silent) _onEdited(this);
    }

    /// <summary>Sets the amount without telling the screen the user edited it (auto allocation).</summary>
    public void SetSilently(long paisa)
    {
        _silent = true;
        PayNow = FinanceUi.Rupees(paisa);
        _silent = false;
    }

    public VoucherAllocation ToAllocation() => new(Due.Target, Due.DocumentId, Due.Number, PayNowPaisa);
}
