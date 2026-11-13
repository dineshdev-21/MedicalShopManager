using System.Reflection;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>Discovers <see cref="IMigration"/> implementations by reflection and validates their versions.</summary>
public static class MigrationCatalog
{
    /// <summary>Highest version reserved for foundation migrations (Infrastructure/Database/Migrations).</summary>
    public const int MaxFoundationVersion = 9;

    /// <summary>
    /// Reserved migration number per module (folder name under Infrastructure/Modules). A module may use its number V
    /// and, if it needs more, the overflow block 100 + (V − 10) × 10 … +9. Enforced by MigrationCatalogTests.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ModuleVersions { get; } = new Dictionary<string, int>
    {
        ["Billing"] = 10,
        ["SalesReturns"] = 11,
        ["Purchases"] = 12,
        ["Stock"] = 13,
        ["AuditLabels"] = 14,
        ["Orderbook"] = 15,
        ["Finance"] = 16,
        ["DoctorsFollowups"] = 17,
        ["Reports"] = 18,
        ["ControlCenter"] = 19,
        ["DashboardSearch"] = 20
    };

    /// <summary>True when <paramref name="version"/> is the module's number or inside its overflow block.</summary>
    public static bool IsVersionAllowedFor(string module, int version)
    {
        if (!ModuleVersions.TryGetValue(module, out var reserved)) return false;
        var overflowStart = 100 + (reserved - 10) * 10;
        return version == reserved || (version >= overflowStart && version <= overflowStart + 9);
    }

    /// <summary>
    /// All migrations in <paramref name="assembly"/> (default: Infrastructure) ordered by version.
    /// Throws when two migrations claim the same version or a version is not positive, so a clash between
    /// module agents fails at startup and in tests rather than silently skipping a migration.
    /// </summary>
    public static IReadOnlyList<IMigration> Discover(Assembly? assembly = null)
    {
        assembly ??= typeof(MigrationCatalog).Assembly;

        var migrations = assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IMigration)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Version)
            .ToList();

        var invalid = migrations.Where(m => m.Version <= 0).Select(m => m.GetType().Name).ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException($"Migration versions must be positive: {string.Join(", ", invalid)}");

        var duplicates = migrations.GroupBy(m => m.Version).Where(g => g.Count() > 1)
            .Select(g => $"v{g.Key}: {string.Join(", ", g.Select(m => m.GetType().Name))}")
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate migration versions: {string.Join("; ", duplicates)}");

        return migrations;
    }
}
