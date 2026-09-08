using LibVLCSharp.Shared;

namespace MauiScannetwork.Features.Camera;

public partial class CameraViewPage : ContentPage
{
    private CameraDevice _device = new();
    private bool _showPass;
    private bool _isHik;
    private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
    private LibVLCSharp.Shared.Media? _vlcMedia;

    public CameraViewPage(CameraDevice device)
    {
        InitializeComponent();
        _device = device;
        BindingContext = new { Ip = device.Ip };
        PckEngine.SelectedIndex = 0;
    }

    protected override void OnDisappearing()
    {
        StopPlayback();
        base.OnDisappearing();
    }

    private void OnVendorClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn)
        {
            SetVendor(btn.Text);
        }
    }

    private void OnShowPassClicked(object? sender, EventArgs e)
    {
        _showPass = !_showPass;
        TxtPass.IsPassword = !_showPass;
        BtnShowPass.Text = _showPass ? "Hide" : "Show";
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        StopPlayback();
        LblConnStatus.Text = string.Empty;
        LiveFrame.IsVisible = false;
    }

    private bool UseVlc => PckEngine.SelectedIndex == 0;

    private void SetVendor(string vendor)
    {
        _isHik = string.Equals(vendor, "Hikvision", StringComparison.OrdinalIgnoreCase);
        TxtPort.Text = _isHik ? "8000" : "80";
        TxtUser.Text = "admin";
        TxtPass.Text = _isHik ? "" : "123456";
        LblDefaultPass.IsVisible = true;
        LblDefaultPass.Text = _isHik
            ? "Hikvision default password: admin | khong co (hoac mac-dinh do dai da dat)"
            : "Provision default password: admin | 123456 (RTSP port 544/554/8554, profile1-3)";
        BtnConnect.IsEnabled = true;
        LblConnStatus.Text = string.Empty;
        LiveFrame.IsVisible = false;
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        var ip = _device.Ip;
        var port = TxtPort.Text?.Trim();
        var user = TxtUser.Text?.Trim();
        var pass = TxtPass.Text ?? string.Empty;

        if (string.IsNullOrEmpty(port) || string.IsNullOrEmpty(user))
        {
            LblConnStatus.Text = "Port va user khong duoc bo trong.";
            return;
        }

        BtnConnect.IsEnabled = false;
        BtnConnect.Text = "DANG KET NOI...";
        StopPlayback();

        try
        {
            var candidates = _isHik ? BuildHikRtspUrls(ip, user, pass) : BuildProvRtspUrls(ip, user, pass);

            LblConnStatus.Text = "Dang kiem tra cong RTSP...";
            var openPorts = await RtspProbeService.ProbePortsAsync(ip, candidates.Select(RtspProbeService.PortOf), 600, CancellationToken.None);
            if (openPorts.Count == 0)
            {
                var httpOpen = (await RtspProbeService.ProbePortsAsync(ip, new[] { 80, 8000 }, 600, CancellationToken.None)).Count > 0;
                LblConnStatus.Text = httpOpen
                    ? "Camera co HTTP (80/8000) nhung KHONG mo cong RTSP (554/544/8554). Kiem tra cau hinh RTSP cua camera."
                    : "Camera KHONG phan hoi cong nao. Kiem tra IP (dung menu Camera ben bam SCAN).";
                return;
            }

            var tried = new List<string>();
            var ok = UseVlc
                ? await TryPlayVlcCandidatesAsync(ip, candidates, openPorts, tried)
                : await TryPlaySystemCandidatesAsync(ip, candidates, openPorts, tried);
            if (ok) return;

            var lastErr = VlcPlayerService.LastError;
            var vlcLog = VlcPlayerService.LogTail(30);
            var fileLog = VlcPlayerService.ReadLogFile(40);
            var initErr = VlcPlayerService.LibVLCInitError;
            var playErr = VlcPlayerService.LastPlayError;
            var vlcErr = "";
            if (!string.IsNullOrEmpty(initErr)) vlcErr += $"\nLOI KHOI TAO VLC: {initErr}";
            if (!string.IsNullOrEmpty(playErr)) vlcErr += $"\nLoi phat VLC: {playErr}";
            LblConnStatus.Text = string.IsNullOrEmpty(lastErr)
                ? $"Khong phat duoc video. Da thu {tried.Count} duong dan RTSP (cong mo: {string.Join(", ", openPorts)}). Kiem tra IP/user/pass va duong dan.{vlcErr}\n\nVLC log:\n{vlcLog}\n\nVLC file log:\n{fileLog}"
                : $"Khong phat duoc video. Loi VLC: {lastErr}{vlcErr}\n\nVLC log:\n{vlcLog}\n\nVLC file log:\n{fileLog}";
        }
        catch (Exception ex)
        {
            LblConnStatus.Text = $"Loi: {ex.Message}";
        }
        finally
        {
            BtnConnect.IsEnabled = true;
            BtnConnect.Text = "KET NOI";
        }
    }

    private async Task<bool> TryPlayVlcCandidatesAsync(string ip, System.Collections.Generic.List<string> candidates, HashSet<int> openPorts, System.Collections.Generic.List<string> tried)
    {
        foreach (var rtsp in candidates.Where(c => openPorts.Contains(RtspProbeService.PortOf(c))))
        {
            if (_vlcPlayer is { IsPlaying: true }) return true;
            tried.Add(RtspProbeService.Sanitize(rtsp));
            LblConnStatus.Text = $"Dang thu: {RtspProbeService.Sanitize(rtsp)} (TCP)";
            if (await TryPlayVlcAsync(rtsp, true))
            {
                LblConnStatus.Text = "Dang phat RTSP live qua VLC.";
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryPlaySystemCandidatesAsync(string ip, System.Collections.Generic.List<string> candidates, HashSet<int> openPorts, System.Collections.Generic.List<string> tried)
    {
        foreach (var rtsp in candidates.Where(c => openPorts.Contains(RtspProbeService.PortOf(c))))
        {
            tried.Add(RtspProbeService.Sanitize(rtsp));
            LblConnStatus.Text = $"Dang thu: {RtspProbeService.Sanitize(rtsp)} (VideoView)";
            if (await TryPlayAsync(rtsp))
            {
                LblConnStatus.Text = "Dang phat RTSP live qua VideoView.";
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryPlayAsync(string rtsp)
    {
        var tcs = new TaskCompletionSource<bool>();
        void OnStarted(object? s, EventArgs e) => tcs.TrySetResult(true);
        void OnFailed(object? s, EventArgs e) => tcs.TrySetResult(false);
        LiveView.PlaybackStarted += OnStarted;
        LiveView.PlaybackFailed += OnFailed;
        try
        {
            if (_vlcPlayer != null)
            {
                _vlcPlayer.Stop();
                VlcView.MediaPlayer = null;
            }
            VlcView.IsVisible = false;
            LiveView.IsVisible = true;
            LiveFrame.IsVisible = true;
            LiveView.Source = rtsp;
            LiveView.IsPlaying = true;

            var timeoutTask = Task.Delay(8000);
            var done = await Task.WhenAny(tcs.Task, timeoutTask);
            if (done == timeoutTask || !tcs.Task.IsCompleted)
            {
                StopPlayback();
                return false;
            }
            return await tcs.Task;
        }
        finally
        {
            LiveView.PlaybackStarted -= OnStarted;
            LiveView.PlaybackFailed -= OnFailed;
        }
    }

    private async Task<bool> TryPlayVlcAsync(string rtsp, bool useTcp)
    {
        try
        {
            StopPlayback();
            var player = VlcPlayerService.CreatePlayer();
            _vlcPlayer = player;
            VlcView.MediaPlayer = player;

            var tcs = new TaskCompletionSource<bool>();
            void OnPlaying(object? s, EventArgs e) => tcs.TrySetResult(true);
            void OnError(object? s, EventArgs e) => tcs.TrySetResult(false);
            player.Playing += OnPlaying;
            player.EncounteredError += OnError;
            try
            {
                LiveView.IsVisible = false;
                LiveView.Source = null;
                VlcView.IsVisible = true;
                LiveFrame.IsVisible = true;

                var media = new Media(VlcPlayerService.LibVLC, rtsp, FromType.FromLocation);
                media.AddOption(":network-caching=1000");
                if (useTcp) media.AddOption(":rtsp-tcp");
                _vlcMedia = media;
                player.Play(media);

                var timeoutTask = Task.Delay(15000);
                var done = await Task.WhenAny(tcs.Task, timeoutTask);
                if (done == timeoutTask || !tcs.Task.IsCompleted)
                {
                    var err = VlcPlayerService.LastError;
                    var state = player.State;
                    LblConnStatus.Text = string.IsNullOrEmpty(err)
                        ? $"{RtspProbeService.Sanitize(rtsp)} ({(useTcp ? "TCP" : "UDP")}): timeout - trang thai {state}"
                        : $"{RtspProbeService.Sanitize(rtsp)} ({(useTcp ? "TCP" : "UDP")}): {err}";
                    StopPlayback();
                    return false;
                }
                return await tcs.Task;
            }
            finally
            {
                player.Playing -= OnPlaying;
                player.EncounteredError -= OnError;
            }
        }
        catch (Exception ex)
        {
            StopPlayback();
            VlcPlayerService.LastPlayError = ex.Message;
            LblConnStatus.Text = $"Loi VLC: {ex.Message}";
            return false;
        }
    }

    private void StopPlayback()
    {
        try
        {
            LiveView.IsPlaying = false;
            LiveView.Source = null;
            if (_vlcPlayer != null)
            {
                try { _vlcPlayer.Stop(); } catch { }
                try { VlcView.MediaPlayer = null; } catch { }
                try { _vlcPlayer.Dispose(); } catch { }
                _vlcPlayer = null;
            }
            if (_vlcMedia != null)
            {
                try { _vlcMedia.Dispose(); } catch { }
                _vlcMedia = null;
            }
        }
        catch { }
    }

    private System.Collections.Generic.List<string> BuildHikRtspUrls(string ip, string user, string pass)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var p in new[] { 554, 8000 })
        {
            list.Add(Rtsp(ip, p, user, pass, "/Streaming/Channels/101"));
            list.Add(Rtsp(ip, p, user, pass, "/Streaming/Channels/1"));
        }
        return list;
    }

    private System.Collections.Generic.List<string> BuildProvRtspUrls(string ip, string user, string pass)
    {
        var list = new System.Collections.Generic.List<string>();
        // Profile-style paths on common RTSP ports (provision)
        foreach (var p in new[] { 554, 544 })
            foreach (var profile in new[] { "profile1", "profile2", "profile3" })
                list.Add(Rtsp(ip, p, user, pass, "/" + profile));

        foreach (var p in new[] { 554, 8554, 8000 })
        {
            list.Add(Rtsp(ip, p, user, pass, "/live/ch0"));
            list.Add(Rtsp(ip, p, user, pass, "/live/ch0_0"));
            list.Add(Rtsp(ip, p, user, pass, "/ch0"));
            list.Add(Rtsp(ip, p, user, pass, "/Streaming/Channels/101"));
        }
        return list;
    }

    private string Rtsp(string ip, int port, string user, string pass, string path)
    {
        var cred = System.Net.WebUtility.UrlEncode(user) + ":" + System.Net.WebUtility.UrlEncode(pass) + "@";
        return $"rtsp://{cred}{ip}:{port}{path}";
    }
}
