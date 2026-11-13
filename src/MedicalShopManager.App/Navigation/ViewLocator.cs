using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;

namespace MedicalShopManager.App.Navigation;

/// <summary>
/// Convention-based view resolution: FooViewModel → FooPage (preferred) or FooView, any class deriving from
/// FrameworkElement in a namespace ending in ".Views" of the App assembly (App.Views or App.Modules.&lt;Name&gt;.Views).
/// Registered as the App resource "ViewLocator"; use it as ContentTemplateSelector wherever a ViewModel is shown.
/// Never throws while rendering: a missing or ambiguous view, or a page that failed to open
/// (<see cref="PageErrorViewModel"/>), is shown as an error text in place of the page.
/// </summary>
public sealed class ViewLocator : DataTemplateSelector
{
    private static readonly ConcurrentDictionary<Type, DataTemplate> Templates = new();
    private static readonly Lazy<Type[]> ViewTypes = new(() => typeof(ViewLocator).Assembly.GetTypes()
        .Where(t => typeof(FrameworkElement).IsAssignableFrom(t) && !typeof(Window).IsAssignableFrom(t) && !t.IsAbstract
                    && t.Namespace != null && t.Namespace.EndsWith(".Views", StringComparison.Ordinal)
                    && t.GetConstructor(Type.EmptyTypes) != null)
        .ToArray());

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item == null ? null : Templates.GetOrAdd(item.GetType(), BuildTemplate);

    /// <summary>The view type for a ViewModel type, or null when none follows the naming convention.</summary>
    public static Type? ResolveViewType(Type viewModelType)
    {
        const string suffix = "ViewModel";
        if (!viewModelType.Name.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var baseName = viewModelType.Name[..^suffix.Length];

        foreach (var candidate in new[] { baseName + "Page", baseName + "View" })
        {
            var matches = ViewTypes.Value.Where(t => t.Name == candidate).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"View name '{candidate}' is ambiguous: {string.Join(", ", matches.Select(m => m.FullName))}. View class names must be unique.");
            if (matches.Count == 1) return matches[0];
        }
        return null;
    }

    private static DataTemplate BuildTemplate(Type viewModelType)
    {
        FrameworkElementFactory factory;
        if (viewModelType == typeof(PageErrorViewModel))
        {
            factory = MessageFactory(new System.Windows.Data.Binding(nameof(PageErrorViewModel.Message)));
        }
        else
        {
            Type? viewType;
            string? problem = null;
            try
            {
                viewType = ResolveViewType(viewModelType);
            }
            catch (InvalidOperationException ex)
            {
                viewType = null;
                problem = ex.Message;
            }

            factory = viewType != null
                ? new FrameworkElementFactory(viewType)
                : MessageFactory(problem ??
                    $"No view found for {viewModelType.Name}. Name the view FooPage for FooViewModel and put it in a .Views namespace.");
        }

        var template = new DataTemplate(viewModelType) { VisualTree = factory };
        template.Seal();
        return template;
    }

    /// <summary>A wrapped, selectable error text in place of a page (missing / ambiguous view, page failed to open).</summary>
    private static FrameworkElementFactory MessageFactory(object text)
    {
        var factory = new FrameworkElementFactory(typeof(TextBox));
        if (text is System.Windows.Data.BindingBase binding)
            factory.SetBinding(TextBox.TextProperty, binding);
        else
            factory.SetValue(TextBox.TextProperty, text);
        factory.SetValue(TextBox.IsReadOnlyProperty, true);
        factory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        factory.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        factory.SetValue(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        factory.SetValue(Control.ForegroundProperty, System.Windows.Media.Brushes.Firebrick);
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(16));
        return factory;
    }
}
