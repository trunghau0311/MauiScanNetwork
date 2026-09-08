using Android.Content;
using Android.Views;
using Android.Widget;
using MauiScannetwork.Features.Camera;
using Microsoft.Maui.Handlers;
using AndroidVideoView = Android.Widget.VideoView;
using AndroidUri = Android.Net.Uri;
using AndroidMediaPlayer = Android.Media.MediaPlayer;
using AndroidMedia = Android.Media;

namespace MauiScannetwork.Platforms.Android;

public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, AndroidVideoView>
{
    public static IPropertyMapper<VideoPlayerView, VideoPlayerViewHandler> PropertyMapper = new PropertyMapper<VideoPlayerView, VideoPlayerViewHandler>(ViewHandler.ViewMapper)
    {
        [nameof(VideoPlayerView.Source)] = MapSource,
        [nameof(VideoPlayerView.IsPlaying)] = MapIsPlaying,
    };

    public VideoPlayerViewHandler() : base(PropertyMapper)
    {
    }

    protected override AndroidVideoView CreatePlatformView()
    {
        var videoView = new AndroidVideoView(Context);
        videoView.SetZOrderOnTop(false);
        videoView.LayoutParameters = new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

        videoView.SetOnPreparedListener(new PreparedListener(this));
        videoView.SetOnErrorListener(new ErrorListener(this));
        videoView.SetOnCompletionListener(new CompletionListener(this));

        return videoView;
    }

    private class PreparedListener : Java.Lang.Object, AndroidMediaPlayer.IOnPreparedListener
    {
        private readonly VideoPlayerViewHandler _handler;
        public PreparedListener(VideoPlayerViewHandler h) => _handler = h;
        public void OnPrepared(AndroidMediaPlayer mp)
        {
            // ScaleToFit (letterbox) thay vi ScaleToFitWithCrop mac dinh: hien thi du video,
            // khong cat 2 ben khi khung cao hon aspect video
            try { mp.SetVideoScalingMode(AndroidMedia.VideoScalingMode.ScaleToFit); } catch { }
            mp.Start();
            MainThread.BeginInvokeOnMainThread(() => _handler.VirtualView?.NotifyPlaybackStarted());
        }
    }

    private class ErrorListener : Java.Lang.Object, AndroidMediaPlayer.IOnErrorListener
    {
        private readonly VideoPlayerViewHandler _handler;
        public ErrorListener(VideoPlayerViewHandler h) => _handler = h;
        public bool OnError(AndroidMediaPlayer mp, AndroidMedia.MediaError what, int extra)
        {
            MainThread.BeginInvokeOnMainThread(() => _handler.VirtualView?.NotifyPlaybackFailed());
            return true;
        }
    }

    private class CompletionListener : Java.Lang.Object, AndroidMediaPlayer.IOnCompletionListener
    {
        private readonly VideoPlayerViewHandler _handler;
        public CompletionListener(VideoPlayerViewHandler h) => _handler = h;
        public void OnCompletion(AndroidMediaPlayer mp)
        {
            MainThread.BeginInvokeOnMainThread(() => _handler.VirtualView?.NotifyPlaybackCompleted());
        }
    }

    protected override void ConnectHandler(AndroidVideoView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView != null)
            VirtualView.SourceChanged += OnSourceChanged;
    }

    protected override void DisconnectHandler(AndroidVideoView platformView)
    {
        if (VirtualView != null)
            VirtualView.SourceChanged -= OnSourceChanged;
        try { platformView.StopPlayback(); } catch { }
        base.DisconnectHandler(platformView);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (VirtualView is not null)
            MapSource(this, VirtualView);
    }

    private static void MapSource(VideoPlayerViewHandler handler, VideoPlayerView view)
    {
        handler.PlatformView?.SetVideoURI(string.IsNullOrEmpty(view.Source) ? null : AndroidUri.Parse(view.Source));
        if (!string.IsNullOrEmpty(view.Source))
            handler.PlatformView?.Start();
    }

    private static void MapIsPlaying(VideoPlayerViewHandler handler, VideoPlayerView view)
    {
        if (view.IsPlaying)
            handler.PlatformView?.Start();
        else
            handler.PlatformView?.Pause();
    }
}
