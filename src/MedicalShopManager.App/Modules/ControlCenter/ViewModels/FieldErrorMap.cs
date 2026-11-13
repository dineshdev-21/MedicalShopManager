using System.ComponentModel;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Per-field validation messages for XAML: <c>{Binding Errors[Gstin]}</c> returns "" for a valid field, so a
/// missing key never causes a binding error. Filled from <see cref="ControlCenterValidationException.Errors"/>.
/// </summary>
public sealed class FieldErrorMap : INotifyPropertyChanged
{
    private Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string field] => _errors.TryGetValue(field, out var message) ? message : string.Empty;

    public bool HasErrors => _errors.Count > 0;

    public void Set(IEnumerable<SettingsError> errors)
    {
        _errors = errors.GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal);
        Raise();
    }

    public void Clear()
    {
        if (_errors.Count == 0) return;
        _errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Raise();
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasErrors)));
    }

    /// <summary>Banner text for a validation failure: one line per problem.</summary>
    public static string Summary(ControlCenterValidationException ex) =>
        ex.Errors.Count == 1
            ? ex.Errors[0].Message
            : "Please fix the following:" + Environment.NewLine + string.Join(Environment.NewLine, ex.Errors.Select(e => "• " + e.Message));
}
