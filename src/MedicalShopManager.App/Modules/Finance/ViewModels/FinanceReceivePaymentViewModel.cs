using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>Finance ▸ Receive Payment (Payment In, Localwell payment-in): money received from a customer against credit bills.</summary>
public sealed class FinanceReceivePaymentViewModel : FinancePartyPaymentViewModel
{
    public FinanceReceivePaymentViewModel(IPartyPaymentService service, ICashBankService cashBank, IAuthService auth, IPrintService print,
        IDocumentTemplate<FinanceVoucher> template, INavigationService navigation, ILogger<FinanceReceivePaymentViewModel> logger)
        : base(service, cashBank, auth, print, template, navigation, logger)
    {
    }

    public override FinanceVoucherKind Kind => FinanceVoucherKind.Receipt;
}
