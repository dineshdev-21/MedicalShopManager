using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>WPF helpers for the logo / signature pickers (file dialog and in-memory preview).</summary>
internal static class BrandingImageLoader
{
    /// <summary>Shows an Open dialog for PNG/JPEG and returns the bytes, or null when cancelled.</summary>
    public static byte[]? PickImageFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? File.ReadAllBytes(dialog.FileName) : null;
    }

    /// <summary>A frozen bitmap for an Image control, or null when the bytes cannot be decoded.</summary>
    public static ImageSource? ToImageSource(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // corrupt image: the page shows the "no image" placeholder instead of crashing
        }
    }
}
