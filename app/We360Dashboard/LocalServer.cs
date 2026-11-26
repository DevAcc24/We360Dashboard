using System.Net;
using System.Net.Sockets;
using System.Text;

namespace We360Dashboard;

/// <summary>
/// Мінімальний HTTP-сервер лише на 127.0.0.1. Віддає одну сторінку за секретною адресою.
/// </summary>
public sealed class LocalServer : IDisposable
{
    readonly TcpListener _listener;
    readonly string _token;
    readonly Func<CancellationToken, Task<string>> _render;
    readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/{_token}/";

    public LocalServer(int preferredPort, string token, Func<CancellationToken, Task<string>> render)
    {
        _token = token;
        _render = render;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, preferredPort);
            _listener.Start();
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0); // порт зайнятий — беремо вільний
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                // ReceiveTimeout на асинхронне читання не діє, тому обмежуємо час токеном:
                // з'єднання, через яке так і не надіслали запит, закривається через 10 с
                using var io = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                io.CancelAfter(TimeSpan.FromSeconds(10));
                var (method, path, host) = await ReadRequest(stream, io.Token);
                if (method == null) return;
                io.CancelAfter(Timeout.Infinite); // запит прочитано; збірка сторінки (перший розбір логів) може тривати довше
                bool headOnly = method == "HEAD";
                Task Send(int status, string type, string body, string? location = null)
                {
                    io.CancelAfter(TimeSpan.FromSeconds(30)); // на відповідь окремий запас часу
                    return Respond(stream, status, type, body, location, headOnly, io.Token);
                }

                // захист від DNS rebinding: приймаємо лише наші власні імена хоста
                if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
                {
                    await Send(421, "text/plain", "Misdirected request");
                    return;
                }
                if (method != "GET" && method != "HEAD")
                {
                    await Send(405, "text/plain", "Method not allowed");
                    return;
                }
                var p = path!.Split('?')[0];
                if (p == $"/{_token}")
                {
                    await Send(301, "text/plain", "", $"/{_token}/");
                    return;
                }
                if (p != $"/{_token}/")
                {
                    await Send(404, "text/plain", "Not found");
                    return;
                }
                string html;
                int status = 200;
                try { html = await _render(_cts.Token); }
                catch (Exception ex)
                {
                    status = 500;
                    html = "<!doctype html><meta charset=utf-8><title>Помилка</title><pre>" + WebUtility.HtmlEncode(ex.ToString()) + "</pre>";
                }
                await Send(status, "text/html; charset=utf-8", html);
            }
            catch { /* клієнт відʼєднався */ }
        }
    }

    static async Task<(string? method, string? path, string? host)> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[16384];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) break;
            len += n;
            if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
        }
        var head = Encoding.ASCII.GetString(buf, 0, len);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return (null, null, null);
        string? host = null;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = l[5..].Trim().ToLowerInvariant();
        return (first[0], first[1], host);
    }

    /// <summary>headOnly: відповідь на HEAD - ті самі заголовки, зокрема справжній Content-Length, але без тіла.</summary>
    static async Task Respond(NetworkStream s, int status, string contentType, string body, string? location, bool headOnly, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 301 => "Moved Permanently", 404 => "Not Found", 405 => "Method Not Allowed", 421 => "Misdirected Request", _ => "Error" };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {bytes.Length}\r\n");
        if (location != null) sb.Append($"Location: {location}\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'none'\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await s.WriteAsync(headBytes, ct);
        if (!headOnly) await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
