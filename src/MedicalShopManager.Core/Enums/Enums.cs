namespace MedicalShopManager.Core.Enums;

public enum DosageForm
{
    Tablet,
    Capsule,
    Syrup,
    Injection,
    Cream,
    Ointment,
    Drops,
    Inhaler,
    Powder,
    Gel,
    Lotion,
    Suppository,
    Patch,
    Spray,
    Suspension,
    Solution,
    Other
}

/// <summary>
/// Drugs &amp; Cosmetics Rules schedule of a medicine (stored as INTEGER in Medicines.Schedule).
/// Values 0–5 keep the integers of the former PrescriptionClassification enum, so no data migration is needed.
/// Use <see cref="DrugScheduleExtensions"/> for the dispensing rules.
/// </summary>
public enum DrugSchedule
{
    /// <summary>Over the counter: no prescription needed (Localwell shows "-").</summary>
    None = 0,
    /// <summary>Schedule H: prescription drug (Rx).</summary>
    H = 1,
    /// <summary>Schedule H1: Rx + mandatory H1 register (patient, doctor, qty), kept 3 years.</summary>
    H1 = 2,
    /// <summary>Schedule X: Rx retained by the pharmacy + separate register.</summary>
    X = 3,
    /// <summary>NRx: narcotic / psychotropic (NDPS) drug, prescription mandatory. Formerly "Narcotic".</summary>
    NRx = 4,
    /// <summary>Legacy "Other" value kept for existing rows; treat as prescription-only.</summary>
    Other = 5,
    /// <summary>Prescription-only but not H/H1/X/NRx (e.g. Schedule G).</summary>
    Rx = 6
}

public enum MedicineStatus
{
    Active,
    Inactive,
    Discontinued
}

public enum PaymentMethod
{
    Cash,
    UPI,
    Card,
    BankTransfer,
    Credit,
    Split,
    Other
}

public enum PaymentStatus
{
    Paid,
    Partial,
    Unpaid,
    Refunded
}

public enum PaymentType
{
    SalePayment,
    PurchasePayment,
    SupplierPayment,
    CustomerRefund,
    ExpensePayment,
    Other
}

public enum MovementType
{
    PurchaseReceipt,
    SaleDispatch,
    SaleReturn,
    PurchaseReturn,
    StockAdjustment,
    Damaged,
    Expired,
    WriteOff,
    OpeningStock,
    Other
}

public enum ReturnType
{
    SalesReturn,
    PurchaseReturn,
    Damaged,
    Expired,
    WriteOff
}

public enum PurchaseStatus
{
    Draft,
    Confirmed,
    Cancelled
}

public enum ExpenseCategory
{
    Rent,
    Salary,
    Electricity,
    Delivery,
    Packaging,
    Transport,
    BankCharges,
    Maintenance,
    Insurance,
    License,
    Advertising,
    Stationery,
    Internet,
    Phone,
    Miscellaneous,
    Other
}

public enum UserRole
{
    Administrator,
    Pharmacist,
    BillingOperator,
    InventoryManager
}

public enum SyncStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,

    /// <summary>Workbook is open in another process (e.g. Excel); sync deferred. UI state only, never persisted on queue rows.</summary>
    Locked
}

public enum SyncEntityType
{
    Medicine,
    Batch,
    Sale,
    SaleItem,
    Purchase,
    PurchaseItem,
    Supplier,
    Customer,
    Payment,
    Expense,
    Return,
    StockMovement,
    ProfitReport,
    ShopSettings,
    FullRebuild
}
