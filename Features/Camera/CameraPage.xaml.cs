using System.Collections.ObjectModel;

namespace MauiScannetwork.Features.Camera;

public partial class CameraPage : ContentPage
{
    private bool _scanning;
    private CancellationTokenSource? _cts;

    public CameraPage()
    {
        InitializeComponent();
        CameraList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<CameraDevice>();
    }

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        if (_scanning) return;

        _scanning = true;
        BtnScan.IsEnabled = false;
        BtnScan.Text = "DANG SCAN...";
        LblStatus.Text = "Dang tim camera trong LAN...";

        var items = (System.Collections.ObjectModel.ObservableCollection<CameraDevice>)CameraList.ItemsSource;
        items.Clear();

        _cts = new CancellationTokenSource();
        try
        {
            await ScanLanAsync(items, _cts.Token);
            LblStatus.Text = $"Hoan tat. Tim thay {items.Count} camera.";
        }
        catch (OperationCanceledException)
        {
            LblStatus.Text = "Da huy scan.";
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"Loi: {ex.Message}";
        }
        finally
        {
            _scanning = false;
            BtnScan.IsEnabled = true;
            BtnScan.Text = "SCAN CAMERA";
        }
    }

    private async Task ScanLanAsync(ObservableCollection<CameraDevice> items, CancellationToken token)
        => await CameraDiscoveryService.ScanInSubnetAsync(items, msg => LblStatus.Text = msg, token);

    private async void OnViewClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is CameraDevice device)
        {
            var page = new CameraViewPage(device);
            await Navigation.PushAsync(page);
        }
    }

    private async void OnPreviewClicked(object? sender, EventArgs e)
    {
        var page = new CameraPreviewPage();
        await Navigation.PushAsync(page);
    }
}
