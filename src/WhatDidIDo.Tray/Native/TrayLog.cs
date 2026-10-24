using System.Diagnostics;
using System.Globalization;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// <c>%LOCALAPPDATA%\whatdidido-windows\tray.log</c>: the tray's own errors,
/// one line each (callers log a message once). A tray app has no console or
/// journal, so this is where they go (TRAY.md 2.6). Never the token, a window
/// title or a microphone label; capped at 256 KiB (the previous file is kept
/// as <c>tray.log.1</c>).
/// </summary>
internal static class TrayLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly Lock Gate = new();

    private static string? FilePath
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local)
                ? null
                : Path.Combine(local, "whatdidido-windows", "tray.log");
        }
    }

    public static void Write(string message)
    {
        Debug.WriteLine($"whatdidido-tray: {message}");
        if (FilePath is not { } path)
            return;
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging never fails the tray.
            }
        }
    }
}
