namespace MedicalShopManager.Core.Enums;

/// <summary>
/// Every numbered document the app issues. Stored as INTEGER in DocumentSequences.DocumentType;
/// the explicit values are persisted, so never renumber them. Append new types at the end.
/// </summary>
public enum DocumentType
{
    Sale = 1,
    SaleReturn = 2,
    CreditNote = 3,
    Purchase = 4,
    PurchaseReturn = 5,
    DebitNote = 6,
    PurchaseOrder = 7,
    /// <summary>Payment In (money received from a customer).</summary>
    Receipt = 8,
    /// <summary>Payment Out (money paid to a supplier or other payee).</summary>
    Payment = 9,
    Expense = 10,
    StockAdjustment = 11,
    StockAudit = 12
}

/// <summary>
/// Kind of party a ledger row, payment or document points at. Ledgers store (PartyType, PartyId)
/// instead of one nullable foreign key per party table; see docs/ARCHITECTURE-MODULES.md §Parties.
/// Stored as INTEGER; never renumber.
/// </summary>
public enum PartyType
{
    Customer = 1,
    Supplier = 2,
    Doctor = 3,
    /// <summary>Payee that is not a master record (expense vendor, cash adjustment). PartyId is NULL.</summary>
    Other = 4
}

/// <summary>Paper sizes the print service lays out for.</summary>
public enum PaperSize
{
    A4,
    A5,
    Thermal80mm,
    Thermal58mm
}

/// <summary>How an invoice total is rounded (MASTER-SPEC §4.1). Default for bills is <see cref="NearestRupee"/>.</summary>
public enum RoundOffMode
{
    None,
    NearestRupee,
    NearestFiftyPaise
}

/// <summary>Whether a rate already contains GST (sale side: MRP-based rates) or GST is added on top (purchase side).</summary>
public enum TaxMode
{
    Inclusive,
    Exclusive
}
