using System.Text;
using System.Text.RegularExpressions;

namespace WhatDidIDo.Tray.Core.App;

/// <summary>The <c>appUrl</c> setting, parsed: discover automatically, an override origin, or invalid.</summary>
public sealed record AppUrlSetting(bool Auto, string? Origin, bool Invalid);

/// <summary>
/// The app server's address rules and the app's hash routes, ported from the
/// extension's lib/day_model.js (POPOVER.md 3.1, 3.6 and Amendment 1).
/// </summary>
public static partial class AppUrls
{
    // http://127.0.0.1:<port> (not [::1]: the app server grants the local
    // principal only for Host 127.0.0.1), no userinfo, then an optional path,
    // query or fragment.
    [GeneratedRegex(@"^http://(127\.0\.0\.1):([0-9]{1,5})((?:[/?#].*)?)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LoopbackRe();

    private static (string Origin, string Tail)? Loopback(string? url)
    {
        var m = url is null ? null : LoopbackRe().Match(url);
        if (m is null || !m.Success || !int.TryParse(m.Groups[2].Value, out var port) || port is < 1 or > 65535)
            return null;
        return ($"http://{m.Groups[1].Value}:{port}", m.Groups[3].Value);
    }

    private static readonly int[] Redirects = [302, 303, 307];

    /// <summary>
    /// The app server origin from the control server's answer to <c>GET /day</c>:
    /// a 302/303/307 whose Location is an absolute loopback http URL gives that
    /// URL's origin; a relative Location starting with <c>/app/</c> gives the
    /// control server's origin; anything else gives null.
    /// </summary>
    public static string? AppBaseFromLocation(string controlBase, int status, string? location)
    {
        if (!Redirects.Contains(status) || location is null)
            return null;
        if (location.StartsWith("/app/", StringComparison.Ordinal))
            return Loopback(controlBase)?.Origin;
        return Loopback(location)?.Origin;
    }

    /// <summary>
    /// Empty or blank: automatic discovery. Otherwise only a loopback http
    /// origin with an explicit port and an optional trailing slash.
    /// </summary>
    public static AppUrlSetting ParseSetting(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0)
            return new AppUrlSetting(true, null, false);
        return Loopback(t) is { } lb && lb.Tail is "" or "/"
            ? new AppUrlSetting(false, lb.Origin, false)
            : new AppUrlSetting(false, null, true);
    }

    /// <summary>The app's hash route (formatHash): <c>#/timeline?date=2026-10-15&amp;t=10:45</c>; empty values are left out.</summary>
    public static string Route(string view, IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        var query = (parameters ?? []).Where(p => p.Value.Length > 0)
            .Select(p => $"{FormEncode(p.Key)}={FormEncode(p.Value)}").ToList();
        return query.Count == 0 ? $"#/{view}" : $"#/{view}?{string.Join('&', query)}";
    }

    // application/x-www-form-urlencoded like URLSearchParams: ASCII
    // alphanumerics and *-._ stay, space is '+', everything else %XX of UTF-8.
    private static string FormEncode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '*' or '-' or '.' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('+');
            else
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
