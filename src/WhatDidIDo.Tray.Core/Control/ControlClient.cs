using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Control;

/// <summary>
/// The work session's control API (whatdidido docs/CONTROL_API.md), as the
/// extension's lib/client.js: the control file is read on every request (the
/// session writes a new token on each start and deletes the file on a clean
/// shutdown), and once more after a 403.
/// </summary>
public sealed class ControlClient : IDisposable
{
    public const string UserAgent = "whatdidido-windows/0.1";

    private readonly IControlFileSource _source;
    private readonly HttpClient _http;

    public ControlClient(IControlFileSource source, HttpMessageHandler? handler = null)
    {
        _source = source;
        // No proxy, no cookie container, no redirects: the API refuses Cookie,
        // Origin and Sec-Fetch-*, and the token must never leave 127.0.0.1.
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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public void Dispose() => _http.Dispose();

    public async Task<ControlEndpoint> EndpointAsync(CancellationToken ct = default)
    {
        var text = await _source.ReadAsync(ct).ConfigureAwait(false)
            ?? throw new ControlException("control file not found", notRunning: true);
        return ControlFile.Parse(text);
    }

    public async Task<SessionStatus> StatusAsync(CancellationToken ct = default) =>
        SessionStatus.Parse(await RequestAsync(HttpMethod.Get, "/control/v1/status", null, ct).ConfigureAwait(false));

    public async Task<MicInventory> MicrophonesAsync(CancellationToken ct = default) =>
        MicInventory.Parse(await RequestAsync(HttpMethod.Get, "/control/v1/microphones", null, ct).ConfigureAwait(false));

    /// <summary>
    /// Turning on resolves the chosen microphone against a fresh inventory
    /// (its serial may have changed since the flyout was built); if the
    /// inventory changed in between, it is read again and the request retried
    /// once. It never picks another microphone: no choice, or a choice not in
    /// the inventory, throws and sends nothing.
    /// </summary>
    public async Task SetMicOnAsync(bool on, PreferredMic preferred, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        if (!on)
        {
            await RequestAsync(HttpMethod.Post, "/control/v1/mic", MicModel.Request(false, null, preferred, "")!, ct).ConfigureAwait(false);
            return;
        }
        if (!preferred.HasChoice)
            throw new ControlException(MicModel.ChooseText, code: "microphone_unset");
        for (var attempt = 0; ; attempt++)
        {
            var inventory = await MicrophonesAsync(ct).ConfigureAwait(false);
            var body = MicModel.Request(true, inventory.Microphones, preferred, inventory.Inventory)
                ?? throw new ControlException(MicModel.MissingText(preferred), code: "microphone_missing", microphones: inventory.Microphones);
            try
            {
                await RequestAsync(HttpMethod.Post, "/control/v1/mic", body, ct).ConfigureAwait(false);
                return;
            }
            catch (ControlException e) when (attempt == 0 && e.Code == "inventory_changed")
            {
            }
        }
    }

    /// <returns>The state after the command.</returns>
    public async Task<string?> CommandAsync(string action, CancellationToken ct = default)
    {
        if (action is not ("start" or "pause" or "resume" or "stop"))
            throw new ArgumentOutOfRangeException(nameof(action));
        var data = await RequestAsync(HttpMethod.Post, "/control/v1/command", new Dictionary<string, object> { ["action"] = action }, ct)
            .ConfigureAwait(false);
        return data.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    public Task<JsonElement> SetIntervalAsync(int seconds, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 3600);
        return RequestAsync(HttpMethod.Post, "/control/v1/interval", new Dictionary<string, object> { ["seconds"] = seconds }, ct);
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var endpoint = await EndpointAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendAsync(endpoint, method, path, body, ct).ConfigureAwait(false);
        }
        catch (ControlException e) when (e.Status == (int)HttpStatusCode.Forbidden)
        {
            // The session may have restarted with a new token.
            var fresh = await EndpointAsync(ct).ConfigureAwait(false);
            if (fresh == endpoint)
                throw;
            return await SendAsync(fresh, method, path, body, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> SendAsync(ControlEndpoint endpoint, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            // Exactly "application/json": the API compares the media type, and a charset parameter is noise.
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            // A control file left behind by a crashed session.
            throw new ControlException("connection refused", notRunning: true);
        }
        catch (HttpRequestException e)
        {
            throw new ControlException($"request failed: {e.HttpRequestError}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlException("request failed: timeout");
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
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
                var code = data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
                throw new ControlException(code is null ? $"HTTP {status}" : $"HTTP {status} {code}", status, code);
            }
            if (data is not { ValueKind: JsonValueKind.Object } ok)
                throw new ControlException("invalid JSON response");
            return ok;
        }
    }
}
