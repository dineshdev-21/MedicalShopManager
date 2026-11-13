using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MedicalShopManager.App.Modules.ControlCenter.ViewModels;

namespace MedicalShopManager.App.Modules.ControlCenter.Views;

/// <summary>
/// PasswordBox.Password is not bindable (by design, so the text never sits in a binding), so the two password fields
/// push their value to the ViewModel here and are cleared whenever their dialog opens or closes.
/// </summary>
public partial class UserManagementPage : UserControl
{
    private UserManagementViewModel? _viewModel;

    public UserManagementPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as UserManagementViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UserManagementViewModel.IsCreateUserModalOpen):
                NewPasswordBox.Clear();
                break;
            case nameof(UserManagementViewModel.IsResetPasswordModalOpen):
                ResetPasswordBox.Clear();
                break;
        }
    }

    private void NewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.NewPassword = NewPasswordBox.Password;
    }

    private void ResetPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.ResetPassword = ResetPasswordBox.Password;
    }
}
