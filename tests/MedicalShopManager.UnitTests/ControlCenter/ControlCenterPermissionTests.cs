using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

/// <summary>Permission catalogue, role defaults, effective permissions and the user-account guards.</summary>
public class ControlCenterPermissionTests
{
    [Fact]
    public void Catalog_KeysAreUnique_AndEveryGroupHasAtMostOneMaster()
    {
        PermissionCatalog.All.Select(p => p.Key).Should().OnlyHaveUniqueItems();
        foreach (var group in PermissionCatalog.Groups)
        {
            group.Permissions.Count(p => p.IsMaster).Should().BeLessThanOrEqualTo(1, group.Key);
            group.Permissions.Should().OnlyContain(p => p.Group == group.Key);
        }
        PermissionCatalog.Find("sale_add").Should().NotBeNull();
        PermissionCatalog.Find(PermissionCatalog.ControlCenterView).Should().NotBeNull();
        PermissionCatalog.Find("nope").Should().BeNull();
    }

    [Fact]
    public void Administrator_HasEverythingButNoRestriction_AndIgnoresOverrides()
    {
        var overrides = new Dictionary<string, bool> { ["sale_delete"] = false, ["is_sale_discount_locked"] = true };
        var effective = PermissionRules.Effective(UserRole.Administrator, overrides);

        foreach (var p in PermissionCatalog.All)
            effective[p.Key].Should().Be(!p.IsRestriction, p.Key);
        PermissionRules.IsFullAccess(UserRole.Administrator).Should().BeTrue();
        PermissionRules.IsFullAccess(UserRole.Pharmacist).Should().BeFalse();
    }

    [Fact]
    public void BillingOperator_CanBillButNotDeleteOrSeeControlCenter_AndDiscountIsLocked()
    {
        var e = PermissionRules.Effective(UserRole.BillingOperator, null);
        e["sale_add"].Should().BeTrue();
        e["sale_delete"].Should().BeFalse();
        e["purchase_add"].Should().BeFalse();
        e[PermissionCatalog.ControlCenterView].Should().BeFalse();
        e["is_sale_discount_locked"].Should().BeTrue();
    }

    [Fact]
    public void Overrides_ChangeTheRoleDefault()
    {
        var e = PermissionRules.Effective(UserRole.Pharmacist, new Dictionary<string, bool>
        {
            ["sale_delete"] = true,
            ["sale_edit"] = false
        });
        e["sale_delete"].Should().BeTrue();
        e["sale_edit"].Should().BeFalse();
        e["sale_add"].Should().BeTrue();
    }

    [Fact]
    public void MasterSwitchOff_RevokesTheWholeGroup_ButKeepsRestrictions()
    {
        var e = PermissionRules.Effective(UserRole.BillingOperator, new Dictionary<string, bool>
        {
            ["sale_view"] = false,
            ["sale_delete"] = true
        });

        e["sale_view"].Should().BeFalse();
        e["sale_add"].Should().BeFalse("the master *_view switch gates the group");
        e["sale_delete"].Should().BeFalse();
        e["is_sale_discount_locked"].Should().BeTrue("a restriction is not a grant and keeps its value");
        e["stock_view"].Should().BeTrue("other groups are unaffected");
    }

    [Fact]
    public void Diff_StoresOnlyDifferencesFromTheRole_AndIgnoresUnknownKeys()
    {
        var desired = PermissionRules.Effective(UserRole.InventoryManager, null).ToDictionary(p => p.Key, p => p.Value);
        desired["purchase_delete"] = true;
        desired["stock_edit"] = false;
        desired["unknown_key"] = true;

        var diff = PermissionRules.Diff(UserRole.InventoryManager, desired);

        diff.Should().BeEquivalentTo(new Dictionary<string, bool> { ["purchase_delete"] = true, ["stock_edit"] = false });
        PermissionRules.Effective(UserRole.InventoryManager, diff).Should()
            .BeEquivalentTo(desired.Where(d => d.Key != "unknown_key").ToDictionary(d => d.Key, d => d.Value));
    }

    // ─── user accounts ───

    [Fact]
    public void NewUser_Validation()
    {
        UserAccountRules.ValidateNewUser(new NewUserRequest("ravi.k", "Ravi K", "secret1", UserRole.Pharmacist)).Should().BeEmpty();

        var errors = UserAccountRules.ValidateNewUser(new NewUserRequest("r k", " ", "abc", (UserRole)42));
        errors.Select(e => e.Field).Should().BeEquivalentTo(new[] { "Username", "DisplayName", "Password", "Role" });
    }

    [Theory]
    [InlineData("12345", false)]
    [InlineData("      ", false)]
    [InlineData("Ravi99", false)] // same as the username
    [InlineData("s3cret!", true)]
    public void Password_Rules(string password, bool ok) =>
        (UserAccountRules.PasswordError(password, "ravi99") == null).Should().Be(ok);

    private static User U(long id, UserRole role, bool active = true) =>
        new() { Id = id, Username = "u" + id, DisplayName = "User " + id, Role = role, IsActive = active };

    [Fact]
    public void UpdateGuard_KeepsOneActiveAdministrator()
    {
        var users = new List<User> { U(1, UserRole.Administrator), U(2, UserRole.Pharmacist), U(3, UserRole.Administrator, active: false) };

        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(1, "Admin", UserRole.Pharmacist, true), currentUserId: 2)
            .Should().Contain("At least one active administrator");
        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(1, "Admin", UserRole.Administrator, false), currentUserId: 2)
            .Should().Contain("At least one active administrator");

        users.Add(U(4, UserRole.Administrator));
        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(1, "Admin", UserRole.Pharmacist, true), currentUserId: 4).Should().BeNull();
    }

    [Fact]
    public void UpdateGuard_StopsSelfLockout()
    {
        var users = new List<User> { U(1, UserRole.Administrator), U(2, UserRole.Administrator), U(5, UserRole.Pharmacist) };

        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(1, "Me", UserRole.Administrator, false), currentUserId: 1)
            .Should().Be("You cannot deactivate your own account.");
        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(1, "Me", UserRole.Pharmacist, true), currentUserId: 1)
            .Should().Be("You cannot remove your own administrator role.");
        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(5, "Pharma", UserRole.BillingOperator, false), currentUserId: 1).Should().BeNull();
        UserAccountRules.UpdateGuard(users, new UserUpdateRequest(99, "Ghost", UserRole.Pharmacist, true), currentUserId: 1)
            .Should().Be("The user no longer exists.");
    }
}
