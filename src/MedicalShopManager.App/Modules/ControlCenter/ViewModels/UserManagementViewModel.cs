using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Control Center ▸ Users &amp; Permissions (Localwell "Users &amp; Devices"): accounts, role, active flag, password
/// reset, the per-user permission matrix and the audit trail. Moved here from App/ViewModels (owned by Control Center).
/// Keys: Ctrl+N new user, Ctrl+S save the selected user, Esc closes a dialog.
/// </summary>
public partial class UserManagementViewModel : BaseViewModel
{
    private readonly IUserAdminService _users;
    private readonly IUserPermissionService _permissions;
    private readonly IAuditLogRepository _auditRepository;
    private readonly IAuthService _auth;

    public UserManagementViewModel(IUserAdminService users, IUserPermissionService permissions,
        IAuditLogRepository auditRepository, IAuthService auth)
    {
        _users = users;
        _permissions = permissions;
        _auditRepository = auditRepository;
        _auth = auth;
    }

    public ObservableCollection<User> Users { get; } = new();
    public ObservableCollection<AuditLog> AuditLogs { get; } = new();
    public ObservableCollection<PermissionGroupItem> PermissionGroups { get; } = new();
    public FieldErrorMap Errors { get; } = new();

    public IReadOnlyList<UserRole> AvailableRoles { get; } = Enum.GetValues<UserRole>();

    /// <summary>The app is offline and single-device: the device list is this computer.</summary>
    public string DeviceName => Environment.MachineName;
    public string SignedInAs => _auth.CurrentUser is { } u ? $"{u.DisplayName} ({u.Username}, {u.Role})" : "Not signed in";

    [ObservableProperty] private User? _selectedUser;

    // Selected-user editor
    [ObservableProperty] private string _editDisplayName = string.Empty;
    [ObservableProperty] private UserRole _editRole = UserRole.BillingOperator;
    [ObservableProperty] private bool _editIsActive = true;
    [ObservableProperty] private bool _isPermissionMatrixEditable;
    [ObservableProperty] private string _permissionNote = string.Empty;

    // Create-user dialog
    [ObservableProperty] private bool _isCreateUserModalOpen;
    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private string _newDisplayName = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private UserRole _newRole = UserRole.BillingOperator;

    // Reset-password dialog
    [ObservableProperty] private bool _isResetPasswordModalOpen;
    [ObservableProperty] private string _resetPassword = string.Empty;

    public bool HasSelection => SelectedUser != null;
    public bool HasAuditLogs => AuditLogs.Count > 0;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var selectedId = SelectedUser?.Id;
            Users.Clear();
            foreach (var u in await _users.GetUsersAsync())
                Users.Add(u);

            AuditLogs.Clear();
            foreach (var l in await _auditRepository.GetRecentAsync(100))
                AuditLogs.Add(l);

            SelectedUser = Users.FirstOrDefault(u => u.Id == selectedId) ?? Users.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SetError($"Failed to load users: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasAuditLogs));
        }
    }

    partial void OnSelectedUserChanged(User? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        Errors.Clear();
        if (value == null)
        {
            PermissionGroups.Clear();
            return;
        }
        EditDisplayName = value.DisplayName;
        EditRole = value.Role;
        EditIsActive = value.IsActive;
        _ = LoadPermissionsAsync(value);
    }

    private async Task LoadPermissionsAsync(User user)
    {
        try
        {
            var effective = await _permissions.GetEffectiveAsync(user.Id);
            var editable = !PermissionRules.IsFullAccess(user.Role) && user.IsActive;
            IsPermissionMatrixEditable = editable;
            PermissionNote = PermissionRules.IsFullAccess(user.Role)
                ? "Administrators always have every permission. Change the role to restrict this user."
                : user.IsActive
                    ? "Untick a group's master switch to hide the whole area. Changes apply at the user's next action."
                    : "Inactive users cannot sign in; activate the user to edit permissions.";
            PermissionGroups.Clear();
            foreach (var group in PermissionCatalog.Groups)
                PermissionGroups.Add(new PermissionGroupItem(group, effective, editable));
        }
        catch (Exception ex)
        {
            SetError($"Could not load permissions: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (SelectedUser is not { } user || IsLoading) return;
        IsLoading = true;
        ClearStatus();
        try
        {
            await _users.UpdateUserAsync(new UserUpdateRequest(user.Id, EditDisplayName, EditRole, EditIsActive));
            var roleUnchanged = user.Role == EditRole;
            if (roleUnchanged && IsPermissionMatrixEditable && EditIsActive)
            {
                var desired = PermissionGroups.SelectMany(g => g.Items).ToDictionary(i => i.Key, i => i.IsPermitted);
                await _permissions.SavePermissionsAsync(user.Id, desired);
            }
            else if (!roleUnchanged)
            {
                await _permissions.ResetToRoleDefaultsAsync(user.Id); // old per-user changes belonged to the old role
            }
            Errors.Clear();
            IsLoading = false;
            await LoadAsync();
            SetStatus(roleUnchanged
                ? $"User '{user.Username}' saved."
                : $"User '{user.Username}' saved. Permissions now follow the {EditRole} role; adjust them and save again if needed.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Failed to save user: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ResetPermissionsToRoleAsync()
    {
        if (SelectedUser is not { } user) return;
        ClearStatus();
        try
        {
            await _permissions.ResetToRoleDefaultsAsync(user.Id);
            await LoadPermissionsAsync(user);
            SetStatus($"Permissions of '{user.Username}' reset to the {user.Role} defaults.");
        }
        catch (Exception ex)
        {
            SetError($"Could not reset permissions: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenCreateUserModal()
    {
        NewUsername = string.Empty;
        NewDisplayName = string.Empty;
        NewPassword = string.Empty;
        NewRole = UserRole.BillingOperator;
        Errors.Clear();
        IsCreateUserModalOpen = true;
    }

    [RelayCommand]
    private void CloseModals()
    {
        IsCreateUserModalOpen = false;
        IsResetPasswordModalOpen = false;
        ResetPassword = string.Empty;
        NewPassword = string.Empty;
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ClearStatus();
        try
        {
            var user = await _users.CreateUserAsync(new NewUserRequest(NewUsername, NewDisplayName, NewPassword, NewRole));
            IsCreateUserModalOpen = false;
            NewPassword = string.Empty;
            Errors.Clear();
            IsLoading = false;
            await LoadAsync();
            SelectedUser = Users.FirstOrDefault(u => u.Id == user.Id);
            SetStatus($"User '{user.Username}' created with role {user.Role}. Ask them to sign in and change the password.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Failed to create user: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenResetPasswordModal()
    {
        if (SelectedUser == null) return;
        ResetPassword = string.Empty;
        Errors.Clear();
        IsResetPasswordModalOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmResetPasswordAsync()
    {
        if (SelectedUser is not { } user) return;
        ClearStatus();
        try
        {
            await _users.ResetPasswordAsync(user.Id, ResetPassword);
            IsResetPasswordModalOpen = false;
            ResetPassword = string.Empty;
            Errors.Clear();
            SetStatus($"Password of '{user.Username}' changed.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Failed to reset password: {ex.Message}");
        }
    }
}
