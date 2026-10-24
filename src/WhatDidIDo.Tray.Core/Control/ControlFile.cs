using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatDidIDo.Tray.Core.Control;

/// <summary>Where the session listens and the token it expects (TRAY.md section 2).</summary>
public sealed record ControlEndpoint(string BaseUrl, string Token)
{
    // The token stays out of logs and exceptions, including a record's ToString.
    public override string ToString() => $"ControlEndpoint {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>
/// control.json: <c>{"version": 1, "base_url": "http://127.0.0.1:&lt;port&gt;", "token": "..."}</c>.
/// Only an IPv4 loopback http origin is accepted, so the token can't be sent
/// anywhere else. Error messages never echo file contents (they hold the token).
/// </summary>
public static partial class ControlFile
{
    [GeneratedRegex(@"^http://127\.0\.0\.1:([0-9]{1,5})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex BaseRe();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRe();

    /// <summary>The session's control file: <c>%LOCALAPPDATA%\whatdidido\control.json</c>.</summary>
    public static string? DefaultPath()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local)
            ? null
            : Path.Combine(local, "whatdidido", "control.json");
    }

    /// <exception cref="ControlException">The text is not a valid control file.</exception>
    public static ControlEndpoint Parse(string text)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ControlException("control file is not valid JSON");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ControlException("control file is not an object");
        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v) || v != 1)
            throw new ControlException("control file has an unsupported version");

        var baseUrl = root.TryGetProperty("base_url", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : null;
        var m = baseUrl is null ? null : BaseRe().Match(baseUrl);
        // No leading zero: "http://127.0.0.1:080" is not the same spelling as the session's Host check.
        if (m is null || !m.Success || m.Groups[1].Value.StartsWith('0')
            || !int.TryParse(m.Groups[1].Value, out var port) || port is < 1 or > 65535)
            throw new ControlException("control file base_url is not a loopback http URL");

        var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : null;
        if (token is null || !TokenRe().IsMatch(token))
            throw new ControlException("control file token is malformed");

        return new ControlEndpoint(baseUrl!.TrimEnd('/'), token);
    }
}

/// <summary>Reads the control file's text: null when there is none (the session is not running).</summary>
public interface IControlFileSource
{
    /// <exception cref="ControlException">The file exists but can't be used (unreadable, wrong owner).</exception>
    Task<string?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A plain file read, with no owner check (tests, and platforms without one).</summary>
public sealed class PlainControlFileSource(string path) : IControlFileSource
{
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ControlException($"cannot read control file: {e.GetType().Name}");
        }
    }
}
