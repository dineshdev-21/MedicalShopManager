using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.Billing.Printing;

/// <summary>
/// What the sale invoice template prints: the saved sale plus Control Center's invoice print toggles and business
/// profile (bank / UPI / FSSAI), loaded by the caller because templates render synchronously.
/// </summary>
public sealed record SaleInvoicePrintModel(
    BillingInvoice Invoice,
    InvoicePrintSettings Settings,
    BusinessProfile? Business = null,
    string? PrintedBy = null);
