using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QNetwork;

public static class ProcessIconCache
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(
        StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetIcon(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            return null;

        if (Cache.TryGetValue(executablePath, out BitmapSource? cached))
            return cached;

        BitmapSource? icon = ExtractIcon(executablePath);
        Cache[executablePath] = icon;
        return icon;
    }

    private static BitmapSource? ExtractIcon(string path)
    {
        try
        {
            using System.Drawing.Icon? icon =
                System.Drawing.Icon.ExtractAssociatedIcon(path);

            if (icon is null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
        }
        catch
        {
            return null;
        }
    }

    public static void Clear() => Cache.Clear();
}
