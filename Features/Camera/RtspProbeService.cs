using System.Net.Sockets;
using System.Text;

namespace MauiScannetwork.Features.Camera;

public static class RtspProbeService
{
    /// <summary>Quet nhanh (song song) cac port TCP, tra ve danh sach port dang mo.</summary>
    public static async Task<HashSet<int>> ProbePortsAsync(string ip, IEnumerable<int> ports, int timeoutMs, CancellationToken token)
    {
        var open = new HashSet<int>();
        var tasks = ports.Distinct().Select(async port =>
        {
            if (await IsPortOpenAsync(ip, port, timeoutMs, token))
                open.Add(port);
        });
        await Task.WhenAll(tasks);
        return open;
    }

    public static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var delayTask = Task.Delay(timeoutMs, token);
            var done = await Task.WhenAny(connectTask, delayTask);
            return done == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static int PortOf(string rtspUrl)
    {
        try
        {
            var host = rtspUrl.Substring(rtspUrl.IndexOf('@') + 1);
            var colon = host.IndexOf(':');
            if (colon < 0) return 554;
            var slash = host.IndexOf('/', colon);
            var portStr = slash < 0 ? host.Substring(colon + 1) : host.Substring(colon + 1, slash - colon - 1);
            return int.TryParse(portStr, out var p) ? p : 554;
        }
        catch
        {
            return 554;
        }
    }

    /// <summary>Giau mat khau trong URL RTSP khi hien thi: rtsp://admin:***@ip:port/path</summary>
    public static string Sanitize(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        var host = url.IndexOf('@', scheme + 3);
        if (host < 0) return url;
        var colon = url.IndexOf(':', scheme + 3, host - (scheme + 3));
        if (colon < 0) return url;
        return url.Substring(0, colon) + ":***" + url.Substring(host);
    }

    /// <summary>Tach host/port tu URL RTSP (bo qua user:pass@).</summary>
    public static (string Host, int Port) ParseRtsp(string url)
    {
        var host = url;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = url.Substring(scheme + 3);
        var at = host.IndexOf('@');
        if (at >= 0) host = host.Substring(at + 1);
        var port = 554;
        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            var slash = host.IndexOf('/', colon);
            var portStr = slash < 0 ? host.Substring(colon + 1) : host.Substring(colon + 1, slash - colon - 1);
            if (int.TryParse(portStr, out var p)) port = p;
            host = host.Substring(0, colon);
        }
        var q = host.IndexOf('/');
        if (q >= 0) host = host.Substring(0, q);
        return (host, port);
    }

    public static string HostOf(string url) => ParseRtsp(url).Host;

    public static (string User, string Pass) ParseCreds(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return ("", "");
        var rest = url.Substring(scheme + 3);
        var at = rest.IndexOf('@');
        if (at < 0) return ("", "");
        var cred = rest.Substring(0, at);
        var colon = cred.IndexOf(':');
        if (colon < 0) return (Uri.UnescapeDataString(cred), "");
        return (Uri.UnescapeDataString(cred.Substring(0, colon)), Uri.UnescapeDataString(cred.Substring(colon + 1)));
    }

    private static string UriWithoutCreds(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        var rest = url.Substring(scheme + 3);
        var at = rest.IndexOf('@');
        if (at >= 0) rest = rest.Substring(at + 1);
        return url.Substring(0, scheme + 3) + rest;
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf("\r\n", StringComparison.Ordinal);
        return i > 0 ? s.Substring(0, i).Trim() : s;
    }

    private static async Task<string> SendAndRead(NetworkStream ns, string req, int timeoutMs)
    {
        var bytes = Encoding.ASCII.GetBytes(req);
        await ns.WriteAsync(bytes, 0, bytes.Length);
        var buf = new byte[2048];
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (sb.Length < 8192)
        {
            int n;
            try { n = await ns.ReadAsync(buf, 0, buf.Length, cts.Token); }
            catch { break; }
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n") && sb.Length > 512) break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Gui DESCRIBE (co/khong Authorization Basic) de kiem tra RTSP chay duoc den buoc bao nhieu:
    /// - "RTSP/1.0 200 OK" + SDP → control chan tot, loi nam o buoc SETUP/media.
    /// - 401/407 khi co auth → sai user/pass hoac server qua kich.
    /// - khong phan hoi → firewall/VLAN chan cac goi lon hon OPTIONS.
    /// </summary>
    public static async Task<string> DescribeRtspAsync(string url, string user, string pass, int timeoutMs)
    {
        (var host, var port) = ParseRtsp(url);
        var uri = UriWithoutCreds(url);
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
                return "TCP khong mo duoc trong thoi gian cho";
            await connect;

            var ns = tcp.GetStream();
            var resp = await SendAndRead(ns, $"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\nUser-Agent: MauiScanner\r\n\r\n", timeoutMs);
            var firstLine = FirstLine(resp);

            if (resp.Contains("401") || resp.Contains("407"))
            {
                var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                resp = await SendAndRead(ns, $"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 3\r\nAccept: application/sdp\r\nAuthorization: Basic {cred}\r\nUser-Agent: MauiScanner\r\n\r\n", timeoutMs);
                firstLine = FirstLine(resp);
            }

            if (resp.Length == 0) return "Khong nhan phan hoi DESCRIBE (bi chan o muc goi +?).";
            var bodyStart = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var body = bodyStart > 0 ? resp.Substring(bodyStart + 4) : "";
            return body.Length > 0
                ? $"{firstLine} | SDP: {body.Substring(0, Math.Min(180, body.Length)).Replace("\r\n", " ")}"
                : firstLine;
        }
        catch (Exception ex)
        {
            return "Loi DESCRIBE: " + ex.Message;
        }
    }

    /// <summary>
    /// Doi chieu thuc su giao thuc RTSP: mo TCP, gui OPTIONS va cho phan hoi.
    /// Dung de phan biet "TCP mo nhung firewal/VLAN chan RTSP" vs "RTSP chay on".
    /// </summary>
    public static async Task<string> CheckRtspAsync(string url, int timeoutMs)
    {
        (var host, var port) = ParseRtsp(url);
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(host, port);
            var delay = Task.Delay(timeoutMs);
            if (await Task.WhenAny(connect, delay) != connect)
                return $"TCP {host}:{port} khong mo trong {timeoutMs / 1000}s";
            await connect;

            var ns = tcp.GetStream();
            var req = Encoding.ASCII.GetBytes($"OPTIONS rtsp://{host}:{port} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: MauiScanner\r\n\r\n");
            await ns.WriteAsync(req, 0, req.Length);

            var buf = new byte[2048];
            var cts = new CancellationTokenSource(timeoutMs);
            var doTimeout = Task.Delay(timeoutMs);
            var readTask = ns.ReadAsync(buf, 0, buf.Length, cts.Token);
            var rd = await Task.WhenAny(readTask, doTimeout);
            if (rd != readTask || !readTask.IsCompleted)
                return "TCP mo, gui OPTIONS 200 OK nhung KHONG nhan phan hoi → RTSP bi chan qua VLAN/firewall";
            var n = await readTask;
            if (n <= 0)
                return "TCP mo nhung server dong ket noi ngay (port nay khong phai RTSP)";
            var text = Encoding.ASCII.GetString(buf, 0, n);
            var first = text.Substring(0, Math.Min(60, text.Length)).Replace("\r\n", " | ");
            return text.StartsWith("RTSP/1.0", StringComparison.Ordinal)
                ? $"RTSP OK: {first}"
                : $"Tra loi la la: {first}";
        }
        catch (Exception ex)
        {
            return "Loi RTSP: " + ex.Message;
        }
    }
}