using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>Finance ▸ Pay Supplier (Payment Out, Localwell payment-out): money paid to a distributor against purchase bills.</summary>
public sealed class FinancePaySupplierViewModel : FinancePartyPaymentViewModel
{
    public FinancePaySupplierViewModel(IPartyPaymentService service, ICashBankService cashBank, IAuthService auth, IPrintService print,
        IDocumentTemplate<FinanceVoucher> template, INavigationService navigation, ILogger<FinancePaySupplierViewModel> logger)
        : base(service, cashBank, auth, print, template, navigation, logger)
    {
    }

    public override FinanceVoucherKind Kind => FinanceVoucherKind.Payment;
}
