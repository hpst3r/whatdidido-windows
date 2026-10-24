using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WhatDidIDo.Tray.Core.Tests;

/// <summary>A request as the fake received it, headers lower-cased.</summary>
internal sealed record ReceivedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A loopback HTTP/1.1 fake of the work session's control API that enforces
/// the contract's request rules (CONTROL_API.md "Discovery and
/// authentication"), like the extension's tests/fake_control.js: a wrong
/// token, Host, browser-shaped header, query string or body is a 403/400
/// before the route's handler runs. It reads raw bytes, so it sees exactly the
/// headers the client sent.
/// </summary>
internal sealed class FakeControlServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<ReceivedRequest> _requests = [];

    public FakeControlServer(string token)
    {
        Token = token;
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public string Token { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>The route handler after the guards: (method, path, body) → (status, JSON).</summary>
    public Func<string, string, string, (int Status, string Json)> Handler { get; set; } = (_, _, _) => (404, """{"error":"not_found"}""");

    public IReadOnlyList<ReceivedRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public string ControlJson(string? token = null) =>
        $$"""{"version": 1, "base_url": "{{BaseUrl}}", "token": "{{token ?? Token}}"}""";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var request = await ReadRequest(stream);
            if (request is null)
                return;
            lock (_requests)
                _requests.Add(request);
            var (status, json) = Guard(request) ?? Handler(request.Method, request.Target, request.Body);
            var body = Encoding.UTF8.GetBytes(json);
            var head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nCache-Control: no-store\r\n"
                + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
        }
    }

    private (int, string)? Guard(ReceivedRequest r)
    {
        const string forbidden = """{"error":"forbidden"}""";
        const string invalid = """{"error":"invalid"}""";
        var h = r.Headers;
        if (!h.TryGetValue("authorization", out var auth) || auth != $"Bearer {Token}")
            return (403, forbidden);
        if (!h.TryGetValue("host", out var host) || host != $"127.0.0.1:{Port}")
            return (403, forbidden);
        foreach (var name in new[] { "origin", "sec-fetch-site", "sec-fetch-mode", "cookie", "forwarded", "x-real-ip" })
        {
            if (h.ContainsKey(name))
                return (403, forbidden);
        }
        if (h.Keys.Any(k => k.StartsWith("x-forwarded-", StringComparison.Ordinal)))
            return (403, forbidden);
        if (r.Target.Contains('?', StringComparison.Ordinal))
            return (400, invalid);
        if (r.Method == "POST")
        {
            if (!h.TryGetValue("content-type", out var type) || type != "application/json")
                return (400, invalid);
            if (Encoding.UTF8.GetByteCount(r.Body) > 1024)
                return (400, invalid);
        }
        return null;
    }

    private static async Task<ReceivedRequest?> ReadRequest(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer))
        {
            if (await stream.ReadAsync(one) == 0)
                return null;
            buffer.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        var length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read));
            if (n == 0)
                break;
            read += n;
        }
        return new ReceivedRequest(parts[0], parts[1], headers, Encoding.UTF8.GetString(body, 0, read));
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';
}

/// <summary>A control file held in memory, so a test can rewrite or remove it.</summary>
internal sealed class MemoryControlFile : Control.IControlFileSource
{
    public string? Text { get; set; }

    public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
}
