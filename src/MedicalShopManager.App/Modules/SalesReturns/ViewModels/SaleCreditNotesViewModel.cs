using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>
/// Invoices ▸ Credit Notes (Localwell /invoices/credit-note): the sale returns screen opened on credit notes that still
/// carry open customer credit (any date); untick "Open credit only" to browse every credit note by date. F4 refunds
/// open credit (cash / UPI / card / bank), Ctrl+P prints the credit note.
/// </summary>
public sealed class SaleCreditNotesViewModel : SaleReturnsViewModel
{
    public SaleCreditNotesViewModel(ISaleReturnService service, IAuthService auth, IPrintService print,
        IDocumentTemplate<SaleReturnDocument> template, INavigationService navigation, ILogger<SaleReturnsViewModel> logger)
        : base(service, auth, print, template, navigation, logger)
    {
        OpenCreditOnly = true;
    }

    public override bool IsCreditNotesView => true;
}
