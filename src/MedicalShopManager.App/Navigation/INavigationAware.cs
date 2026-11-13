namespace MedicalShopManager.App.Navigation;

/// <summary>
/// Optional for page ViewModels that accept a navigation parameter (e.g. a sale id to open).
/// The shell calls <see cref="OnNavigatedTo"/> before BaseViewModel.LoadAsync.
/// </summary>
public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);
}
