using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.Modules.ControlCenter.Printing;

/// <summary>A registered print template as the Print Layout page lists it.</summary>
public sealed record PrintTemplateEntry(
    string Name,
    string DisplayName,
    IReadOnlyCollection<PaperSize> SupportedPaperSizes,
    PaperSize DefaultPaperSize,
    Type ModelType,
    object Template);

/// <summary>
/// Every <c>IDocumentTemplate&lt;T&gt;</c> registered by any module. ControlCenterModule runs last (App.xaml.cs order),
/// so it captures the full list of template service types at registration time; the instances are resolved lazily.
/// Templates are generic, so their members are read through the closed interface type.
/// </summary>
public sealed partial class PrintTemplateCatalog
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Type> _templateServiceTypes;

    public PrintTemplateCatalog(IServiceProvider services, IReadOnlyList<Type> templateServiceTypes)
    {
        _services = services;
        _templateServiceTypes = templateServiceTypes;
    }

    /// <summary>The closed IDocumentTemplate&lt;T&gt; service types in <paramref name="services"/>.</summary>
    public static IReadOnlyList<Type> FindTemplateServiceTypes(IServiceCollection services) =>
        services.Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDocumentTemplate<>))
            .Distinct()
            .ToList();

    public IReadOnlyList<PrintTemplateEntry> GetTemplates()
    {
        var entries = new List<PrintTemplateEntry>();
        foreach (var serviceType in _templateServiceTypes)
        {
            var name = serviceType.GetProperty(nameof(IDocumentTemplate<object>.Name))!;
            var sizes = serviceType.GetProperty(nameof(IDocumentTemplate<object>.SupportedPaperSizes))!;
            var defaultSize = serviceType.GetProperty(nameof(IDocumentTemplate<object>.DefaultPaperSize))!;
            foreach (var template in _services.GetServices(serviceType).OfType<object>())
            {
                var templateName = (string)name.GetValue(template)!;
                var entry = new PrintTemplateEntry(
                    templateName,
                    Humanize(templateName),
                    (IReadOnlyCollection<PaperSize>)sizes.GetValue(template)!,
                    (PaperSize)defaultSize.GetValue(template)!,
                    serviceType.GetGenericArguments()[0],
                    template);

                // Two templates may share a name (and so one paper-size key, e.g. the two sale invoice layouts):
                // list it once, preferring the one the Print Layout page can preview with a sample Sale.
                var existing = entries.FindIndex(e => e.Name == templateName);
                if (existing < 0) entries.Add(entry);
                else if (entries[existing].ModelType != typeof(Sale) && entry.ModelType == typeof(Sale)) entries[existing] = entry;
            }
        }
        return entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>"SaleInvoice" → "Sale Invoice".</summary>
    public static string Humanize(string name) => WordBoundary().Replace(name, " ");
}
