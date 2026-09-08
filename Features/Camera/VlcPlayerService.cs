using System;
using System.Collections.Generic;
using System.Linq;
using LibVLCSharp.Shared;

namespace MauiScannetwork.Features.Camera;

public static class VlcPlayerService
{
    private static LibVLC? _libVLC;
    private static readonly List<string> LogLines = new();
    private const int MaxLines = 120;
    private static string? _logPath;

    public static string LastError { get; private set; } = string.Empty;
    public static string LastPlayError { get; internal set; } = string.Empty;
    public static string LibVLCInitError { get; private set; } = string.Empty;

    public static LibVLC LibVLC
    {
        get
        {
            if (_libVLC == null)
            {
                try
                {
                    Core.Initialize();
                    _logPath = Path.Combine(FileSystem.AppDataDirectory, "vlc.log");
                    try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }
                    var opts = new[] { "--rtsp-tcp", "--network-caching=1000", "--no-audio" };
                    _libVLC = new LibVLC(opts);
                    _libVLC.Log += OnLibVlcLog;
                }
                catch (Exception ex)
                {
                    LibVLCInitError = ex.ToString();
                    try
                    {
                        _libVLC = new LibVLC(Array.Empty<string>());
                        _libVLC.Log += OnLibVlcLog;
                        LibVLCInitError += $"\n(Options day du that bai, options RONG lai doi duoc!)";
                    }
                    catch (Exception ex2)
                    {
                        LibVLCInitError += $"\n(Options RONG cung that bai: {ex2.Message})";
                        throw;
                    }
                }
            }
            return _libVLC;
        }
    }

    /// <summary>Lay n dong log moi nhat tu file log cua VLC (bat ca stderr cua live555).</summary>
    public static string ReadLogFile(int n)
    {
        try
        {
            if (_logPath == null || !File.Exists(_logPath)) return "(chua co file log)";
            var lines = File.ReadAllLines(_logPath);
            var skip = Math.Max(0, lines.Length - n);
            return string.Join(Environment.NewLine, lines.Skip(skip));
        }
        catch (Exception ex)
        {
            return "Loi doc log: " + ex.Message;
        }
    }

    /// <summary>Lay n dong log moi nhat cua VLC (de hien ra khi phat that bai).</summary>
    public static string LogTail(int n)
    {
        lock (LogLines)
        {
            var take = Math.Min(n, LogLines.Count);
            return string.Join(Environment.NewLine, LogLines.Skip(LogLines.Count - take));
        }
    }

    public static MediaPlayer CreatePlayer() => new MediaPlayer(LibVLC);

    private static void OnLibVlcLog(object? sender, LibVLCSharp.Shared.LogEventArgs e)
    {
        lock (LogLines)
        {
            LogLines.Add($"[{e.Level}] {e.Message}");
            if (LogLines.Count > MaxLines) LogLines.RemoveAt(0);
        }
        if (e.Level is LogLevel.Warning or LogLevel.Error)
            LastError = e.Message;
    }
}
