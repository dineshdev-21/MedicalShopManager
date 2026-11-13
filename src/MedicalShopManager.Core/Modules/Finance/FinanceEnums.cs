using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>Kind of money account (Localwell payment account types). Stored as INTEGER in FinanceAccounts.AccountType; never renumber.</summary>
public enum FinanceAccountType
{
    /// <summary>Cash in hand (the till). One is seeded by migration 16.</summary>
    Cash = 1,
    /// <summary>Savings / current bank account.</summary>
    Bank = 2,
    /// <summary>UPI wallet / QR (PhonePe, GPay, Paytm business).</summary>
    UpiWallet = 3,
    /// <summary>Card swipe machine (POS / EDC); settles into a bank account.</summary>
    CardPos = 4
}

/// <summary>Receipt (payment in from a customer) or payment voucher (payment out to a supplier). Stored as INTEGER; never renumber.</summary>
public enum FinanceVoucherKind
{
    Receipt = 1,
    Payment = 2
}

/// <summary>How a receipt / payment was made. Stored as INTEGER in FinanceVouchers.Mode; never renumber.</summary>
public enum FinancePaymentMode
{
    Cash = 1,
    Upi = 2,
    Card = 3,
    BankTransfer = 4,
    /// <summary>Cheque: the bill is settled now, the account moves when the cheque clears (bounce reverses it).</summary>
    Cheque = 5
}

/// <summary>Life cycle of a receipt / payment voucher. Stored as INTEGER; never renumber.</summary>
public enum FinanceVoucherStatus
{
    Active = 1,
    /// <summary>Cancelled by the user: allocations reversed, number kept.</summary>
    Cancelled = 2,
    /// <summary>Paid by a cheque that bounced: allocations reversed, the dues are open again.</summary>
    Bounced = 3
}

/// <summary>Cheque received from a customer or issued to a supplier. Stored as INTEGER; never renumber.</summary>
public enum ChequeDirection
{
    Received = 1,
    Issued = 2
}

/// <summary>Cheque status. Stored as INTEGER in FinanceCheques.Status; never renumber.</summary>
public enum ChequeStatus
{
    Pending = 1,
    Deposited = 2,
    Cleared = 3,
    Bounced = 4,
    Cancelled = 5
}

/// <summary>Account-to-account movement or a manual cash adjustment. Stored as INTEGER; never renumber.</summary>
public enum FinanceMovementKind
{
    /// <summary>Money moved between two of the shop's accounts (cash deposited in bank, UPI settlement, …).</summary>
    Transfer = 1,
    /// <summary>Money added to an account that is not a sale receipt (owner capital, other income, correction).</summary>
    AddMoney = 2,
    /// <summary>Money taken out of an account that is not an expense or supplier payment (owner drawings, correction).</summary>
    ReduceMoney = 3
}

/// <summary>What a receipt / payment amount was applied to. Stored as INTEGER; never renumber.</summary>
public enum AllocationTarget
{
    Sale = 1,
    Purchase = 2,
    /// <summary>The party's opening balance (dues brought forward from before the software).</summary>
    OpeningBalance = 3
}

/// <summary>Source of a line in an account statement.</summary>
public enum AccountEntrySource
{
    OpeningBalance,
    SalePayment,
    SupplierPayment,
    CustomerRefund,
    OtherPayment,
    Expense,
    Receipt,
    PaymentVoucher,
    Transfer,
    Adjustment
}

/// <summary>Source of a line in a party (customer / supplier) ledger.</summary>
public enum PartyEntrySource
{
    OpeningBalance,
    Sale,
    PaidWithBill,
    SaleReturn,
    Refund,
    Purchase,
    PurchaseReturn,
    RefundReceived,
    Receipt,
    PaymentVoucher,
    ChequeBounced
}

public static class FinanceLabels
{
    public static string ToLabel(this FinanceAccountType type) => type switch
    {
        FinanceAccountType.Cash => "Cash in hand",
        FinanceAccountType.Bank => "Bank account",
        FinanceAccountType.UpiWallet => "UPI wallet",
        FinanceAccountType.CardPos => "Card / POS",
        _ => type.ToString()
    };

    public static string ToLabel(this FinancePaymentMode mode) => mode switch
    {
        FinancePaymentMode.Cash => "Cash",
        FinancePaymentMode.Upi => "UPI",
        FinancePaymentMode.Card => "Card",
        FinancePaymentMode.BankTransfer => "Bank transfer / NEFT",
        FinancePaymentMode.Cheque => "Cheque",
        _ => mode.ToString()
    };

    public static string ToLabel(this ChequeStatus status) => status switch
    {
        ChequeStatus.Pending => "Pending",
        ChequeStatus.Deposited => "Deposited",
        ChequeStatus.Cleared => "Cleared",
        ChequeStatus.Bounced => "Bounced",
        ChequeStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string ToLabel(this FinanceVoucherKind kind) => kind == FinanceVoucherKind.Receipt ? "Receipt (Payment In)" : "Payment (Payment Out)";

    public static string ToLabel(this FinanceVoucherStatus status) => status switch
    {
        FinanceVoucherStatus.Active => "Active",
        FinanceVoucherStatus.Cancelled => "Cancelled",
        FinanceVoucherStatus.Bounced => "Cheque bounced",
        _ => status.ToString()
    };

    public static string ToLabel(this FinanceMovementKind kind) => kind switch
    {
        FinanceMovementKind.Transfer => "Transfer",
        FinanceMovementKind.AddMoney => "Add money",
        FinanceMovementKind.ReduceMoney => "Reduce money",
        _ => kind.ToString()
    };

    public static string ToLabel(this AccountEntrySource source) => source switch
    {
        AccountEntrySource.OpeningBalance => "Opening balance",
        AccountEntrySource.SalePayment => "Sale",
        AccountEntrySource.SupplierPayment => "Purchase payment",
        AccountEntrySource.CustomerRefund => "Refund",
        AccountEntrySource.OtherPayment => "Payment",
        AccountEntrySource.Expense => "Expense",
        AccountEntrySource.Receipt => "Payment In",
        AccountEntrySource.PaymentVoucher => "Payment Out",
        AccountEntrySource.Transfer => "Transfer",
        AccountEntrySource.Adjustment => "Adjustment",
        _ => source.ToString()
    };

    public static string ToLabel(this PartyEntrySource source) => source switch
    {
        PartyEntrySource.OpeningBalance => "Opening balance",
        PartyEntrySource.Sale => "Sale",
        PartyEntrySource.PaidWithBill => "Paid with bill",
        PartyEntrySource.SaleReturn => "Sale return",
        PartyEntrySource.Refund => "Refund",
        PartyEntrySource.Purchase => "Purchase",
        PartyEntrySource.PurchaseReturn => "Purchase return",
        PartyEntrySource.RefundReceived => "Refund received",
        PartyEntrySource.Receipt => "Payment In",
        PartyEntrySource.PaymentVoucher => "Payment Out",
        PartyEntrySource.ChequeBounced => "Cheque bounced",
        _ => source.ToString()
    };

    /// <summary>The legacy <see cref="PaymentMethod"/> written to Payments rows so other modules' readers show a sensible method.</summary>
    public static PaymentMethod ToPaymentMethod(this FinancePaymentMode mode) => mode switch
    {
        FinancePaymentMode.Cash => PaymentMethod.Cash,
        FinancePaymentMode.Upi => PaymentMethod.UPI,
        FinancePaymentMode.Card => PaymentMethod.Card,
        // Billing writes cheque tenders as BankTransfer too (BillingCalculator.ToPaymentMethod).
        FinancePaymentMode.BankTransfer or FinancePaymentMode.Cheque => PaymentMethod.BankTransfer,
        _ => PaymentMethod.Other
    };

    /// <summary>The legacy payment method that matches an account (expenses keep the old column filled).</summary>
    public static PaymentMethod ToPaymentMethod(this FinanceAccountType type) => type switch
    {
        FinanceAccountType.Cash => PaymentMethod.Cash,
        FinanceAccountType.UpiWallet => PaymentMethod.UPI,
        FinanceAccountType.CardPos => PaymentMethod.Card,
        _ => PaymentMethod.BankTransfer
    };

    /// <summary>Default mode for an account: cash account → cash, UPI wallet → UPI, POS → card, bank → bank transfer.</summary>
    public static FinancePaymentMode DefaultMode(this FinanceAccountType type) => type switch
    {
        FinanceAccountType.Cash => FinancePaymentMode.Cash,
        FinanceAccountType.UpiWallet => FinancePaymentMode.Upi,
        FinanceAccountType.CardPos => FinancePaymentMode.Card,
        _ => FinancePaymentMode.BankTransfer
    };
}
