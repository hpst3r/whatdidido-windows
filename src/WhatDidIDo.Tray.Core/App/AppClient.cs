using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.App;

/// <summary>
/// A failed app-server request. <see cref="Status"/>/<see cref="Code"/>: the
/// HTTP status and the API's error code (the v1 envelope
/// <c>{"error": {"code": ...}}</c>, or a flat <c>{"error": "code"}</c>);
/// <see cref="Unreachable"/>: nothing answered (discovered again next time).
/// Client-side refusals carry status 0 and <c>no_app_server</c>, <c>busy</c>
/// (an Assign is in flight) or <c>invalid</c>.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(string message, int status = 0, string? code = null, bool unreachable = false) : base(message)
    {
        Status = status;
        Code = code;
        Unreachable = unreachable;
    }

    public AppException() : this("app server request failed") { }

    public AppException(string message, Exception innerException) : base(message, innerException) { }

    public int Status { get; }
    public string? Code { get; }
    public bool Unreachable { get; }
}

/// <summary>The body of <c>POST /api/v1/timesheet/assignments</c>: exactly these seven keys.</summary>
public sealed record AssignmentBody(string Start, string End, string Ticket, string Client, string Category, string Note, bool Billable);

/// <summary>
/// The app server (the read server that serves <c>/app/</c>), POPOVER.md
/// section 3, ported from the extension's lib/app_client.js. Requests are
/// plain loopback requests with no Authorization (the control token never
/// leaves the control client), Origin, Cookie or Sec-Fetch-*, so the server
/// treats them as the local principal. No redirects are followed anywhere
/// (Amendment 2.1). Its awaits resume on the caller's context.
/// </summary>
public sealed partial class AppClient : IDisposable
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateRe();

    private readonly ControlClient _control;
    private readonly Func<string> _setting;
    private readonly HttpClient _http;
    private (string ControlBase, string? Origin)? _discovered;
    private string? _lastOrigin;   // kept while the session is not running
    private bool _assigning;

    /// <param name="control">The control client, for its base_url.</param>
    /// <param name="setting">The current <c>appUrl</c> setting.</param>
    public AppClient(ControlClient control, Func<string> setting, HttpMessageHandler? handler = null)
    {
        _control = control;
        _setting = setting;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ControlClient.UserAgent);
    }

    public void Dispose() => _http.Dispose();

    public AppUrlSetting Setting => AppUrls.ParseSetting(_setting());

    /// <summary>The setting is set but not a local address (the flyout's notice).</summary>
    public bool InvalidSetting => Setting.Invalid;

    /// <summary>
    /// The app server origin, or null: the setting when set (null when
    /// invalid), else discovered from the control server's <c>GET /day</c>
    /// redirect. Discovery runs again when the control base_url changes or
    /// after an app request found nothing listening; while the session is not
    /// running the last discovered origin is used.
    /// </summary>
    public async Task<string?> BaseAsync(CancellationToken ct = default)
    {
        var setting = Setting;
        if (!setting.Auto)
            return setting.Origin;
        string controlBase;
        try
        {
            controlBase = (await _control.EndpointAsync(ct)).BaseUrl;
        }
        catch (ControlException)
        {
            return _lastOrigin;
        }
        if (_discovered is { } d && d.ControlBase == controlBase)
            return d.Origin;
        string? origin;
        try
        {
            origin = await DiscoverAsync(controlBase, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A control file left behind, or a failed probe: not running.
            return _lastOrigin;
        }
        _discovered = (controlBase, origin);
        if (origin is not null)
            _lastOrigin = origin;
        return origin;
    }

    // GET <control>/day without following redirects and without the control token.
    private async Task<string?> DiscoverAsync(string controlBase, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(controlBase + "/day"), HttpCompletionOption.ResponseHeadersRead, ct);
        var location = response.Headers.Location?.OriginalString;
        return AppUrls.AppBaseFromLocation(controlBase, (int)response.StatusCode, location);
    }

    /// <summary>
    /// The day for a local date (YYYY-MM-DD), or null when there is no app
    /// server or it runs without <c>--timesheet</c> (404).
    /// </summary>
    public async Task<TimesheetDay?> DayAsync(string date, CancellationToken ct = default)
    {
        if (!DateRe().IsMatch(date))
            throw new AppException("invalid date", code: "invalid");
        if (await BaseAsync(ct) is not { } origin)
            return null;
        JsonElement data;
        try
        {
            data = await SendAsync(origin, HttpMethod.Get, $"/api/v1/timesheet/day?date={date}", null, ct);
        }
        catch (AppException e) when (e.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
        return TimesheetDay.Parse(data);
    }

    /// <summary>
    /// <c>POST /api/v1/timesheet/assignments</c>. One at a time: a second call
    /// while one is in flight throws with code <c>busy</c>.
    /// </summary>
    public async Task AssignAsync(AssignmentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_assigning)
            throw new AppException("an assignment is already being saved", code: "busy");
        _assigning = true;
        try
        {
            var origin = await BaseAsync(ct) ?? throw new AppException("no app server", code: "no_app_server");
            var exact = new Dictionary<string, object>
            {
                ["start"] = body.Start,
                ["end"] = body.End,
                ["ticket"] = body.Ticket,
                ["client"] = body.Client,
                ["category"] = body.Category,
                ["note"] = body.Note,
                ["billable"] = body.Billable,
            };
            await SendAsync(origin, HttpMethod.Post, "/api/v1/timesheet/assignments", exact, ct);
        }
        finally
        {
            _assigning = false;
        }
    }

    /// <summary>
    /// <c>&lt;app&gt;/app/#/&lt;view&gt;?&lt;params&gt;</c> for the browser, or null without
    /// a known app server: the setting, else the last discovered origin.
    /// </summary>
    public string? Url(string view, IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        var setting = Setting;
        var origin = setting.Auto ? (_discovered is { } d ? d.Origin : _lastOrigin) : setting.Origin;
        return origin is null ? null : $"{origin}/app/{AppUrls.Route(view, parameters)}";
    }

    private async Task<JsonElement> SendAsync(string origin, HttpMethod method, string path, IEnumerable<KeyValuePair<string, object>>? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(origin + path));
        if (body is not null)
        {
            request.Content = new StringContent(JsonBody.Write(body), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.ConnectionReset })
        {
            _discovered = null;
            throw new AppException("app server not reachable", unreachable: true);
        }
        catch (HttpRequestException e)
        {
            throw new AppException($"request failed: {e.HttpRequestError}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException("request failed: timeout");
        }
        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            JsonElement? data = null;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                data = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                var code = ErrorCode(data);
                throw new AppException(code is null ? $"HTTP {status}" : $"HTTP {status} {code}", status, code);
            }
            if (data is not { ValueKind: JsonValueKind.Object } ok)
                throw new AppException("invalid JSON response", status, "invalid");
            return ok;
        }
    }

    private static string? ErrorCode(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("error", out var err))
            return null;
        if (err.ValueKind == JsonValueKind.String)
            return err.GetString();
        return err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
    }
}
