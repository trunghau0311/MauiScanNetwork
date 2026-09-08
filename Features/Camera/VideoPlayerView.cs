namespace MauiScannetwork.Features.Camera;

public class VideoPlayerView : View
{
    public static readonly BindableProperty SourceProperty =
        BindableProperty.Create(nameof(Source), typeof(string), typeof(VideoPlayerView), null, propertyChanged: OnSourceChanged);

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly BindableProperty IsPlayingProperty =
        BindableProperty.Create(nameof(IsPlaying), typeof(bool), typeof(VideoPlayerView), false);

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    private static void OnSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        (bindable as VideoPlayerView)?.SourceChanged?.Invoke(bindable, EventArgs.Empty);
    }

    public event EventHandler? SourceChanged;
    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackFailed;
    public event EventHandler? PlaybackCompleted;

    public void NotifyPlaybackStarted() => PlaybackStarted?.Invoke(this, EventArgs.Empty);
    public void NotifyPlaybackFailed() => PlaybackFailed?.Invoke(this, EventArgs.Empty);
    public void NotifyPlaybackCompleted() => PlaybackCompleted?.Invoke(this, EventArgs.Empty);
}
