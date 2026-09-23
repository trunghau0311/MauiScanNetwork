# MauiScannetwork

Ứng dụng **.NET MAUI** (chỉ build cho Android) dùng để kiểm tra thông tin mạng LAN trên điện thoại: IP, Subnet, Gateway, DNS, trạng thái của kết nối WiFi / Mobile Data / USB-C LAN.

> Tài liệu này ghi lại cấu trúc code và chức năng menu để khi cần code tiếp chỉ cần đọc lại.

---

## 1. Thông tin chung

| Mục | Giá trị |
|---|---|
| Target Framework | `net9.0-android` |
| Min Android version | SDK 21 (Android 5.0) |
| Root namespace | `MauiScannetwork` |
| ApplicationId | `com.companyname.mauiscannetwork` |
| Giao diện | Shell + 1 trang Network (không Flyout) |
| Ngôn ngữ UI | Tiếng Việt (chưa có i18n/Localization) |
| Background task | Toàn bộ dùng `async/await` trực tiếp trong code-behind, **chưa dùng MVVM / DependencyInjection / MVVM Toolkit** |

### Thư viện nuget đã cài (xem `MauiScannetwork.csproj`)
- `Microsoft.Maui.Controls` `$(MauiVersion)`
- `Microsoft.Extensions.Logging.Debug` 9.0.9

### Cách chạy
- Mở bằng Visual Studio 2022+ (có workload .NET MAUI / Android).
- Build/deploy target: **Android** (chỉ config được `net9.0-android`).
- Đã cấu hình `EmbedAssembliesIntoApk=true` + `AndroidFastDeploymentType=None` để APK sideload được bình thường.
- SDK: build bằng `dotnet` CLI thì phải dùng SDK **9.0.x** (đã pin `9.0.318` trong `global.json` — SDK 10 mặc định sẽ lỗi NU1102 khi restore).

---

## 2. Cấu trúc thư mục

```
MauiScannetwork/
├── MauiProgram.cs                  # Khởi động app: đăng ký fonts
├── App.xaml / App.xaml.cs          # Application root, merge Styles/Colors
├── AppShell.xaml / .xaml.cs        # Shell 1 trang Network + đăng ký Route
├── MauiScannetwork.csproj          # Cấu hình project, packages
│
├── Features/
│   └── Network/
│       ├── NetworkPage.xaml        # Kiểm tra thông tin mạng
│       └── NetworkPage.xaml.cs
│
├── Platforms/
│   └── Android/
│       ├── AndroidManifest.xml     # Permissions mạng/wifi, cleartext traffic
│       ├── MainActivity.cs         # Entry Activity Android
│       └── MainApplication.cs
│
└── Resources/
    ├── Styles/Colors.xaml          # Bảng màu
    ├── Styles/Styles.xaml          # Style chung
    ├── Images/dotnet_bot.png       # Icon (tạm thời)
    ├── AppIcon/ Splash/ Fonts/ Raw/
```

---

## 3. Luồng khởi động

1. `MainActivity` (Android) → `MainApplication.CreateMauiApp()` → `MauiProgram.CreateMauiApp()`
2. `MauiProgram`: `UseMauiApp<App>()` + thêm fonts OpenSans.
3. `App.CreateWindow()` → tạo `new Window(new AppShell())`.
4. `AppShell` hiển thị `NetworkPage` (ShellContent trực tiếp, không qua Flyout).

---

## 4. Chức năng menu

### Network — Kiểm tra thông tin mạng
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

---

## 5. Cấu hình quan trọng

### Android permissions (`Platforms/Android/AndroidManifest.xml`)
```xml
ACCESS_NETWORK_STATE   // đọc thông tin mạng
ACCESS_WIFI_STATE      // đọc WiFi (RSSI, LinkSpeed)
INTERNET
```
- `android:usesCleartextTraffic="true"` trong `<application>`.

### Cấu hình APK (`MauiScannetwork.csproj`)
- `EmbedAssembliesIntoApk=true` và `AndroidFastDeploymentType=None` → build APK đầy đủ để cài trực tiếp (sideload), không cần deploy qua IDE.

---

## 6. Trạng thái debug

### [23/09/2026] Thu gọn app — chỉ còn Network
- Đã **xoá hoàn toàn** các menu và feature `Camera`, `Diagnostics`, `Nmap`, `Dashboard` như yêu cầu:
  - Xoá thư mục `Features/Camera`, `Features/Nmap`, `Features/Diagnostics`, và `Dashboard.xaml(.cs)`.
  - Gỡ khỏi `AppShell.xaml(.cs)`: FlyoutItems + route + using.
  - `MauiProgram.cs`: bỏ `UseLibVLCSharp()` và handler `VideoPlayerView`, xoá `Platforms/Android/VideoPlayerViewHandler.cs`.
  - `MauiScannetwork.csproj`: gỡ package `LibVLCSharp`, `LibVLCSharp.MAUI`, `VideoLAN.LibVLC.Android`.
  - `AndroidManifest.xml`: bỏ các permission chỉ phục vụ camera (`CHANGE_WIFI_MULTICAST_STATE`, `ACCESS_FINE/COARSE_LOCATION`), CLDR icon vào Resource đã bỏ.
  - App mở thẳng vào trang **Network** (Shell không còn Flyout).
- **Đã build Release thành công — 0 lỗi.** APK: `bin\Release\net9.0-android\com.companyname.mauiscannetwork-Signed.apk`.

### Lịch sử ghi chú trước đó (đã xoá phần feature)
- Toàn bộ nội dung hướng dẫn/ghi chú cho Camera/Diagnostics/Nmap (quét camera, RTSP, LibVLC, IP Manager, SADP/ONVIF/Provision/SSDP, ...) đã được gỡ cùng code — không còn áp dụng cho app hiện tại.
- Ghi chú về build/deploy:
  - `dotnet build -f net9.0-android -c Release` (phải dùng SDK 9.0.x, đã pin `9.0.318`).
  - Deploy: `adb -s <ip>:<port> install -r -d bin\Release\net9.0-android\com.companyname.mauiscannetwork-Signed.apk`.

---

## 7. Hướng dẫn code tiếp (checklist nhanh)

1. **Thêm menu mới**: thêm `<FlyoutItem>` + `ShellContent` trong `AppShell.xaml` (đổi `FlyoutBehavior="Flyout"`) trỏ tới page trong `Features/<TenFeature>/`.
2. **Thêm page mới**: tạo trong `Features/<Tên feature>/`, gồm `.xaml` + `.xaml.cs`. Nếu push bằng `Navigation.PushAsync` thì không cần đăng ký Route.
3. **Không dùng MVVM**: code hiện tại viết thẳng trong code-behind (đơn giản, nhanh).
4. **Đọc thông tin mạng**: tham khảo `NetworkPage` (đọc native Android `ConnectivityManager`/`WifiManager`).
5. **Cấu hình file**: nhớ cập nhật `MauiScannetwork.csproj` nếu thêm package.
6. Chạy thử trên Android thật để kiểm tra `CHECK` hiển thị đủ 3 loại kết nối (WiFi / Mobile / USB-LAN).