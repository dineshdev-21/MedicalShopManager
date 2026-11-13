using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>One legacy payment method and the account its bill payments book into (saved as soon as it changes).</summary>
public sealed partial class FinanceMethodAccountRow : ObservableObject
{
    private readonly Func<FinanceMethodAccountRow, FinanceAccount?, Task> _onChanged;
    private bool _initialising = true;

    public FinanceMethodAccountRow(PaymentMethodAccount mapping, FinanceAccount? account, Func<FinanceMethodAccountRow, FinanceAccount?, Task> onChanged)
    {
        Method = mapping.Method;
        Label = mapping.MethodLabel;
        _onChanged = onChanged;
        _account = account;
        _initialising = false;
    }

    public PaymentMethod Method { get; }
    public string Label { get; }

    [ObservableProperty] private FinanceAccount? _account;

    partial void OnAccountChanged(FinanceAccount? oldValue, FinanceAccount? newValue)
    {
        if (_initialising || newValue is null || oldValue?.Id == newValue.Id) return;
        _ = _onChanged(this, oldValue);
    }

    /// <summary>Puts the previous account back without saving (after a failed save).</summary>
    public void Revert(FinanceAccount? account)
    {
        _initialising = true;
        Account = account;
        _initialising = false;
    }
}
