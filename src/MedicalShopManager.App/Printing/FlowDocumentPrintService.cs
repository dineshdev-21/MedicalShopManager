using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;

namespace MedicalShopManager.App.Printing;

/// <summary>
/// <see cref="IPrintService"/> over WPF FlowDocument + PrintDialog. Works with any Windows printer driver
/// (laser, inkjet, and thermal printers installed with a Windows driver).
/// </summary>
public sealed class FlowDocumentPrintService : IPrintService
{
    private readonly IAppSettingsRepository _settings;
    private readonly ILogger<FlowDocumentPrintService> _logger;

    public FlowDocumentPrintService(IAppSettingsRepository settings, ILogger<FlowDocumentPrintService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> PrintAsync<TModel>(IDocumentTemplate<TModel> template, TModel model, PrintOptions? options = null)
    {
        options ??= new PrintOptions();
        var document = await RenderAsync(template, model, options);

        var dialog = new PrintDialog();
        if (options.ShowPrintDialog && dialog.ShowDialog() != true)
            return false;

        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, template.Name);
        _logger.LogInformation("Printed {Template}", template.Name);
        return true;
    }

    public async Task PreviewAsync<TModel>(IDocumentTemplate<TModel> template, TModel model, PrintOptions? options = null)
    {
        options ??= new PrintOptions();
        var document = await RenderAsync(template, model, options);
        var window = new PrintPreviewWindow(document, $"Print Preview - {template.Name}",
            () => PrintAsync(template, model, options with { ShowPrintDialog = true }))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    public async Task<PaperSize> GetPaperSizeAsync<TModel>(IDocumentTemplate<TModel> template)
    {
        var configured = await _settings.GetValueAsync($"Print.PaperSize.{template.Name}");
        return Enum.TryParse<PaperSize>(configured, ignoreCase: true, out var size) && template.SupportedPaperSizes.Contains(size)
            ? size
            : template.DefaultPaperSize;
    }

    private async Task<FlowDocument> RenderAsync<TModel>(IDocumentTemplate<TModel> template, TModel model, PrintOptions options)
    {
        var paper = options.PaperSize ?? await GetPaperSizeAsync(template);
        if (!template.SupportedPaperSizes.Contains(paper))
            throw new InvalidOperationException($"{template.Name} cannot be printed on {paper}.");

        var context = new PrintContext(paper, await LoadShopProfileAsync(), options.CopyLabel);
        var document = template.Render(model, context);
        PaperLayout.Apply(document, paper);
        return document;
    }

    private async Task<ShopProfile> LoadShopProfileAsync() => new(
        await _settings.GetValueAsync("ShopName") ?? "My Medical Shop",
        await _settings.GetValueAsync("ShopAddress") ?? string.Empty,
        await _settings.GetValueAsync("ShopPhone") ?? string.Empty,
        await _settings.GetValueAsync("ShopGstin") ?? string.Empty,
        await _settings.GetValueAsync("LicenseNumber") ?? string.Empty,
        await _settings.GetValueAsync("PharmacistName") ?? string.Empty);
}
