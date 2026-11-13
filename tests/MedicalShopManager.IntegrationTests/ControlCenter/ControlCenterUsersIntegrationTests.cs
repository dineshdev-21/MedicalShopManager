using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.IntegrationTests.ControlCenter;

/// <summary>User accounts and the per-user permission matrix against SQLite.</summary>
public class ControlCenterUsersIntegrationTests : ControlCenterDbTestBase
{
    [Fact]
    public async Task CreateUser_HashesThePassword_AuditsWithoutIt_AndRejectsDuplicates()
    {
        var user = await UserAdmin.CreateUserAsync(new NewUserRequest(" ravi ", "Ravi Kumar", "s3cret!", UserRole.Pharmacist));

        user.Username.Should().Be("ravi");
        (await Auth.AuthenticateAsync("ravi", "s3cret!")).Should().NotBeNull();
        var audit = (await AuditAsync("ControlCenter.UserCreated")).Should().ContainSingle().Subject;
        audit.NewValues.Should().Contain("Pharmacist").And.NotContain("s3cret!");

        await FluentActions.Awaiting(() => UserAdmin.CreateUserAsync(new NewUserRequest("RAVI", "Other", "another1", UserRole.BillingOperator)))
            .Should().ThrowAsync<ControlCenterValidationException>().WithMessage("*already taken*");
        await FluentActions.Awaiting(() => UserAdmin.CreateUserAsync(new NewUserRequest("x", "", "1", UserRole.Pharmacist)))
            .Should().ThrowAsync<ControlCenterValidationException>();
    }

    [Fact]
    public async Task UpdateUser_CannotRemoveTheLastAdministrator()
    {
        var admin = await AdminAsync();

        await FluentActions.Awaiting(() => UserAdmin.UpdateUserAsync(new UserUpdateRequest(admin.Id, "Admin", UserRole.Pharmacist, true)))
            .Should().ThrowAsync<ControlCenterValidationException>().WithMessage("*At least one active administrator*");
        (await Users.GetByIdAsync(admin.Id))!.Role.Should().Be(UserRole.Administrator);

        var second = await UserAdmin.CreateUserAsync(new NewUserRequest("owner", "Owner", "owner123", UserRole.Administrator));
        await UserAdmin.UpdateUserAsync(new UserUpdateRequest(admin.Id, "Old Admin", UserRole.Pharmacist, false));

        var updated = (await Users.GetByIdAsync(admin.Id))!;
        (updated.DisplayName, updated.Role, updated.IsActive).Should().Be(("Old Admin", UserRole.Pharmacist, false));
        (await Users.GetByIdAsync(second.Id))!.Role.Should().Be(UserRole.Administrator);
        (await AuditAsync("ControlCenter.UserUpdated")).Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateUser_SignedInUserCannotDeactivateThemselves()
    {
        await UserAdmin.CreateUserAsync(new NewUserRequest("owner", "Owner", "owner123", UserRole.Administrator));
        var me = (await Auth.AuthenticateAsync("owner", "owner123"))!;

        await FluentActions.Awaiting(() => UserAdmin.UpdateUserAsync(new UserUpdateRequest(me.Id, "Owner", UserRole.Administrator, false)))
            .Should().ThrowAsync<ControlCenterValidationException>().WithMessage("*your own account*");
    }

    [Fact]
    public async Task ResetPassword_ReplacesTheHash()
    {
        var user = await UserAdmin.CreateUserAsync(new NewUserRequest("asha", "Asha", "first123", UserRole.BillingOperator));
        await UserAdmin.ResetPasswordAsync(user.Id, "second456");

        (await Auth.AuthenticateAsync("asha", "first123")).Should().BeNull();
        (await Auth.AuthenticateAsync("asha", "second456")).Should().NotBeNull();
        await FluentActions.Awaiting(() => UserAdmin.ResetPasswordAsync(user.Id, "123"))
            .Should().ThrowAsync<ControlCenterValidationException>();
        (await AuditAsync("ControlCenter.PasswordReset")).Should().ContainSingle()
            .Which.NewValues.Should().BeNull("the password itself is never audited");
    }

    [Fact]
    public async Task Permissions_StoreOnlyDifferences_AndApplyThem()
    {
        var user = await UserAdmin.CreateUserAsync(new NewUserRequest("billing", "Billing Desk", "bill1234", UserRole.BillingOperator));
        (await Permissions.IsPermittedAsync(user.Id, "sale_delete")).Should().BeFalse();

        var desired = (await Permissions.GetEffectiveAsync(user.Id)).ToDictionary(p => p.Key, p => p.Value);
        desired["sale_delete"] = true;
        desired["customer_add"] = false;
        await Permissions.SavePermissionsAsync(user.Id, desired);

        (await PermissionRepository.GetOverridesAsync(user.Id)).Should()
            .BeEquivalentTo(new Dictionary<string, bool> { ["sale_delete"] = true, ["customer_add"] = false });
        (await Permissions.IsPermittedAsync(user.Id, "sale_delete")).Should().BeTrue();
        (await Permissions.IsPermittedAsync(user.Id, "customer_add")).Should().BeFalse();
        (await AuditAsync("ControlCenter.PermissionsUpdated")).Should().ContainSingle();

        await Permissions.ResetToRoleDefaultsAsync(user.Id);
        (await PermissionRepository.GetOverridesAsync(user.Id)).Should().BeEmpty();
        (await Permissions.IsPermittedAsync(user.Id, "sale_delete")).Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_AdministratorCannotBeRestricted_UnknownKeysRejected()
    {
        var admin = await AdminAsync();
        await FluentActions.Awaiting(() => Permissions.SavePermissionsAsync(admin.Id, new Dictionary<string, bool> { ["sale_add"] = false }))
            .Should().ThrowAsync<ControlCenterValidationException>();
        (await Permissions.IsPermittedAsync(admin.Id, PermissionCatalog.ControlCenterView)).Should().BeTrue();

        var user = await UserAdmin.CreateUserAsync(new NewUserRequest("inv", "Inventory", "inv12345", UserRole.InventoryManager));
        await FluentActions.Awaiting(() => Permissions.SavePermissionsAsync(user.Id, new Dictionary<string, bool> { ["fly"] = true }))
            .Should().ThrowAsync<ControlCenterValidationException>();
        await FluentActions.Awaiting(() => Permissions.IsPermittedAsync(user.Id, "fly")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Permissions_InactiveOrMissingUsersHaveNone_AndCurrentUserIsChecked()
    {
        (await Permissions.GetEffectiveAsync(9999)).Values.Should().OnlyContain(v => !v);
        (await Permissions.IsCurrentUserPermittedAsync("sale_add")).Should().BeFalse("nobody is signed in");

        await UserAdmin.CreateUserAsync(new NewUserRequest("pharma", "Pharmacist", "ph123456", UserRole.Pharmacist));
        await Auth.AuthenticateAsync("pharma", "ph123456");
        (await Permissions.IsCurrentUserPermittedAsync("sale_add")).Should().BeTrue();
        (await Permissions.IsCurrentUserPermittedAsync(PermissionCatalog.ControlCenterView)).Should().BeFalse();
    }

    [Fact]
    public async Task Repository_Replace_ForAnUnknownUser_FailsAndLeavesOtherRowsAlone()
    {
        var user = await UserAdmin.CreateUserAsync(new NewUserRequest("txuser", "Tx User", "tx123456", UserRole.Pharmacist));
        await PermissionRepository.ReplaceAsync(user.Id, new Dictionary<string, bool> { ["sale_delete"] = true });

        // A user id that does not exist violates the foreign key (PRAGMA foreign_keys=ON): the whole transaction rolls back.
        await FluentActions.Awaiting(() => PermissionRepository.ReplaceAsync(424242, new Dictionary<string, bool> { ["sale_add"] = true }))
            .Should().ThrowAsync<Exception>();
        (await ScalarAsync("SELECT COUNT(*) FROM ControlCenterUserPermissions WHERE UserId = 424242")).Should().Be(0L);
        (await PermissionRepository.GetOverridesAsync(user.Id)).Should().ContainKey("sale_delete");
    }
}
