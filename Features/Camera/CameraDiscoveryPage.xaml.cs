using System.Collections.ObjectModel;

namespace MauiScannetwork.Features.Camera;

public partial class CameraDiscoveryPage : ContentPage
{
    private bool _scanning;
    private CancellationTokenSource? _cts;
    private string _filter = "";
    private ObservableCollection<CameraDevice> _items = new();

    // Shell tao lai instance cua tab moi khi quay lai -> cache static de giu danh sach + bo loc
    private static readonly ObservableCollection<CameraDevice> _cachedItems = new();
    private static string _cachedFilter = "";

    public CameraDiscoveryPage()
    {
        _items = _cachedItems;
        _filter = _cachedFilter;
        InitializeComponent();
        IpFilter.Text = _cachedFilter;
        CameraList.ItemsSource = _items;
        ApplyFilter();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _items = _cachedItems;
        _filter = _cachedFilter;
        if (IpFilter.Text != _cachedFilter)
            IpFilter.Text = _cachedFilter;
        CameraDiscoveryService.ListChanged += OnServiceListChanged;
        ApplyFilter();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CameraDiscoveryService.ListChanged -= OnServiceListChanged;
    }

    // Duoc goi khi service cap nhat danh sach (realtime trong luc scan,
    // ke ca khi page bi Shell tao moi giua chung) -> refresh lien tuc
    private void OnServiceListChanged()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(ApplyFilter);
            return;
        }
        try { ApplyFilter(); } catch { }
    }

    private void SetBusy(bool busy)
    {
        _scanning = busy;
        BtnScanSubnet.IsEnabled = !busy;
        BtnScanUnknown.IsEnabled = !busy;
        BtnStop.IsVisible = busy;
    }

    // Loc danh sach theo IP (hoac MAC/vendor/info) vua nhap, cap nhat realtime.
    // Khi KHONG loc: ItemsSource giu nguyen ObservableCollection _items de scan them
    // tu tung camera (khong rebind lai toan bo -> cuon list khong bi reset).
    private ObservableCollection<CameraDevice> _filtered = new();
    private void ApplyFilter()
    {
        var f = _filter.Trim();
        if (f.Length == 0)
        {
            if (!ReferenceEquals(CameraList.ItemsSource, _items))
                CameraList.ItemsSource = _items;
            return;
        }

        _filtered = new ObservableCollection<CameraDevice>(_items.Where(c =>
            c.Ip.Contains(f, StringComparison.OrdinalIgnoreCase)
            || c.Mac.Contains(f, StringComparison.OrdinalIgnoreCase)
            || c.Vendor.Contains(f, StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || c.Info.Contains(f, StringComparison.OrdinalIgnoreCase)));
        CameraList.ItemsSource = _filtered;
        LblStatus.Text = _filtered.Count == 0
            ? "Khong tim thay camera nao trong danh sach"
            : $"Loc: hien thi {_filtered.Count}/{_items.Count} camera";
    }

    private void OnIpFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        _cachedFilter = e.NewTextValue ?? "";
        _filter = _cachedFilter;
        ApplyFilter();
    }

    private async void OnScanSubnetClicked(object? sender, EventArgs e)
    {
        if (_scanning) return;

        SetBusy(true);
        LblStatus.Text = "Dang khoi dong scan lop mang...";

        // Xoa bo loc cu (neu con gi lai tu lan truoc) de sau scan hien thi full danh sach
        _cachedFilter = "";
        _filter = "";
        IpFilter.Text = "";

        _items.Clear();
        ApplyFilter();

        _cts = new CancellationTokenSource();
        try
        {
            await CameraDiscoveryService.ScanInSubnetAsync(_items, msg => LblStatus.Text = msg, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            LblStatus.Text = "Da huy scan.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"Loi: {ex.Message}";
            ApplyFilter();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnScanUnknownClicked(object? sender, EventArgs e)
    {
        if (_scanning) return;

        SetBusy(true);
        LblStatus.Text = "Dang khoi dong scan (TCP + SADP/ONVIF/Provision/SSDP)...";

        // Xoa bo loc cu (neu con gi lai tu lan truoc) de sau scan hien thi full danh sach
        _cachedFilter = "";
        _filter = "";
        IpFilter.Text = "";

        _items.Clear();
        ApplyFilter();

        _cts = new CancellationTokenSource();
        try
        {
            // Cung chay scan toan dien nhu Scan Lop Mang (TCP-scan tim camera du IP,
            // UDP discovery bo sung thong tin). Dung chung 1 luong scan de trach ghi de list.
            await CameraDiscoveryService.ScanInSubnetAsync(_items, msg => LblStatus.Text = msg, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            LblStatus.Text = "Da huy scan.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"Loi: {ex.Message}";
            ApplyFilter();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        _cts?.Cancel();
    }

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
