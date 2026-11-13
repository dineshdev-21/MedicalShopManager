using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MedicalShopManager.App.ViewModels;

/// <summary>
/// Base ViewModel providing common functionality for all page ViewModels.
/// </summary>
public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    protected void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
        StatusMessage = null;
    }

    protected void ClearError()
    {
        HasError = false;
        ErrorMessage = null;
    }

    /// <summary>Clears both the status and error banners, e.g. before starting a new operation.</summary>
    protected void ClearStatus()
    {
        StatusMessage = null;
        ClearError();
    }

    protected void SetStatus(string message)
    {
        StatusMessage = message;
        ClearError();
    }

    /// <summary>
    /// Called when the page becomes active.
    /// </summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
