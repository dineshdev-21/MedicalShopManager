namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>Customer master kind (spec 02 §D "CUSTOMER IS"). Stored as INTEGER in Customers.CustomerType; never renumber.</summary>
public enum CustomerKind
{
    Individual = 1,
    Business = 2
}

/// <summary>Gender of a customer or patient. Stored as INTEGER; 0 = not given.</summary>
public enum PersonGender
{
    Unspecified = 0,
    Male = 1,
    Female = 2,
    Other = 3
}

/// <summary>Life cycle of a posted sale. Stored as INTEGER in Sales.Status; pre-6.2 rows default to Active.</summary>
public enum SaleStatus
{
    Active = 1,
    /// <summary>Deleted from the billing screen: stock restored, payments reversed, kept for audit and numbering.</summary>
    Cancelled = 2
}

/// <summary>
/// Tender used for one payment on a sale (spec 02 §M step 2). Mapped onto the shared <c>PaymentMethod</c> enum
/// when the payment row is written (Cheque is stored as BankTransfer with the cheque number as reference).
/// </summary>
public enum TenderMode
{
    Cash = 1,
    Upi = 2,
    Card = 3,
    BankTransfer = 4,
    Cheque = 5
}

/// <summary>How the bill discount was entered (spec 02 §I card 1: IN PERCENT or IN VALUE).</summary>
public enum BillDiscountKind
{
    None = 0,
    Percent = 1,
    Amount = 2
}

/// <summary>Order in which batches are offered and auto-picked (spec 02 §E "BATCH ORDER").</summary>
public enum BatchPickOrder
{
    /// <summary>First expiry, first out (default; Localwell "FIRST IN, FIRST OUT" by expiry).</summary>
    Fefo = 0,
    /// <summary>Latest received first.</summary>
    Lifo = 1
}

/// <summary>Expiry state of a batch relative to the bill date (spec 02 "Near-expiry / expired").</summary>
public enum BatchExpiryState
{
    Ok = 0,
    NearExpiry = 1,
    Expired = 2
}
