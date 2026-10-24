using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// The Now card's app icon. The session reports a window's app as its
/// executable's file name (<c>Code.exe</c>, whatdidido
/// <c>platforms/windows/foreground.py</c>), so the icon comes from a running
/// process of that name, if this user can read its path; else none, and the
/// card shows initials. Results, misses included, are kept for the run.
/// </summary>
internal sealed class AppIcons
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call on the UI thread; the process lookup and icon extraction run off it.</summary>
    public async Task<ImageSource?> GetAsync(string appId)
    {
        if (_cache.TryGetValue(appId, out var cached))
            return cached;
        var png = await Task.Run(() => IconPng(appId));
        ImageSource? image = null;
        if (png is not null)
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(png);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            image = bitmap;
        }
        _cache[appId] = image;
        return image;
    }

    private static byte[]? IconPng(string appId)
    {
        var name = Path.GetFileNameWithoutExtension(appId);
        if (name.Length == 0 || !string.Equals(Path.GetExtension(appId), ".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Another user's or an elevated process: its path is not ours to read.
                    continue;
                }
                if (path is null)
                    continue;
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is null)
                    continue;
                using var sized = new Icon(icon, 32, 32);
                using var bitmap = sized.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
            return null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
