using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MauiScannetwork.Features.Diagnostics;

public partial class DiagnosticsPage : ContentPage
{
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _useTcp;
    private int _sent;
    private int _received;

    public DiagnosticsPage()
    {
        InitializeComponent();
        PckMode.SelectedIndex = 0;
    }

    private void SetRunning(bool running)
    {
        _running = running;
        TxtIp.IsEnabled = !running;
        PckMode.IsEnabled = !running;
        BtnPing.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        BtnClear.IsEnabled = !running;
        BtnPing.Text = running ? "DANG PING..." : "PING";
    }

    private async void OnPingClicked(object? sender, EventArgs e)
    {
        if (_running) return;

        var ip = TxtIp.Text?.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            AppendLine("Vui long nhap dia chi IP (xxx.xxx.xxx.xxx).", Colors.Red);
            return;
        }
        if (!System.Net.IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
        {
            AppendLine($"IP khong hop le: {ip}. Dinh dang dung: xxx.xxx.xxx.xxx", Colors.Red);
            return;
        }

        bool continuous = PckMode.SelectedIndex == 1;
        _cts = new CancellationTokenSource();
        _sent = 0;
        _received = 0;
        _useTcp = false;
        SetRunning(true);

        AppendLine($"--- Ping {ip} - {(continuous ? "lien tuc (-t)" : "4 goi tin")} ---", Colors.DarkBlue);

        try
        {
            if (continuous)
            {
                while (!_cts.IsCancellationRequested)
                {
                    var cycle = System.Diagnostics.Stopwatch.StartNew();
                    await DoSinglePingAsync(ip, _cts.Token);
                    await PaceAsync(cycle, _cts.Token);
                }
            }
            else
            {
                for (int i = 0; i < 4 && !_cts.IsCancellationRequested; i++)
                {
                    var cycle = System.Diagnostics.Stopwatch.StartNew();
                    await DoSinglePingAsync(ip, _cts.Token);
                    if (i < 3)
                        await PaceAsync(cycle, _cts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLine($"Loi: {ex.Message}", Colors.Red);
        }
        finally
        {
            int lost = _sent - _received;
            int lossPct = _sent == 0 ? 0 : (int)Math.Round(lost * 100.0 / _sent);
            AppendLine($"--- Thong ke: {_sent} goi gui, {_received} nhan, mat {lost} ({lossPct}%) ---", Colors.DarkBlue);
            SetRunning(false);
        }
    }

    private async Task PaceAsync(System.Diagnostics.Stopwatch cycle, CancellationToken token)
    {
        var remaining = 1000 - cycle.ElapsedMilliseconds;
        if (remaining > 0 && !token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(remaining), token); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task DoSinglePingAsync(string ip, CancellationToken token)
    {
        Interlocked.Increment(ref _sent);

        if (_useTcp)
        {
            await DoTcpPingAsync(ip, token);
            return;
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse(ip), TimeSpan.FromSeconds(3), new byte[32], new PingOptions(64, true), token);
            if (token.IsCancellationRequested) return;
            if (reply.Status == IPStatus.Success)
            {
                Interlocked.Increment(ref _received);
                AppendLine($"Reply tu {ip}: bytes=32 time={reply.RoundtripTime}ms TTL={reply.Options?.Ttl}", Colors.Green);
                return;
            }
            AppendLine(reply.Status == IPStatus.TimedOut
                ? $"Yeu cau ping {ip} vuot thoi gian (2s)."
                : $"{ip}: {reply.Status}", Colors.Red);
        }
        catch (OperationCanceledException) { }
        catch (PingException ex)
        {
            if (token.IsCancellationRequested) return;
            if (IsIcmpBlocked(ex))
                _useTcp = true;
            else
                AppendLine($"Loi ping {ip}: {ex.InnerException?.Message ?? ex.Message}", Colors.Red);
        }
        catch (Exception ex) when (IsIcmpBlocked(ex))
        {
            if (token.IsCancellationRequested) return;
            _useTcp = true;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                AppendLine($"Loi: {ex.Message}", Colors.Red);
        }

        if (_useTcp)
        {
            AppendLine("ICMP bi chan tren thiet bi nay (app khong co root/cap_net_raw). Chuyen sang ping qua TCP (cac cong 80/443/22/554/8000/8080/53).", Colors.DarkOrange);
            await DoTcpPingAsync(ip, token);
        }
    }

    private static bool IsIcmpBlocked(Exception ex)
    {
        var msg = (ex.InnerException?.Message ?? ex.Message) ?? "";
        return msg.Contains("unable to send", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("cap_net_raw", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("privileged", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DoTcpPingAsync(string ip, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(2000);

            var tasks = new List<Task<TcpResult>>();
            foreach (var port in new[] { 80, 443, 22, 554, 8000, 8080, 53 })
                tasks.Add(TcpConnectAsync(ip, port, cts.Token));

            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                var res = await done;
                if (res.Connected)
                {
                    Interlocked.Increment(ref _received);
                    AppendLine($"Reply tu {ip}: TCP:{res.Port} time={res.ElapsedMs}ms", Colors.Green);
                    return;
                }
                if (token.IsCancellationRequested || cts.IsCancellationRequested)
                    break;
            }

            AppendLine($"Khong ket noi duoc {ip} (thu: 80/443/22/554/8000/8080/53) sau 2s.", Colors.Red);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                AppendLine($"Loi TCP ping {ip}: {ex.Message}", Colors.Red);
        }
    }

    private record TcpResult(int Port, bool Connected, int ElapsedMs, string? Error);

    private async Task<TcpResult> TcpConnectAsync(string ip, int port, CancellationToken token)
    {
        using var client = new System.Net.Sockets.TcpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, token);
            sw.Stop();
            return new TcpResult(port, client.Connected, Math.Max(0, (int)sw.ElapsedMilliseconds), null);
        }
        catch (OperationCanceledException)
        {
            return new TcpResult(port, false, 0, null);
        }
        catch (Exception ex)
        {
            return new TcpResult(port, false, 0, ex.Message);
        }
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        AppendLine("Dang dung ping...", Colors.DarkOrange);
        _cts?.Cancel();
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        ResultStack.Children.Clear();
    }

    private void AppendLine(string text, Microsoft.Maui.Graphics.Color? color = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var label = new Label
            {
                Text = text,
                FontSize = 12,
                FontFamily = "monospace",
                TextColor = color ?? Colors.DarkGray,
                LineBreakMode = LineBreakMode.WordWrap
            };
            ResultStack.Children.Insert(0, label);
            if (ResultStack.Children.Count > 800)
                ResultStack.Children.RemoveAt(ResultStack.Children.Count - 1);
            _ = ResultScroll.ScrollToAsync(0, 0, false);
        });
    }
}