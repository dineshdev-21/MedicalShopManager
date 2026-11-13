using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App.Navigation;

/// <summary>
/// Shown in place of a page whose ViewModel could not be created or loaded (e.g. a missing DI registration),
/// so the user sees what failed instead of the previous page staying on screen. Rendered by <see cref="ViewLocator"/>.
/// </summary>
public sealed class PageErrorViewModel : BaseViewModel
{
    public PageErrorViewModel(string pageTitle, Exception error)
    {
        PageTitle = pageTitle;
        Message = $"'{pageTitle}' could not be opened: {error.GetBaseException().Message}\n\nDetails were written to the log.";
        SetError(Message);
    }

    public string PageTitle { get; }
    public string Message { get; }
}
