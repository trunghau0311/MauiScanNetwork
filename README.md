# MauiScannetwork

Ứng dụng **.NET MAUI** (chỉ build cho Android) dùng để quét và kiểm tra mạng LAN: xem thông tin kết nối mạng, quét tìm camera IP (Hikvision/Provision/ONVIF) và xem live stream RTSP từ camera.

> Tài liệu này ghi lại toàn bộ cấu trúc code và chức năng từng menu để khi cần code tiếp chỉ cần đọc lại.

---

## 1. Thông tin chung

| Mục | Giá trị |
|---|---|
| Target Framework | `net9.0-android` |
| Min Android version | SDK 21 (Android 5.0) |
| Root namespace | `MauiScannetwork` |
| ApplicationId | `com.companyname.mauiscannetwork` |
| Giao diện | Shell + Flyout (menu trượt bên trái) |
| Ngôn ngữ UI | Tiếng Việt (chưa có i18n/Localization) |
| Background task | Toàn bộ dùng `async/await` trực tiếp trong code-behind, **chưa dùng MVVM / DependencyInjection / MVVM Toolkit** |

### Thư viện nuget đã cài (xem `MauiScannetwork.csproj`)
- `LibVLCSharp` 3.10.1 — thư viện bind VLC để phát RTSP
- `LibVLCSharp.MAUI` 3.10.1 — control `MediaPlayerElement` cho MAUI
- `VideoLAN.LibVLC.Android` 3.6.5 — bản native LibVLC cho Android
- `Microsoft.Extensions.Logging.Debug` 9.0.9

### Cách chạy
- Mở bằng Visual Studio 2022+ (có workload .NET MAUI / Android).
- Build/deploy target: **Android** (chỉ config được `net9.0-android`).
- Đã cấu hình `EmbedAssembliesIntoApk=true` + `AndroidFastDeploymentType=None` để APK sideload được bình thường.

---

## 2. Cấu trúc thư mục

```
MauiScannetwork/
├── MauiProgram.cs                  # Khởi động app: đăng ký handlers, fonts, LibVLC
├── App.xaml / App.xaml.cs          # Application root, merge Styles/Colors
├── AppShell.xaml / .xaml.cs        # Shell + 4 menu Flyout + đăng ký Route
├── Dashboard.xaml / .xaml.cs       # Menu 1: màn hình chào (trang chủ)
├── MauiScannetwork.csproj          # Cấu hình project, packages
│
├── Features/
│   ├── Network/
│   │   ├── NetworkPage.xaml        # Menu 2: kiểm tra thông tin mạng
│   │   └── NetworkPage.xaml.cs
│   ├── Camera/
│   │   ├── CameraDiscoveryPage.xaml # Menu 3: quét camera LAN realtime (WiFi/Mobile/USB-LAN) + xem live
│   │   ├── CameraDiscoveryPage.xaml.cs
│   │   ├── CameraDiscoveryService.cs# Service static: logic quét camera (Probe/Fingerprint/MAC/DiscoverTargets)
│   │   ├── CameraDevice.cs         # Model 1 camera (Ip, Mac, Vendor, Name, Info)
│   │   ├── CameraPreviewPage.xaml  # Xem live bằng IP nhập tay
│   │   ├── CameraPreviewPage.xaml.cs
│   │   ├── CameraViewPage.xaml     # Xem live của camera đã quét thấy
│   │   ├── CameraViewPage.xaml.cs
│   │   ├── CameraPage.xaml(.cs)    # LEGACY (không còn trên menu) — scan cũ, delegate qua service
│   │   ├── VideoPlayerView.cs      # Control custom: bọc Android VideoView (engine "Hệ thống")
│   │   └── VlcPlayerService.cs     # Singleton quản lý LibVLC + tạo MediaPlayer
│   ├── Diagnostics/
│   │   ├── DiagnosticsPage.xaml    # Menu 4: ping IP (4 gói / -t liên tục, real-time)
│   │   └── DiagnosticsPage.xaml.cs
│   └── Nmap/
│       ├── NmapPage.xaml           # Menu 5: CHƯA làm (chỉ có tiêu đề)
│       └── NmapPage.xaml.cs
│
├── Platforms/
│   ├── Android/
│   │   ├── AndroidManifest.xml     # Permissions mạng/wifi/location, cleartext traffic
│   │   ├── MainActivity.cs         # Entry Activity Android
│   │   ├── MainApplication.cs
│   │   └── VideoPlayerViewHandler.cs  # Handler Android cho VideoPlayerView
│   ├── iOS/ MacCatalyst/ Tizen/ Windows/  # Config mặc định (không dùng)
│
└── Resources/
    ├── Styles/Colors.xaml          # Bảng màu
    ├── Styles/Styles.xaml          # Style chung (kể cả "Headline")
    ├── Images/dotnet_bot.png       # Icon menu tạm thời
    ├── AppIcon/ Splash/ Fonts/ Raw/
```

---

## 3. Luồng khởi động

1. `MainActivity` (Android) → `MainApplication.CreateMauiApp()` → `MauiProgram.CreateMauiApp()`
2. `MauiProgram`:
   - `UseMauiApp<App>()`
   - `UseLibVLCSharp()` — cần cho `MediaPlayerElement`
   - Đăng ký handler Android: `VideoPlayerView` → `VideoPlayerViewHandler` (chỉ trong `#if ANDROID`)
   - Thêm fonts OpenSans
3. `App.CreateWindow()` → tạo `new Window(new AppShell())`
4. `AppShell` (Flyout) → đăng ký Route bằng `Routing.RegisterRoute(...)` cho các page camera (dùng khi `Navigation.PushAsync` cũng hoạt động vì các page không tạo trước cũng được).

> Lưu ý: các trang camera được mở bằng `Navigation.PushAsync(new CameraViewPage(...))` chứ **không dùng Shell navigation qua Route**. Các `RegisterRoute` trong `AppShell.xaml.cs` hiện chỉ mang tính khai báo.

---

## 4. Chức năng từng menu

### 4.1 Dashboard (trang chủ)
- File: `Dashboard.xaml(.cs)`
- Chức năng: chỉ hiển thị logo `dotnet_bot.png` + tên "Scannetwork". Chưa có nội dung thực tế.
- Không có logic trong code-behind.

### 4.2 Network — Kiểm tra thông tin mạng
- File: `Features/Network/NetworkPage.xaml(.cs)`
- Chức năng: nhấn nút **CHECK** → dò tìm và hiển thị thông tin (IP, Subnet, Gateway, DNS, trạng thái) của tối đa **3 loại kết nối**:

| Khung | Loại | Màu nền Frame |
|---|---|---|
| WIFI | WiFi | xanh lam `#E3F2FD` |
| MOBILE DATA | Cellular (mạng di động) | xanh lá `#E8F5E9` |
| USB-C / LAN | Ethernet (`eth`, `usb`, `rndis`, `lan`) | cam `#FFF3E0` |

- Luồng xử lý (`OnCheckClicked`):
  1. Dùng `Microsoft.Maui.Networking.Connectivity` lấy `NetworkAccess` + `ConnectionProfiles`.
  2. Kiểm tra Ethernet bằng `CheckEthernetAvailable()` (qua `ConnectivityManager`, Android 28+; có fallback theo tên interface).
  3. Nếu có loại kết nối → gọi `LoadNetworkDetails(type, access)` chạy trong `Task.Run`.
  4. Trong `LoadNetworkDetails`: dùng `ConnectivityManager.GetAllNetworks()` + `GetNetworkCapabilities()` + `GetLinkProperties()` để lấy IP/Subnet/Gateway/DNS.
  5. Nếu không lấy được IP (trường hợp một số máy Oppo USB-C LAN) → fallback `GetDetailsFromNetworkInterface()` (dùng `System.Net.NetworkInformation.NetworkInterface`).
  6. Cập nhật UI bằng `MainThread.BeginInvokeOnMainThread`.
- Hàm phụ trợ:
  - `PrefixLengthToSubnetMask(int)` — đổi prefix length sang subnet mask dạng `255.255.255.0`.
  - `GetStatusText(NetworkAccess)` — dịch trạng thái internet sang tiếng Việt.
  - WiFi (Android 31+): ghi thêm `Toc do: X Mbps | Tin hieu: Y dBm` từ `WifiManager.ConnectionInfo`.

### 4.3 Camera — Quét camera IP trong LAN + xem live
- File: `Features/Camera/CameraDiscoveryPage.xaml(.cs)` — **menu Camera hiện tại**.
- Logic quét đặt trong service: `Features/Camera/CameraDiscoveryService.cs` (static, pattern `VlcPlayerService`).
- **Mục đích**: tìm camera hư, camera quên IP, hoặc camera mới còn IP mặc định khi cắm **USB-LAN/Ethernet** hoặc cùng WiFi/Mobile — rồi xem live để kiểm tra/sửa.

#### Nút "SCAN LỚP MẠNG" / "SCAN UNKNOWN" (`OnScanSubnetClicked` / `OnScanUnknownClicked`)
1. Xoá danh sách cũ + **xoá bộ lọc cũ**, tạo `CancellationTokenSource`.
2. **CẢ 2 NÚT giờ cùng chạy 1 scan tổng hợp** `ScanInSubnetAsync(items, onProgress, token)`:
   - **Giai đoạn 1 — TCP-scan toàn subnet hiện tại**: non-blocking connect (semaphore 32 luồng) lên 9 cổng `8000, 554, 80, 443, 9008, 7681, 8080, 34567, 37777`. Device tìm thấy được **thêm vào UI realtime ngay lập tức** (không chờ scan xong toàn bộ) qua `MainThread.BeginInvokeOnMainThread` + fire sự kiện `CameraDiscoveryService.ListChanged`.
   - **Giai đoạn 2 — 4 phương pháp UDP song song** (bổ sung IP/MAC/vendor):
     - `SadpDiscoveryAsync` — SADP (Hikvision, UDP 37020)
     - `OnvifDiscoveryAsync` — ONVIF WS-Discovery (UDP 3702)
     - `ProvisionDiscoveryAsync` — UDP 23456: multicast `234.55.55.56/234.55.55.55` + **unicast sweep 253 IP** mỗi host
     - `SsdpDiscoveryAsync` — SSDP M-SEARCH (UPnP, UDP 1900); bộ lọc đã nới lỏng: nhận cả camera linux+UPnP (`hi3535/hi3536/HiSilicon`, `IPCam`, `Bamboo`)
   - Kết thúc: **merge** toàn bộ camera UI + kết quả UDP (không bao giờ đánh mất camera đã tìm thấy) → `NotifyListChanged`.
3. **Ô lọc IP** (`IpFilter`, SearchBar): nhập IP/MAC/Vendor/Name/Info → danh sách **tự động lọc realtime**. Khi không lọc, `ItemsSource` giữ nguyên `ObservableCollection` (update incremental — cuộn list không bị reset mất dòng).
4. **Danh sách + bộ lọc được cache static** (`_cachedItems` / `_cachedFilter`): Shell tạo lại page khi quay lại sau khi xem preview → danh sách vẫn còn nguyên.
5. Nút **STOP** (`OnStopClicked`): `_cts.Cancel()` — dừng ngay lập tức.

#### Nút "XEM LIVE (Nhap IP)" (`OnPreviewClicked`)
- Mở `CameraPreviewPage` nhập IP tay — dùng để **test live view** với IP bất kỳ trước khi áp dụng vào camera quét được.

#### Nút "View" trên mỗi thiết bị tìm thấy (`OnViewClicked`)
- Mở `CameraViewPage(device)` — xem live RTSP (VLC/VideoView) của **đúng camera vừa quét**, đã tự điền IP.

#### Model: `CameraDevice.cs`
```csharp
public string Ip        // địa chỉ IP
public string Mac        // địa chỉ MAC (từ ARP)
public string Vendor     // Hikvision | Provision | ONVIF | ""
public string Name       // hiện chỉ = Ip
public string Info       // "Port mo: 554, 8000" — các cổng camera đang mở
```

### 4.4 Camera — Xem live RTSP (2 trang gần giống nhau)

>Cả 2 trang `CameraPreviewPage` (nhập tay) và `CameraViewPage` (từ kết quả scan) **gần như giống hệt** về logic phát video. Nếu tái cấu trúc, nên gộp lại 1 trang + truyền IP.

#### Chọn hãng (`OnVendorClicked` / `SetVendor`)
- **Hikvision** → Port `8000`, User `admin`, Pass để trống.
- **Provision** → Port `80`, User `admin`, Pass `123456`.
- Hiện hướng dẫn mật khẩu/thông tin RTSP mặc định ở `LblDefaultPass`.

#### Engine phát video (`PckEngine`)
- **VLC (mặc định)**: dùng `LibVLCSharp.MAUI.MediaPlayerElement` (`vlc:MediaPlayerElement`).
- **Hệ thống (VideoView)**: dùng control custom `local:VideoPlayerView` (bọc `Android.Widget.VideoView`).

#### Nút kết nối (`OnConnectClicked`)
1. Validate IP/Port/User.
2. Build danh sách **candidates RTSP** theo hãng:
   - Hikvision: `rtsp://user:pass@ip:554|8000/Streaming/Channels/101` và `/Streaming/Channels/1`.
   - Provision: port `554,544` × profile `profile1/2/3`; port `554,8554,8000` × đường dẫn `/live/ch0`, `/live/ch0_0`, `/ch0`, `/Streaming/Channels/101`.
   - Credential được `WebUtility.UrlEncode` trước khi ghép.
3. Lần lượt thử từng URL bằng engine đang chọn; URL đầu tiên phát được thì dừng.
   - **VLC**: tạo player qua `VlcPlayerService.CreatePlayer()`, gắn vào `VlcView.MediaPlayer`, chờ event `Playing`/`EncounteredError`, timeout 10s. Options: `:network-caching=1000`, `:rtsp-tcp`.
   - **Hệ thống**: set `LiveView.Source = rtsp`, `IsPlaying = true`, chờ event `PlaybackStarted`/`PlaybackFailed` từ handler, timeout 8s.
4. Thành công → báo "Dang phat RTSP live..."; thất bại → báo lỗi.

#### `VideoPlayerView.cs` — control custom
- `View` thuần C# với 2 BindableProperty: `Source (string)` + `IsPlaying (bool)`.
- Source change → raise `SourceChanged`.
- 3 event thông báo từ handler: `PlaybackStarted`, `PlaybackFailed`, `PlaybackCompleted`.
- Phương thức `NotifyPlaybackStarted()/Failed()/Completed()` để native handler gọi lên.

#### `Platforms/Android/VideoPlayerViewHandler.cs`
- `ViewHandler<VideoPlayerView, AndroidVideoView>`.
- `CreatePlatformView`: tạo `AndroidVideoView`, đăng ký `SetOnPreparedListener`/`SetOnErrorListener`/`SetOnCompletionListener`.
- `MapSource`: `SetVideoURI(...)` rồi `Start()`. `MapIsPlaying`: `Start()`/`Pause()`.
- Listeners gọi lại `VirtualView.Notify...` trên main thread.

#### `VlcPlayerService.cs`
```csharp
LibVLC LibVLC   // Singleton (lazy). Init Core.Initialize() + options:
                //   --network-caching=1000, --rtsp-tcp, --no-audio, --clock-jitter=0, --avio-threads=4
CreatePlayer()  // new MediaPlayer(LibVLC)
```
> Dùng chung 1 `LibVLC` instance cho toàn app, mỗi lần chơi tạo `MediaPlayer` mới.

### 4.5 Diagnostics — Ping IP (real-time)
- File: `Features/Diagnostics/DiagnosticsPage.xaml(.cs)`
- Chức năng: ping một địa chỉ IPv4 theo **2 chế độ**, mỗi kết quả **hiện ra ngay khi trả về** (không chờ hết 4 gói):
  - **Ping 4 gói tin (mặc định)** `PckMode.SelectedIndex == 0`: gửi 4 gói rồi dừng, mỗi gói hiện ngay.
  - **Ping liên tục (-t)** `PckMode.SelectedIndex == 1`: lặp vô hạn cho tới khi bấm STOP.
- UI: ô nhập `TxtIp` (validate `xxx.xxx.xxx.xxx` = IPv4), `Picker` chọn chế độ, nút **PING** / **STOP** / **XOI KET QUA**, vùng kết quả dạng Label `monospace` trong `ScrollView` (tự cuộn xuống dòng mới, tự giới hạn tối đa 800 dòng).
- Engine: dùng `System.Net.NetworkInformation.Ping` (không cần shell).
  - `DoSinglePingAsync`: gửi 32 bytes, timeout 3s, TTL 64, `PingOptions(64, true)`. Dùng `SendPingAsync(IPAddress, TimeSpan, byte[], PingOptions, CancellationToken)` — **phải truyền kiểu `IPAddress` + `TimeSpan`** (overload nhận `string` + `int` không bind được trên Android).
  - Đếm `_sent` / `_received` để in thống kê `"--- Thong ke: ... mat X (Y%) ---"` mỗi lần kết thúc.
- **Nút STOP dừng ngay** (`OnStopClicked`): gọi `_cts?.Cancel()` → gói ping đang chờ bị `OperationCanceledException` (bỏ qua im lặng), vòng lặp kiểm tra token nên thoát tức thì.
- Trạng thái UI dùng `SetRunning(bool)`: khoá ô nhập/picker/nút PING, bật nút STOP khi đang chạy.

### 4.6 Nmap — CHƯA THỰC HIỆN ⚠️
- File: `Features/Nmap/NmapPage.xaml(.cs)`
- Hiện chỉ là `ContentPage` trống với tiêu đề "Nmap".
- **Kế hoạch (khi code tiếp)**: menu này dự định làm quét cổng / quét dịch vụ kiểu Nmap trong LAN. Chưa có code, chưa có package Nmap nào được cài.

---

## 5. Cấu hình quan trọng

### Android permissions (`Platforms/Android/AndroidManifest.xml`)
```xml
ACCESS_NETWORK_STATE   // đọc thông tin mạng
ACCESS_WIFI_STATE      // đọc WiFi (RSSI, LinkSpeed)
ACCESS_FINE_LOCATION   // cần cho quét WiFi LAN trên Android (9+ thường yêu cầu)
ACCESS_COARSE_LOCATION
INTERNET
```
- `android:usesCleartextTraffic="true"` trong `<application>` — **bắt buộc** để gọi HTTP (fingerprint) với URL `http://`.

> ⚠️ Android 9+ với target SDK hiện tại: quét camera qua WiFi yêu cầu có quyền **Location** runtime. Hiện **không có code xin quyền runtime** — cần thêm (dùng `Permissions.RequestAsync<Permissions.LocationWhenInUse>()`) trước khi scan nếu deploy trên Android 9+.

### Cấu hình APK (`MauiScannetwork.csproj`)
- `EmbedAssembliesIntoApk=true` và `AndroidFastDeploymentType=None` → build APK đầy đủ để cài trực tiếp (sideload), không cần deploy qua IDE.

### LibVLC
- Bắt buộc `builder.UseLibVLCSharp()` trong `MauiProgram`.
- Gói `VideoLAN.LibVLC.Android` cung cấp native `libVLC` cho Android, `LibVLCSharp` tự tìm.
- Nếu phát lại nhiều lần mà treo/đơ, kiểm tra tái sử dụng `LibVLC` singleton.

---

## 6. TRẠNG THÁI DEBUG ĐANG DỞ (ghi ngày 08/09/2026) — ĐỌC TRƯỚC KHI CODE TIẾP ⚠️

> Phần này ghi lịch sử fix + hiện trạng để lần sau mở project là biết ngay đang ở đâu.

### 6.1. Thông tin camera thực tế của khách (MẤU CHỐT — user cung cấp)

Lớp mạng `192.168.64.x` chỉ có **camera Provision-ISR**, dùng **IP Manager của Provision** (KHÔNG phải Hikvision/SADP).
Có **2 loại camera**:

| Loại | HTTP Port | Data Port | RTSP Port | Thêm |
|---|---|---|---|---|
| **Cũ** | 80 | **9008** | 554 | — |
| **Mới** | 80 | **9008** | 554 | **8080** (Log Polling), **7681** (WebSocket) |

- Camera mới KHÔNG khác port 554, NHƯNG khi xem bằng VLC báo lỗi (chi tiết §6.5).
- User cho biết máy dev này có thể ping/connect thẳng tới camera `192.168.64.x` (đã test thật với `.108:554`).

### 6.2. Cơ chế discovery của IP Manager (đã reverse-engineer từ `IPTool_Search.dll`)

- IP Manager **KHÔNG probe kiểu scanner** — nó **bind UDP cổng 23456 (Provision) + 3702 (ONVIF) + 1900 (SSDP) và LẮNG NGHE announcement** thiết bị tự phát (push model), chạy **4 bộ tìm song song**:
  1. **CDevSearch** — proprietary Provision: gói **binary** có "magic number", multicast `239.255.255.250`, response dạng XML chứa `multicastSearchResult/port`, `productInfo`, `tcpIp/ipAddr`, `devName`.
  2. **COnvifDevSearch** — `soap.udp://239.255.255.250:3702` (ONVIF WS-Discovery Probe).
  3. **CUPNPSearch** — SSDP `M-SEARCH * HTTP/1.1` với `MAN:"ssdp:discover"` tới `HOST:239.255.255.250:1900` (UPnP). Camera trả về `LOCATION` (device description XML), `SERVER`, `USN`.
  4. **CDevSearch (passive listen)** — cũng bind port 23456 để nghe camera tự broadcast announcement (push model).
- **Đã xác nhận bằng log**: camera Provision trong mạng này KHÔNG tự phát announcement định kỳ → chỉ phản hồi đúng probe magic. Chưa lấy được magic (không có disassembler/wireshark).
- **Trong app hiện tại**: `CameraDiscoveryService` đã triển khai đủ 4 phương pháp:
  - `SadpDiscoveryAsync` → SADP (Hikvision, UDP 37020)
  - `OnvifDiscoveryAsync` → ONVIF WS-Discovery (UDP 3702)
  - `ProvisionDiscoveryAsync` → Provision listen (UDP 23456) + text probe
  - `SsdpDiscoveryAsync` → SSDP M-SEARCH (UPnP, UDP 1900) — **đã thêm mới**

#### Kết quả reverse-engineer IPTool_Search.dll (quan trọng — đọc khi quay lại)

- **IP Manager bind duy nhất UDP 23456** (trên cả 2 NIC: 192.168.64.222 + 192.168.68.104). Port 3702/1900 là của Windows services khác, KHÔNG phải IP Manager.
- **CDevSearch dùng multicast `234.55.55.56` (send) + `234.55.55.55` (recv)** trên port 23456. KHÔNG dùng `239.255.255.250` cho Provision.
- **COnvifDevSearch** gửi ONVIF Probe đến `soap.udp://239.255.255.250:3702`, dùng port ephemeral (52007/52008) không bind 3702.
- **CUPNPSearch** gửi M-SEARCH đến `239.255.255.250:1900`, dùng port ephemeral.
- **CDevSearch::handleMessage** xử lý gói tin nhận được, kiểm tra "message length too small" → có kích thước tối thiểu.
- **MẸO ĐÃ XÁC MINH**: các máy thật (camera Provision) trong mạng này **KHÔNG phản hồi** probe binary `MHED` khi replicate (đã thử từ laptop): gửi multicast `234.55.55.56` hoặc unicast từng IP đều trả 0 response. Điểm chết → không đầu tư thêm vào unicast/multicast MHED.
- **Probe CDevSearch đã bắt được (140 byte)**: bắt đầu bằng `4D 48 45 44 0B 00 01 00 01 00` ("MHED") + các byte zero phía sau. Field thứ 6 là cmd (`0x0B` từ `.222:52007`, `0x08` từ `.221:48292`).
- **XML response format** từ camera Provision:
  ```xml
  <multicastSearchResult>
    <tcpIp><ip/><mask/><route/></tcpIp>
    <port><httpType/><rtspPort/></port>
    <productInfo><unit/><softwareVer/><devName/><softBuildDate/><kernelVer/><customerSN/></productInfo>
  </multicastSearchResult>
  ```
- **searchSetIP XML** chứa: `ipAddr`, `maskAddr`, `route`, `dataPort`, `httpPort`, `dhcpSwitch`, `dns1`, `dns2`, `devtype`, `software`
- **Version format**: `%d.%d.%d.%d.%d.beta%d` (ví dụ: `1.3.5`) — có thể là header binary của CDevSearch probe.
- **MAC format**: `%02x:%02x:%02x:%02x:%02x:%02x` (lowercase hex, colon-separated).

### 6.3. Các BUG ĐÃ FIX (quan trọng, tránh hồi quy)

1. **TCP-scan treo vô hạn → scan không hiện kết quả** (fix xong, đã deploy):
   - *Nguyên nhân*: `Socket.ConnectAsync(host, port, cts.Token)` trên Android **không hủy connect thật** khi dùng CancellationToken (chỉ hủy phần DNS). Connect tới IP bị firewall drop treo tới kernel timeout (phút) → kẹt `Task.WhenAll` trong `ScanInSubnetAsync` → UI không bao giờ update → "không thấy gì".
   - *Fix*: dùng `Task.WhenAny(connectTask, Task.Delay(timeout))` + `TcpClient`, swallow task bỏ đi (pattern chuẩn như `RtspProbeService.IsPortOpenAsync`). 
2. **`UdpClient.ReceiveAsync(token)` treo vô hạn** trong SADP/Provision/ONVIF listening (Android không tôn trọng token khi không có gói tới) → thêm helper `ReceiveWithTimeoutAsync(listener, timeoutMs, token)` dùng `Task.WhenAny(recv, Task.Delay)`, trả `UdpReceiveResult?`. Đã thay cả 3 hàm `SadpDiscoveryAsync`, `ProvisionDiscoveryAsync`, `OnvifDiscoveryAsync`.
3. **`ResolveMacAsync` chạy cho cả 254 host kể cả host đóng port** (gọi shell bị SELinux chặn, tốn thời gian) → chuyển sau khi `openPorts.Count == 0 return null`.
4. **Scan xong list chỉ còn ~8 camera** (đã fix): *nguyên nhân* — bộ lọc IP còn sót lại trong static cache `_cachedFilter` + bước final sync làm `items.Clear()` rồi thay bằng kết quả UDP (nhỏ). *Fix* — xoá bộ lọc khi bắt đầu scan mới, và final sync **merge** camera UI + kết quả UDP (không clear thay thế).
5. **List biến mất khi quay lại từ màn hình preview** (đã fix): Shell tạo **instance mới** của `CameraDiscoveryPage` mỗi khi tab được hiện lại → `ObservableCollection` mới rỗng. *Fix* — cache static `_cachedItems`/`_cachedFilter`, `OnAppearing` khôi phục danh sách + bộ lọc.
6. **List reset còn ít dòng khi cuộn trong lúc scan đang chạy** (đã fix): trước đây `ApplyFilter` gán lại `ItemsSource = new List(...)` mỗi lần có camera mới → CollectionView Android rebind toàn bộ → mất dòng khi cuộn. *Fix* — khi **không lọc**: giữ nguyên `ItemsSource = _items` (ObservableCollection update incremental); chỉ tạo collection lọc riêng khi filter hoạt động.
7. **Refresh list khi page bị Shell tạo lại giữa chừng scan** (đã fix): sự kiện update trỏ vào instance cũ → instance mới "kẹt" ở snapshot cũ. *Fix* — `CameraDiscoveryService.ListChanged` (static event); page subscribe trong `OnAppearing`, unsubscribe `OnDisappearing`.
8. **Preview nhiều lần làm máy nặng / rò rỉ native VLC** (đã fix): `MediaPlayer` + `Media` của libVLC không bao giờ Dispose khi thoát trang → tài nguyên tích lũy. *Fix* — `StopPlayback()` của `CameraViewPage` & `CameraPreviewPage` giờ Stop + detach `MediaPlayerElement` + `Dispose()` player & media; thêm `OnDisappearing` → tự dọn khi thoát; `finally` dùng biến local `player` tránh NullReference sau khi dispose.

### 6.4. Danh sách port scan HIỆN TẠI (`ProbeTcpAsync`)

Đã đổi theo camera Provision thật (KHÔNG còn 8554/9000 mặc định Hikvision):
`8000, 554, 80, 443, 9008, 7681, 8080, 34567, 37777`

- Nhận diện vendor: nếu mở `9008` hoặc `7681` → gán **"Provision"** (trước cả `VendorFromMac`).

### 6.4b. Danh sách UDP port discovery HIỆN TẠI

| Port | Giao thức | Ghi chú |
|---|---|---|
| `37020` | SADP (Hikvision) | Broadcast + multicast `239.255.255.250` |
| `3702` | ONVIF WS-Discovery | `soap.udp://239.255.255.250:3702` |
| `23456` | Provision (CDevSearch) | **Multicast `234.55.55.56` (send) + `234.55.55.55` (recv)** — KHÔNG phải `239.255.255.250` |
| `1900` | SSDP/UPnP | `M-SEARCH *` → multicast `239.255.255.250:1900` |

> **Lưu ý**: IP Manager chỉ bind port `23456` (trên cả 2 NIC). Port `3702`/`1900` là của Windows services khác.

### 6.5. 💢 VẤN ĐỀ ĐANG BLOCK: camera Loại MỚI không xem RTSP được (VLC)

**Triệu chứng** (VLC nhập URL `rtsp://admin:123456@192.168.64.108:554/Streaming/Channels/101`):
- TCP 554 **MỞ**, `OPTIONS` → **`RTSP/1.0 200 OK`** (`Server: Customer RTSP Server/1.0.0`).
- `DESCRIBE` → **`RTSP/1.0 401 Unauthorized`** (WWW-Authenticate: `Digest realm="RTSP SERVER", nonce="...", stale="FALSE"` — không có `qop`, algorithm mặc định MD5).
- VLC: `authentication failed` → `Failed to setup RTSP session` → `Your input can't be opened`.

**Đã test trực tiếp từ máy dev (PowerShell) lên `192.168.64.108` — TẤT CẢ 401**:
- Basic auth `admin:123456` → 401
- Digest MD5 `admin:123456` → 401
- Digest MD5 + qop, MD5-sess → 401
- Nhiều combo user/pass: `admin`, `root`, `123456`, `888888`, 111111, rỗng... → 401
- ⚠️ Sau khi thử nhiều lần, camera bắt đầu trả **lỗi kết nối "LOI"** (khả năng brute-force lockout tạm thời) — chờ ~60s rồi thử lại thì `OPTIONS`/`DESCRIBE` phản hồi bình thường trở lại.

**Kết luận tạm thời**: chưa tìm được user/pass hợp lệ cho camera loại mới. Nghi vấn:
- Hoặc pass camera loại mới **khác** `123456` (user khẳng định đúng nhưng VLC vẫn fail → cần xác nhận lại/web IP Manager).
- Hoặc camera loại mới **yêu cầu user RTSP riêng** (không phải `admin`).
- Hoặc cần handshake Digest khác (realm "RTSP SERVER" — MD5 chuẩn đã thử, vẫn 401).

**Còn phải làm khi tiếp tục**:
1. Xác nhận user/pass đúng của camera loại mới (xem trong IP Manager trên PC khách).
2. Hoặc bắt gói (nhờ user chạy Wireshark trên PC IP Manager trong cụm switch) để có capture Digest auth chuẩn.
3. Khi có pass đúng → tìm path RTSP (thử `/`, `/Streaming/Channels/101`, `/Streaming/Channels/1`, `/live`, `/cam/realmonitor?...`).
4. Camera loại mới có thể phát stream qua **WebSocket 7681** (khác RTSP) — nếu RTSP không xem được, nghiên cứu stream qua 7681/8080.

### 6.6. Trạng thái hiện tại của app

- Build **Release** OK, deploy lên `192.168.64.251:5555` thành công (từ 08/09/2026 có thể build bằng Visual Studio + hot reload).
- **Cả 2 nút SCAN LỚP MẠNG / SCAN UNKNOWN** cùng chạy scan tổng hợp `ScanInSubnetAsync`: TCP-scan subnet + 4 phương pháp UDP (SADP/ONVIF/Provision/SSDP).
- TCP-scan **tìm thấy 100+ camera** trong lớp `192.168.64.x` (dhk); UI update **realtime** khi tìm thấy từng camera.
- **SSDP** là nguồn bổ sung tốt: camera Provision phản hồi SSDP M-SEARCH (server `Linux/3.0.8, UPnP/1.0, Portable SDK for UPnP devices/1.6.6`) — bộ lọc SSDP đã nới để nhận các camera này.
- Provision MHED probe multicast/unicast: **0 response** từ camera (kể cả replicate từ laptop) → coi là ngõ cụt, không mở rộng thêm.
- Ô lọc IP (SearchBar) + static cache danh sách + sự kiện `ListChanged` cập nhật giữa chừng scan — mô tả chi tiết ở §6.3.
- VLC preview: đã fix rò rỉ native player (Dispose trên `OnDisappearing`).

#### Kế hoạch ngày 09/09/2026 — TEST TIẾP PHẦN HIKVISION ⬅️
1. Hôm nay (08/09) đã tập trung hoàn thiện phần **Provision** (scan + list + filter + preview). Ngày mai chuyển sang **test Hikvision**.
2. Trong mạng hiện tại camera gần như toàn Provision; Hikvision có thiết bị `.97` (NVR/switch) phản hồi SADP + ONVIF — dùng làm đối tượng test.
3. Việc cần xác nhận: xem live Hikvision qua RTSP (`/Streaming/Channels/101`, port 554/8000) — user/pass mặc định admin + rỗng/đã-đặt; SADP port 37020 đã có sẵn trong `SadpDiscoveryAsync`.
4. Nếu cần: nới thêm parser SADP để lấy đúng `serialNO/IP/MAC` của Hikvision khi quét.

### 6.7. Lệnh deploy / kiểm tra nhanh (khi quay lại)

```powershell
# Build Release
dotnet build -f net9.0-android -c Release

# Deploy lên điện thoại (adb wifi)
adb -s 192.168.64.251:5555 install -r -d bin\Release\net9.0-android\com.companyname.mauiscannetwork-Signed.apk

# Xem log scan
adb -s 192.168.64.251:5555 logcat -c
adb logcat -s CameraDiscovery
```

---

## 7. Hướng dẫn code tiếp (checklist nhanh)

Muốn code tiếp, làm theo:

1. **Thêm menu mới** trong `AppShell.xaml`: thêm 1 `<FlyoutItem>` + `ShellContent` trỏ tới page trong `Features/<TenFeature>/`.
2. **Thêm page mới**: tạo trong `Features/<Tên feature>/`, thường gồm `.xaml` + `.xaml.cs`. Nếu muốn push bằng `Navigation.PushAsync` thì không cần đăng ký Route.
3. **Không dùng MVVM**: code hiện tại viết thẳng trong code-behind (đơn giản, nhanh). Nếu muốn đổi sang MVVM ghi chú lại việc này.
4. **Scan mạng**: tham khảo `CameraPage.ScanLanAsync` (semaphore song song + TCP probe + ARP) hoặc `NetworkPage` (đọc native Android).
5. **Xem live camera**: copy mẫu từ `CameraViewPage` (2 engine VLC + VideoView, danh sách RTSP candidates).
6. **Cấu hình file**: nhớ cập nhật `MauiScannetwork.csproj` nếu thêm package.
7. Chạy thử trên Android, kiểm tra quyền Location runtime khi scan WiFi.

### Các việc nên làm tiếp (todo gợi ý)
- [x] Menu **Diagnostics**: ping 4 gói / ping -t real-time, nút STOP dừng ngay.
- [x] Menu **Camera** mới: quét LAN realtime (WiFi/Mobile/USB-LAN) + xem live từng camera + test nhập IP tay (nút STOP dừng scan ngay).
- [ ] Xin quyền runtime (Location) trước khi scan WiFi trên Android 9+.
- [ ] Hoàn thiện menu **Nmap** (quét port/subnet, kết quả theo bảng).
- [ ] Thay icon menu `dotnet_bot.png` bằng icon thật.
- [ ] Gộp `CameraPreviewPage` và `CameraViewPage` để tránh trùng code.
- [ ] Xoá `CameraPage.xaml(.cs)` legacy (không còn trên menu) sau khi chắc chắn camera scan mới ổn.
- [ ] Chuyển UI text sang tiếng Anh/đa ngôn ngữ nếu cần publish.

---

## 8. Lịch sử & ghi chú quan trọng

- Luồng build Android: `net9.0-android`, min SDK 21. Chưa build/test platform khác.
- `connectivity` trong NetworkPage: nếu `NetworkAccess == Unknown` → báo không tìm thấy mạng.
- Khi scan camera, mỗi subnet phổ biến luôn được quét dù máy không ở subnet đó (hữu ích khi camera ở mạng khác với sóng điện thoại).
- Đường dẫn RTSP mặc định được thử nhiều biến thể; nếu camera lạ, thêm paths mới vào `BuildHikRtspUrls`/`BuildProvRtspUrls`.