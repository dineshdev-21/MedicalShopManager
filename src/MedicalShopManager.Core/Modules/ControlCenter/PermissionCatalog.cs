using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// One permission. <see cref="IsMaster"/> is the group's <c>*_view</c> switch that gates the rest of the group;
/// <see cref="IsRestriction"/> marks a key whose <c>true</c> takes something away (e.g. <c>is_sale_discount_locked</c>),
/// so an administrator gets it as <c>false</c>.
/// </summary>
public sealed record PermissionDefinition(string Key, string Label, string Group, bool IsMaster = false, bool IsRestriction = false);

public sealed record PermissionGroupDefinition(string Key, string Label, IReadOnlyList<PermissionDefinition> Permissions)
{
    public PermissionDefinition? Master => Permissions.FirstOrDefault(p => p.IsMaster);
}

/// <summary>
/// The permission keys of Localwell's Users &amp; Devices screen (spec 01 §5.2, MASTER-SPEC "UserPermission"), grouped
/// sale, purchase, sale_return, purchase_return, stock, expense, cash_and_bank, reports, other. Keys are the web app's
/// so a later cloud sync can map them 1:1. <c>control_center_view</c> is added locally (assumption): it gates this
/// workspace for non-administrators.
/// </summary>
public static class PermissionCatalog
{
    public const string ControlCenterView = "control_center_view";

    public static IReadOnlyList<PermissionGroupDefinition> Groups { get; } = new[]
    {
        Group("sale", "Sales",
            ("sale_view", "View sales", true, false),
            ("sale_by_date_view", "View sales of past dates", false, false),
            ("sale_add", "Create sale", false, false),
            ("sale_edit", "Edit sale", false, false),
            ("sale_delete", "Delete sale", false, false),
            ("back_date_sale_add", "Create back-dated sale", false, false),
            ("sale_profit_view", "See sale profit", false, false),
            ("sale_margin_view", "See sale margin", false, false),
            ("is_sale_discount_locked", "Discount locked (cannot change discount)", false, true),
            ("override_product_discount_lock", "Override item discount lock", false, false),
            ("sale_discount_limit_ignore", "Ignore discount limits", false, false),
            ("customer_add", "Add customer", false, false),
            ("customer_edit", "Edit customer", false, false),
            ("customer_delete", "Delete customer", false, false)),
        Group("purchase", "Purchases",
            ("purchase_view", "View purchases", true, false),
            ("purchase_by_date_view", "View purchases of past dates", false, false),
            ("purchase_add", "Add purchase", false, false),
            ("purchase_edit", "Edit purchase", false, false),
            ("purchase_delete", "Delete purchase", false, false),
            ("purchase_margin_view", "See purchase margin", false, false)),
        Group("sale_return", "Sale returns",
            ("sale_return_view", "View sale returns", true, false),
            ("sale_return_add", "Add sale return", false, false),
            ("sale_return_edit", "Edit sale return", false, false),
            ("sale_return_delete", "Delete sale return", false, false)),
        Group("purchase_return", "Purchase returns",
            ("purchase_return_view", "View purchase returns", true, false),
            ("purchase_return_add", "Add purchase return", false, false),
            ("purchase_return_edit", "Edit purchase return", false, false),
            ("purchase_return_delete", "Delete purchase return", false, false)),
        Group("stock", "Stock",
            ("stock_view", "View stock", true, false),
            ("stock_add", "Add stock", false, false),
            ("stock_info_edit", "Edit stock info", false, false),
            ("merge_duplicate_stocks", "Merge duplicate items", false, false),
            ("stock_edit", "Adjust stock", false, false),
            ("stock_delete", "Delete stock", false, false),
            ("stock_margin_view", "See stock margin", false, false),
            ("item_add", "Add item", false, false),
            ("item_edit", "Edit item", false, false),
            ("stock_settings_view", "Stock settings", false, false),
            ("audit_view", "Stock audit", false, false)),
        Group("expense", "Expenses",
            ("expense_view", "View expenses", true, false),
            ("expenses_add", "Add expense", false, false),
            ("expenses_edit", "Edit expense", false, false),
            ("expenses_delete", "Delete expense", false, false)),
        Group("cash_and_bank", "Cash and bank",
            ("cash_and_bank_view", "View cash and bank", true, false),
            ("cash_and_bank_add", "Add entry", false, false),
            ("cash_and_bank_edit", "Edit entry", false, false),
            ("cash_and_bank_delete", "Delete entry", false, false)),
        Group("reports", "Reports",
            ("business_reports_view", "View reports", true, false),
            ("branch_reports_view", "Branch reports", false, false),
            ("sale_reports_view", "Sale reports", false, false),
            ("stock_reports_view", "Stock reports", false, false),
            ("purchase_reports_view", "Purchase reports", false, false),
            ("payment_reports_view", "Payment reports", false, false),
            ("gst_reports_view", "GST reports", false, false),
            ("party_reports_view", "Party reports", false, false),
            ("order_reports_view", "Order reports", false, false),
            ("profit_in_reports_view", "Profit in reports", false, false)),
        Group("other", "Other",
            ("dashboard_view", "Dashboard", false, false),
            ("customer_orders_view", "Customer orders", false, false),
            ("orderbook_view", "Orderbook", false, false),
            ("branch_transfer_view", "Branch transfer", false, false),
            ("sale_routes_view", "Sale routes", false, false),
            (ControlCenterView, "Control Center", false, false))
    };

    public static IReadOnlyList<PermissionDefinition> All { get; } = Groups.SelectMany(g => g.Permissions).ToArray();

    public static PermissionDefinition? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    private static PermissionGroupDefinition Group(string key, string label,
        params (string Key, string Label, bool IsMaster, bool IsRestriction)[] permissions) =>
        new(key, label, permissions.Select(p => new PermissionDefinition(p.Key, p.Label, key, p.IsMaster, p.IsRestriction)).ToArray());
}

/// <summary>Role defaults and the effective-permission calculation (pure).</summary>
public static class PermissionRules
{
    private static readonly HashSet<string> PharmacistKeys = new()
    {
        "sale_view", "sale_by_date_view", "sale_add", "sale_edit", "customer_add", "customer_edit",
        "sale_return_view", "sale_return_add",
        "purchase_view", "purchase_add", "purchase_return_view", "purchase_return_add",
        "stock_view", "stock_add", "stock_info_edit", "item_add", "item_edit",
        "sale_reports_view", "stock_reports_view", "business_reports_view",
        "dashboard_view", "customer_orders_view", "orderbook_view"
    };

    private static readonly HashSet<string> BillingOperatorKeys = new()
    {
        "sale_view", "sale_add", "customer_add", "sale_return_view", "sale_return_add",
        "stock_view", "dashboard_view", "customer_orders_view",
        "is_sale_discount_locked"
    };

    private static readonly HashSet<string> InventoryManagerKeys = new()
    {
        "purchase_view", "purchase_by_date_view", "purchase_add", "purchase_edit", "purchase_margin_view",
        "purchase_return_view", "purchase_return_add", "purchase_return_edit",
        "stock_view", "stock_add", "stock_info_edit", "merge_duplicate_stocks", "stock_edit", "stock_margin_view",
        "item_add", "item_edit", "stock_settings_view", "audit_view",
        "business_reports_view", "stock_reports_view", "purchase_reports_view",
        "dashboard_view", "orderbook_view"
    };

    /// <summary>Administrators always hold every permission and no restriction; their matrix cannot be edited.</summary>
    public static bool IsFullAccess(UserRole role) => role == UserRole.Administrator;

    /// <summary>What a role gets before any per-user change.</summary>
    public static bool RoleDefault(UserRole role, PermissionDefinition permission) => role switch
    {
        UserRole.Administrator => !permission.IsRestriction,
        UserRole.Pharmacist => PharmacistKeys.Contains(permission.Key),
        UserRole.BillingOperator => BillingOperatorKeys.Contains(permission.Key),
        UserRole.InventoryManager => InventoryManagerKeys.Contains(permission.Key),
        _ => false
    };

    /// <summary>
    /// Effective permissions: role default, overridden per user (ignored for administrators), then a group whose
    /// master <c>*_view</c> is off loses every other grant (restrictions keep their value).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Effective(UserRole role, IReadOnlyDictionary<string, bool>? overrides)
    {
        var result = new Dictionary<string, bool>();
        foreach (var group in PermissionCatalog.Groups)
        {
            foreach (var p in group.Permissions)
            {
                var value = RoleDefault(role, p);
                if (!IsFullAccess(role) && overrides != null && overrides.TryGetValue(p.Key, out var o)) value = o;
                result[p.Key] = value;
            }

            if (group.Master is { } master && !result[master.Key])
                foreach (var p in group.Permissions.Where(p => !p.IsMaster && !p.IsRestriction))
                    result[p.Key] = false;
        }
        return result;
    }

    /// <summary>The stored overrides: only the keys whose value differs from the role default.</summary>
    public static IReadOnlyDictionary<string, bool> Diff(UserRole role, IReadOnlyDictionary<string, bool> desired)
    {
        var diff = new Dictionary<string, bool>();
        foreach (var p in PermissionCatalog.All)
            if (desired.TryGetValue(p.Key, out var value) && value != RoleDefault(role, p))
                diff[p.Key] = value;
        return diff;
    }
}
