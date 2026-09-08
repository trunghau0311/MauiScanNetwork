using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MauiScannetwork.Features.Camera;

/// <summary>
/// Quet camera LAN voi 2 che do:
///  1. ScanInSubnetAsync  - TCP scan subnet dang duoc cap IP (nhanh, trong lop mang hien tai)
///  2. ScanUnknownAsync   - SADP-style: UDP multicast + broadcast de tim camera khac subnet / trung IP
/// Giong cach lam cua Hikvision SADP tool va Provision IP Manager.
/// </summary>
public static class CameraDiscoveryService
{
    // Phat ra moi khi danh sach thay doi trong luc scan -> page dang hien thi nao
    // cung co the cap nhat lai (ke ca khi page bi Shell tao moi giua chung scan).
    public static event Action? ListChanged;

    private static void NotifyListChanged()
    {
        try { ListChanged?.Invoke(); }
        catch { }
    }
    private const string MulticastSadp = "239.255.255.250";   // Hikvision SADP + ONVIF multicast
    private const string ProvisionMulticastSend = "234.55.55.56"; // Provision-ISR CDevSearch send multicast
    private const string ProvisionMulticastRecv = "234.55.55.55"; // Provision-ISR CDevSearch recv multicast
    private const int SadpPort = 37020;                        // Hikvision SADP port
    private const int OnvifPort = 3702;                        // ONVIF WS-Discovery port
    private const int ProvisionPort = 23456;                   // Provision-ISR IP Manager discovery port
    private const int SsdpPort = 1900;                          // SSDP/UPnP (IP Manager CUPNPSearch)

    private static readonly object _lock = new();

    // Dung chung 1 HttpClient (trach tao hang tram handler native -> segfault luc scan)
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1.2) };

    // MAC OUI -> vendor (tra ve vendor neu la camera IP, else null)
    private static string? VendorFromMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        mac = mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        if (mac.Length < 6) return null;

        var oui = mac.Substring(0, 6);
        switch (oui)
        {
            case "74F8DB": return "Provision-ISR";
            case "00A0E2": return "Provision";   // da cu
            case "0018AE": return "Hikvision";
            case "C056E3": return "Hikvision";
            case "44F459": return "Hikvision";
            case "B4A3BD": return "Dahua";
            case "A0E025": return "Dahua";
            case "3CEF8C": return "Dahua";
            case "E0C767": return "Dahua";
            case "9CAFCA": return "Dahua";
            case "ACCC8E": return "Dahua";
            case "C0C9E3": return "Dahua";
            case "00E04C": return "Zhejiang Uniview";
            case "7431BF": return "Uniview";
            case "ACE9B2": return "Uniview";
            case "50816E": return "Uniview";
            case "00295B": return "Vivotek";
            case "0007F6": return "Vivotek";
            case "0002D1": return "Axis";
            case "ACCC3A": return "Axis";
            case "000B59": return "Hangzhou Tiandy";
            case "2846E6": return "Tiandy";
            case "08B78A": return "OPPO/IP camera";
            case "0C6E4F": return "Sunell";
            case "E03FD5": return "Sunell";
            default: return null;
        }
    }

    // Tra ve true neu OUI la cua nha san xuat camera IP
    private static bool IsCameraOui(string? mac) => VendorFromMac(mac) != null;

    // Log ra logcat (tag mono-stdout) de debug tren thiet bi
    private static void Log(string msg)
    {
        try
        {
            Console.WriteLine($"[Discovery] {msg}");
            Android.Util.Log.Info("CameraDiscovery", msg);
        }
        catch { }
    }

    // ────────────────────────────────────────────────────────────────
    // 1. SCAN TRONG LỚP MẠNG (TCP scan subnet hien tai)
    // ────────────────────────────────────────────────────────────────
    public static async Task ScanInSubnetAsync(
        ObservableCollection<CameraDevice> items,
        Action<string>? onProgress,
        CancellationToken token)
    {
        var subnet = GetCurrentSubnetBase(out var hostIp, out var mask);
        var baseIp = subnet ?? "192.168.1.";
        Log($"ScanInSubnet: subnet={subnet}, host={hostIp}, mask={mask}");
        onProgress?.Invoke($"Dang quet lop mang {baseIp}1-254 (IP may: {hostIp ?? "?"})...");

        // TCP-scan toan subnet bang non-blocking connect (mỗi port toi da ~250ms -> nhanh, khong ket)
        var found = new Dictionary<string, CameraDevice>();
        int scanned = 0;
        var maxParallel = 32;
        using var sem = new SemaphoreSlim(maxParallel);
        Log($"ScanInSubnet: bat dau TCP-scan 254 IP, maxParallel={maxParallel}");

        var tasks = Enumerable.Range(1, 254).Select(i => Task.Run(async () =>
        {
            await sem.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return;
                var host = $"{baseIp}{i}";
                var dev = await ProbeTcpAsync(host, token);
                if (dev != null)
                {
                    Log($"TCP-SCAN: tim thay {host} -> {dev.Vendor} ports=[{dev.Info}]");
                    lock (_lock)
                    {
                        var key = string.IsNullOrEmpty(dev.Mac) ? host : dev.Mac;
                        found[key] = dev;
                    }
                    // Cap nhat UI realtime ngay khi tim thay (khong cho den khi scan xong toan bo)
                    var foundDev = dev;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!items.Any(x => x.Ip == foundDev.Ip))
                            items.Add(foundDev);
                        NotifyListChanged();
                    });
                }
                Interlocked.Increment(ref scanned);
            }
            catch (Exception ex) { Log($"TCP-SCAN: loi {baseIp}{i}: {ex.Message}"); }
            finally { sem.Release(); }
        }, token));

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }

        Log($"ScanInSubnet: TCP xong, found={found.Count}");

        // SADP + ONVIF + Provision dieu them camera khong mo port TCP (chua kich hoat / khac subnet)
        onProgress?.Invoke($"TCP xong ({found.Count}). Dang nghe SADP/ONVIF/Provision/SSDP them...");
        using (AcquireMulticastLock())
        {
            var sadpTask = SadpDiscoveryAsync(found, token);
            var onvifTask = OnvifDiscoveryAsync(found, token);
            var provTask = ProvisionDiscoveryAsync(found, token);
            var ssdpTask = SsdpDiscoveryAsync(found, token);
            await Task.WhenAll(
                RunSafe(sadpTask, onProgress, "SADP", token),
                RunSafe(onvifTask, onProgress, "ONVIF", token),
                RunSafe(provTask, onProgress, "Provision", token),
                RunSafe(ssdpTask, onProgress, "SSDP", token));
        }

        Log($"ScanInSubnet: tong cong found={found.Count}");
        var finalList = DedupeByIp(found);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Merge (khong Clear thay the): gop camera trong UI dang co + ket qua UDP,
            // dam bao khong bao gio danh mat camera nao da tim thay.
            var merged = new Dictionary<string, CameraDevice>();
            foreach (var d in items)
                if (!string.IsNullOrEmpty(d.Ip)) merged[d.Ip] = d;
            foreach (var d in finalList)
                if (!string.IsNullOrEmpty(d.Ip)) merged[d.Ip] = d;
            items.Clear();
            foreach (var d in merged.Values.OrderBy(d => d.Ip))
                items.Add(d);
            Log($"ScanInSubnet: done, merged={merged.Count}");
            NotifyListChanged();
        });

        onProgress?.Invoke($"Hoan tat. Tim thay {finalList.Count} camera trong lop mang {baseIp}1-254.");
    }

    // Gom cac entry cung IP ve 1: uu tien entry co MAC (co du thong tin hon)
    private static List<CameraDevice> DedupeByIp(Dictionary<string, CameraDevice> found)
    {
        var result = new Dictionary<string, CameraDevice>();
        foreach (var dev in found.Values)
        {
            // Camera "Unknown" (chua co IP) giu nguyen tung cai
            if (dev.Ip == "Unknown" || dev.Ip == "0.0.0.0")
            {
                result[$"u:{dev.Mac}|{dev.Ip}|{dev.Name}"] = dev;
                continue;
            }
            if (result.TryGetValue(dev.Ip, out var existing))
            {
                // Uu tien cai co MAC / co thong tin nhieu hon
                var wantsNew = !string.IsNullOrEmpty(dev.Mac) && string.IsNullOrEmpty(existing.Mac);
                if (wantsNew) result[dev.Ip] = dev;
            }
            else result[dev.Ip] = dev;
        }
        return result.Values.ToList();
    }

    private static async Task<CameraDevice?> ProbeTcpAsync(string host, CancellationToken token)
    {
        var openPorts = new List<int>();
        foreach (var (port, timeout) in new[]
        {
            // Provision-ISR: http 80, data 9008, rtsp 554; ban moi + log-polling 8080, websocket 7681.
            // 8000 giu lai cho NVR Hikvision/Provision SDK tuong tu.
            (8000, 250), (554, 250), (80, 350), (443, 350),
            (9008, 250), (7681, 250), (8080, 250), (34567, 250), (37777, 250)
        })
        {
            if (await IsPortOpenAsync(host, port, timeout, token))
                openPorts.Add(port);
        }

        if (openPorts.Count == 0) return null;

        // Ghi chu: ResolveMacAsync goi shell /proc/net/arp bi SELinux chan (tra ""). Giữ lai mac tu SADP/ONVIF sau.
        var mac = await ResolveMacAsync(host, token);

        // Nhan dien vendor theo MAC OUI truoc (khong gan "Hikvision" chi vi port 8000 mo -
        // Provision NVR cung mo 8000 cho SDK tuong tu Hikvision)
        var vendor = VendorFromMac(mac);
        if (string.IsNullOrEmpty(vendor)) vendor = HttpVendorHint(await GetHttpInfoAsync(host, token));
        if (string.IsNullOrEmpty(vendor))
        {
            // 9008 (data) + 7681 (websocket) la dac trung Provisional ban moi -> gan Provision truoc
            if (openPorts.Contains(9008) || openPorts.Contains(7681))
                vendor = "Provision";
            else if (openPorts.Contains(8000) || openPorts.Contains(8443))
                vendor = "Camera (port 8000)";
            else if (openPorts.Contains(554))
                vendor = "Camera (RTSP)";
            else if (openPorts.Contains(80) || openPorts.Contains(443))
                vendor = "Camera (HTTP)";
            else
                vendor = "Camera";
        }

        return new CameraDevice
        {
            Ip = host,
            Mac = mac,
            Vendor = vendor,
            Name = host,
            Info = $"TCP | Port: {string.Join(", ", openPorts)}"
                  + (string.IsNullOrEmpty(mac) ? "" : $" | MAC: {mac}")
        };
    }

    // ────────────────────────────────────────────────────────────────
    // 2. SCAN UNKNOWN (SADP + ONVIF multicast/broadcast discovery)
    // ────────────────────────────────────────────────────────────────
    public static async Task ScanUnknownAsync(
        ObservableCollection<CameraDevice> items,
        Action<string>? onProgress,
        CancellationToken token)
    {
        var found = new Dictionary<string, CameraDevice>();

        onProgress?.Invoke("Dang gui SADP broadcast/multicast (Hikvision)...");
        Log("ScanUnknown: bat dau SADP");

        using var multicastLock = AcquireMulticastLock();
        Log($"ScanUnknown: MulticastLock={(multicastLock==null?"FAIL":"OK")}");
        var hikTask = SadpDiscoveryAsync(found, token);

        onProgress?.Invoke("Dang lang nghe Provision announcement (IP Manager)...");
        Log("ScanUnknown: bat dau Provision (UDP 23456)");
        var provTask = ProvisionDiscoveryAsync(found, token);

        onProgress?.Invoke("Dang gui SSDP M-SEARCH (UPnP/port 1900)...");
        Log("ScanUnknown: bat dau SSDP (UDP 1900)");
        var ssdpTask = SsdpDiscoveryAsync(found, token);

        var onvifTask = OnvifDiscoveryAsync(found, token);

        // Chay song song 4 phuong phap: SADP + ONVIF + Provision + SSDP
        await Task.WhenAll(
            RunSafe(hikTask, onProgress, "SADP", token),
            RunSafe(onvifTask, onProgress, "ONVIF", token),
            RunSafe(provTask, onProgress, "Provision", token),
            RunSafe(ssdpTask, onProgress, "SSDP", token));

        Log($"ScanUnknown: ket thuc, found={found.Count}");
        foreach (var kv in found)
            Log($"ScanUnknown:  entry key={kv.Key} ip={kv.Value.Ip} mac={kv.Value.Mac} vendor={kv.Value.Vendor} name={kv.Value.Name}");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            items.Clear();
            foreach (var kv in found.OrderBy(k => k.Key))
                items.Add(kv.Value);
            Log($"ScanUnknown: added {items.Count} items to UI collection (BeginInvoke OK, thread={Environment.CurrentManagedThreadId})");
        });

        onProgress?.Invoke($"Hoan tat. Tim thay {found.Count} camera (gồm camera khác subnet / trùng IP).");
    }

    private static async Task RunSafe(Task task, Action<string>? onProgress, string name, CancellationToken token)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            onProgress?.Invoke($"{name}: {ex.Message}");
        }
    }

    // Hikvision SADP - UDP broadcast + multicast port 37020
    private static async Task SadpDiscoveryAsync(Dictionary<string, CameraDevice> found, CancellationToken token)
    {
        var probe = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Probe><Uuid>{Guid.NewGuid().ToString().ToUpper()}</Uuid><Types>inquiry</Types></Probe>";

        // GUI toa broadcast 255.255.255.255 va multicast 239.255.255.250 (nhu SADP tool)
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        var payload = Encoding.UTF8.GetBytes(probe);
        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, SadpPort),                 // 255.255.255.255:37020
            new(IPAddress.Parse(MulticastSadp), SadpPort)       // 239.255.255.250:37020
        };

        // Gui den tung dia chi broadcast cua moi interface (quan trong voi USB-LAN)
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = uni.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    var parts = uni.Address.GetAddressBytes();
                    var broadcast = new IPAddress(new byte[] {
                        (byte)(parts[0] | ~uni.IPv4Mask.GetAddressBytes()[0]),
                        (byte)(parts[1] | ~uni.IPv4Mask.GetAddressBytes()[1]),
                        (byte)(parts[2] | ~uni.IPv4Mask.GetAddressBytes()[2]),
                        (byte)(parts[3] | ~uni.IPv4Mask.GetAddressBytes()[3])
                    });
                    targets.Add(new IPEndPoint(broadcast, SadpPort));
                }
            }
        }
        catch { }

        Log($"SADP: gui lien tuc {targets.Count} dia chi, lang nghe 8s tren port {SadpPort}...");

        // Lang nghe phan hoi tu camera (broadcast + multicast) trong 6s, vua gui vua nhan
        var deadline = DateTime.UtcNow.AddSeconds(8);
        using var listener = new UdpClient();
        listener.EnableBroadcast = true;
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, SadpPort));
        listener.Client.ReceiveTimeout = 1000;
        int sadpGot = 0;
        try
        {
            listener.JoinMulticastGroup(IPAddress.Parse(MulticastSadp));
        }
        catch { }

        // Gui lai probe moi ~1.5s (nhieu camera chi tra loi khi duoc hoi lai)
        var senderTask = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var ep in targets)
                {
                    try { await sender.SendAsync(payload, payload.Length, ep); }
                    catch { }
                }
                await Task.Delay(1000, token);
            }
        }, CancellationToken.None);

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var result = await ReceiveWithTimeoutAsync(listener, 1000, token);
            if (result == null) continue;
            try
            {
                sadpGot++;
                var xml = Encoding.UTF8.GetString(result.Value.Buffer);
                Log($"SADP recv #{sadpGot} from {result.Value.RemoteEndPoint}: {xml.Length} bytes");
                ParseSadpResponse(xml, found);
            }
            catch { }
        }
        try { await senderTask; } catch { }
        Log($"SADP: ket thuc, nhan {sadpGot} goi.");
    }

    // Provision-ISR: CDevSearch protocol
    // IP Manager bind port 23456 va gui probe den multicast 234.55.55.56:23456
    // Camera tra loi XML voi multicastSearchResult chua IP/MAC/productInfo...
    // IP Manager chi dung 1 port (23456) cho ca send va recv, KHONG dung 3702/1900
    // Binary CDevSearch probe "MHED" dung nhu IP Manager gui:
    //   byte 0-3 : "MHED"
    //   byte 4-5 : lenh/cmd (0x000B ~ 11 hoac 0x0008 ~ 8 tuy sender)
    //   byte 6-7 : 0x0001
    //   byte 8-9 : 0x0001
    //   byte 10-139: de trong (0x00)
    // Tong 140 byte. Camera Provision chi tra loi khi nhan dung probe binary nay.
    private static byte[] BuildCDevSearchProbe(ushort cmd = 0x000B)
    {
        var b = new byte[140];
        b[0] = (byte)'M'; b[1] = (byte)'H'; b[2] = (byte)'E'; b[3] = (byte)'D';
        b[4] = (byte)(cmd & 0xFF);
        b[5] = (byte)((cmd >> 8) & 0xFF);
        b[6] = 0x01; b[7] = 0x00;
        b[8] = 0x01; b[9] = 0x00;
        return b;
    }

    private static async Task ProvisionDiscoveryAsync(Dictionary<string, CameraDevice> found, CancellationToken token)
    {
        // CDevSearch gui den multicast rieng cua Provision (KHONG phai 239.255.255.250),
        // camera tra loi unicast ve chinh port nguon cua socket gui.
        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Parse(ProvisionMulticastSend), ProvisionPort),  // 234.55.55.56:23456 (gui)
            new(IPAddress.Broadcast, ProvisionPort)                       // 255.255.255.255:23456
        };

        var deadline = DateTime.UtcNow.AddSeconds(8);

        var localIp = GetCurrentSubnetBase(out var hostIp, out _) != null ? hostIp : null;
        Log($"Provision: localIp={localIp ?? "?"}");

        // Client gui: bind vao IP local (de camera unicast tra loi dung ve IP nay)
        using var probeUdp = new UdpClient();
        probeUdp.EnableBroadcast = true;
        if (!string.IsNullOrEmpty(localIp))
        {
            try
            {
                probeUdp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(localIp).GetAddressBytes());
                probeUdp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 8);
                probeUdp.Client.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                Log($"Provision: probe bound to {probeUdp.Client.LocalEndPoint}");
            }
            catch (Exception ex) { Log($"Provision: bind/set-opt loi: {ex.Message}"); }
        }

        // Listener multicast 234.55.55.55:23456 de bat response neu camera gui multicast.
        using var listener = new UdpClient();
        listener.EnableBroadcast = true;
        try { listener.Client.Bind(new IPEndPoint(IPAddress.Any, ProvisionPort)); } catch { }
        try { listener.JoinMulticastGroup(IPAddress.Parse(ProvisionMulticastRecv)); } catch { }
        try { listener.JoinMulticastGroup(IPAddress.Parse(MulticastSadp)); } catch { }

        var probes = new List<byte[]>
        {
            BuildCDevSearchProbe(0x000B),
            BuildCDevSearchProbe(0x0008)
        };

        // Unicast sweep: cam phone co the KHONG gui duoc multicast (AP chan), nhung unicast van toi duoc.
        // Cameras tra loi unicast ve nguon, nen gui probe unicast truc tiep den tung IP trong subnet.
        var baseIp = GetCurrentSubnetBase(out _, out _) ?? "";
        var unicastTargets = new List<IPEndPoint>();
        if (!string.IsNullOrEmpty(baseIp))
        {
            for (int i = 1; i <= 254; i++)
            {
                var ip = $"{baseIp}{i}";
                if (ip == localIp) continue;
                unicastTargets.Add(new IPEndPoint(IPAddress.Parse(ip), ProvisionPort));
            }
        }
        foreach (var d in found.Values)
        {
            if (!string.IsNullOrEmpty(d.Ip) && d.Ip != "Unknown" && d.Ip != "0.0.0.0"
                &&!unicastTargets.Any(t => t.Address.ToString() == d.Ip))
                unicastTargets.Add(new IPEndPoint(IPAddress.Parse(d.Ip), ProvisionPort));
        }
        Log($"Provision: unicast sweep {unicastTargets.Count} IP tren port {ProvisionPort}.");

        var senderTask = Task.Run(async () =>
        {
            int i = 0;
            var lastLog = DateTime.UtcNow;
            // Lan dau: quet unicast toan bo IP de camera tra loi som nhat
            if (unicastTargets.Count > 0)
            {
                int sent = 0;
                foreach (var ep in unicastTargets)
                {
                    try { await probeUdp.SendAsync(probes[0], probes[0].Length, ep); sent++; }
                    catch (Exception ex)
                    {
                        if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(2))
                        {
                            Log($"Provision SEND loi (unicast): {ex.Message}");
                            lastLog = DateTime.UtcNow;
                        }
                    }
                    if (sent % 32 == 0) await Task.Delay(30, token);
                }
                Log($"Provision: gui xong unicast sweep ({sent}/{unicastTargets.Count}).");
            }

            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                var p = probes[i % probes.Count];
                foreach (var ep in targets)
                {
                    try { await probeUdp.SendAsync(p, p.Length, ep); }
                    catch (Exception ex)
                    {
                        if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(2))
                        {
                            Log($"Provision SEND loi (mcast): {ex.Message}");
                            lastLog = DateTime.UtcNow;
                        }
                    }
                }
                i++;
                // Gui lien tuc: lan dau gui 5 lan lien tiep trong multicast, sau do moi 1,5s.
                if (i <= 5) await Task.Delay(300, token);
                else await Task.Delay(1500, token);
            }
            Log($"Provision: da gui {probes.Count * (i / probes.Count)} lan multicast probe (i={i}).");
        }, CancellationToken.None);

        // Nhan song song tren ca client gui (unicast) va listener (multicast)
        var got = 0;
        async Task DrainAsync(UdpClient udp, bool isMulticast)
        {
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                var result = await ReceiveWithTimeoutAsync(udp, 1000, token);
                if (result == null) continue;
                var buf = result.Value.Buffer;
                // Bo qua chinh probe cua minh (khi nhan tren multicast group neu join)
                if (buf.Length >= 4 && buf[0] == (byte)'M' && buf[1] == (byte)'H' && buf[2] == (byte)'E' && buf[3] == (byte)'D')
                    continue;
                if (buf.Length == 0) continue;
                int n = System.Threading.Interlocked.Increment(ref got);
                var hex = BitConverter.ToString(buf).Replace("-", " ");
                Log($"Provision recv #{n} ({(isMulticast ? "mcast" : "unicast")}) from {result.Value.RemoteEndPoint}: {buf.Length}b hex=[{hex}]");
                var xml = Encoding.UTF8.GetString(buf);
                ParseProvisionResponse(xml, found);
            }
        }

        using var mcLock = AcquireMulticastLock();
        var recvTasks = new List<Task>
        {
            RunSafe(DrainAsync(probeUdp, false), null, "Prov-uni", token),
            RunSafe(DrainAsync(listener, true), null, "Prov-mcast", token)
        };
        await Task.WhenAll(recvTasks);
        try { await senderTask; } catch { }
        Log($"Provision: ket thuc, nhan {got} goi (unicast+multicast).");
    }

    // Parse goi announce / tra loi cua camera Provision (dang XML text)
    // Cau truc XML theo IPTool_Search.dll:
    //   <multicastSearchResult>
    //     <tcpIp><ip/><mask/><route/></tcpIp>
    //     <port><httpType/><rtspPort/></port>
    //     <productInfo><unit/><softwareVer/><devName/><softBuildDate/><kernelVer/><customerSN/></productInfo>
    //   </multicastSearchResult>
    private static void ParseProvisionResponse(string xml, Dictionary<string, CameraDevice> found)
    {
        // Neu khong phai dang Provision, bo qua (khong log tua lo)
        if (!xml.Contains("multicastSearchResult", StringComparison.OrdinalIgnoreCase)
            && !xml.Contains("tcpIp", StringComparison.OrdinalIgnoreCase)
            && !xml.Contains("productInfo", StringComparison.OrdinalIgnoreCase)
            && !xml.Contains("devName", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Provision parse: khong phai goi Provision ({Truncate(xml, 80)}).");
            return;
        }

        // Parse XML nested: <parent><child>value</child></parent>
        string GetNested(string parent, string child)
        {
            var m = Regex.Match(xml, $"<{parent}>\\s*<{child}>(.*?)</{child}>", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        // Parse flat tag: <tag>value</tag>
        string Get(string tag)
        {
            var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // Trich xuat theo cau truc multicastSearchResult
        var ip = GetNested("tcpIp", "ip");
        var mask = GetNested("tcpIp", "mask");
        var route = GetNested("tcpIp", "route");
        var httpPort = GetNested("port", "httpType");
        var rtspPort = GetNested("port", "rtspPort");
        var unit = GetNested("productInfo", "unit");         // model
        var softwareVer = GetNested("productInfo", "softwareVer");
        var devName = GetNested("productInfo", "devName");
        var buildDate = GetNested("productInfo", "softBuildDate");
        var kernelVer = GetNested("productInfo", "kernelVer");
        var sn = GetNested("productInfo", "customerSN");
        var mac = Get("macAddr");
        if (string.IsNullOrEmpty(mac)) mac = Get("Mac");

        // Fallback: thu flat tags neu nested khong match
        if (string.IsNullOrEmpty(ip)) ip = Get("ipAddr");
        if (string.IsNullOrEmpty(devName)) devName = Get("devName");
        if (string.IsNullOrEmpty(unit)) unit = Get("productInfo");

        var vendor = "Provision";

        if (string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(mac))
        {
            Log($"Provision parse: khong lay duoc ip/mac ({Truncate(xml, 120)})");
            return;
        }

        // Xay dung info chi tiet
        var infoParts = new List<string> { "Provision discovery" };
        if (!string.IsNullOrEmpty(unit)) infoParts.Add($"Model: {unit}");
        if (!string.IsNullOrEmpty(devName)) infoParts.Add($"Name: {devName}");
        if (!string.IsNullOrEmpty(softwareVer)) infoParts.Add($"SW: {softwareVer}");
        if (!string.IsNullOrEmpty(sn)) infoParts.Add($"SN: {sn}");
        if (!string.IsNullOrEmpty(rtspPort)) infoParts.Add($"RTSP: {rtspPort}");
        if (!string.IsNullOrEmpty(httpPort)) infoParts.Add($"HTTP: {httpPort}");
        if (!string.IsNullOrEmpty(mask)) infoParts.Add($"Mask: {mask}");
        if (!string.IsNullOrEmpty(route)) infoParts.Add($"GW: {route}");

        var key = string.IsNullOrEmpty(mac) ? ip : mac;
        lock (_lock)
        {
            if (found.TryGetValue(key, out var existing))
            {
                if (string.IsNullOrEmpty(existing.Mac) && !string.IsNullOrEmpty(mac)) existing.Mac = mac;
                if (string.IsNullOrEmpty(existing.Name) || existing.Name == existing.Ip) existing.Name = devName;
                if (!string.IsNullOrEmpty(unit) && !existing.Info.Contains("Model")) existing.Info += $" | Model: {unit}";
            }
            else
            {
                found[key] = new CameraDevice
                {
                    Ip = string.IsNullOrEmpty(ip) ? "Unknown" : ip,
                    Mac = mac,
                    Vendor = vendor,
                    Name = string.IsNullOrEmpty(devName) ? (string.IsNullOrEmpty(ip) ? "Provision" : ip) : devName,
                    Info = string.Join(" | ", infoParts)
                };
            }
        }
        Log($"Provision parse: mac={mac} ip={ip} name={devName} model={unit} sw={softwareVer}");
    }

    // Parse Hikvision SADP ProbeMatch XML
    private static void ParseSadpResponse(string xml, Dictionary<string, CameraDevice> found)
    {
        if (!xml.Contains("ProbeMatch", StringComparison.OrdinalIgnoreCase))
        {
            Log($"SADP parse: KHONG co ProbeMatch ({xml.Length}b): {Truncate(xml, 120)}");
            return;
        }

        string Get(string tag)
        {
            var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value.Trim()) : "";
        }

        var mac = Get("MAC");
        var ip = Get("IPv4Address");
        var sn = Get("DeviceSN");
        var desc = Get("DeviceDescription");
        var type = Get("DeviceType");
        var cmdPort = Get("CommandPort");
        var httpPort = Get("HttpPort");
        var mask = Get("IPv4SubnetMask");
        var gateway = Get("IPv4Gateway");

        Log($"SADP parse: mac={mac} ip={ip} desc={desc} sn={sn} type={type}");

        if (string.IsNullOrEmpty(mac) && string.IsNullOrEmpty(ip))
        {
            Log("SADP parse: bo qua (khong mac & khong ip)");
            return;
        }

        // Unknown = camera khong co IP hop le (0.0.0.0)
        var isUnknown = string.IsNullOrEmpty(ip) || ip == "0.0.0.0";
        var displayIp = isUnknown ? "Unknown" : ip;

        var vendor = VendorFromMac(mac)
            ?? (desc.Contains("DS-", StringComparison.OrdinalIgnoreCase) ? "Hikvision" : "Camera");

        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(desc)) info.Append(desc);
        if (!string.IsNullOrEmpty(sn)) info.Append($" | SN: {sn}");
        if (!string.IsNullOrEmpty(type)) info.Append($" | Type: {type}");
        if (!string.IsNullOrEmpty(cmdPort)) info.Append($" | Cmd: {cmdPort}");
        if (!string.IsNullOrEmpty(httpPort)) info.Append($" | HTTP: {httpPort}");
        if (!string.IsNullOrEmpty(mask)) info.Append($" | Mask: {mask}");
        if (!string.IsNullOrEmpty(gateway)) info.Append($" | GW: {gateway}");

        var dev = new CameraDevice
        {
            Ip = displayIp,
            Mac = mac,
            Vendor = isUnknown ? "Camera (Unknown)" : vendor,
            Name = string.IsNullOrEmpty(desc) ? displayIp : desc,
            Info = (isUnknown ? "Không có IP (chưa kích hoạt) | " : "") + info
        };

        // Them vao danh sach (dung mac lam key de giu duoc camera trung IP)
        var key = string.IsNullOrEmpty(mac) ? displayIp : mac;
        lock (_lock)
        {
            found[key] = dev;
            Log($"SADP parse: da them key={key} vendor={vendor}");
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    // ────────────────────────────────────────────────────────────────
    // SSDP / UPnP Discovery (IP Manager CUPNPSearch — port 1900)
    // IP Manager gui M-SEARCH den 239.255.255.250:1900, camera tra loi
    // voi LOCATION chua device description XML.
    // ────────────────────────────────────────────────────────────────
    private static async Task SsdpDiscoveryAsync(Dictionary<string, CameraDevice> found, CancellationToken token)
    {
        var msearch =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 3\r\n" +
            "ST: ssdp:all\r\n" +
            "\r\n";

        var payload = Encoding.UTF8.GetBytes(msearch);
        var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastSadp), SsdpPort);

        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        // Gui M-SEARCH den multicast va broadcast cua tung interface
        var targets = new List<IPEndPoint> { multicastEp };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null) continue;
                    var ip = uni.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    var b = uni.Address.GetAddressBytes();
                    var m = uni.IPv4Mask.GetAddressBytes();
                    var broadcast = new IPAddress(new byte[]
                    {
                        (byte)(b[0] | ~m[0]), (byte)(b[1] | ~m[1]),
                        (byte)(b[2] | ~m[2]), (byte)(b[3] | ~m[3])
                    });
                    targets.Add(new IPEndPoint(broadcast, SsdpPort));
                }
            }
        }
        catch { }

        Log($"SSDP: gui M-SEARCH den {targets.Count} dia chi, lang nghe 8s tren port {SsdpPort}...");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        using var listener = new UdpClient();
        listener.EnableBroadcast = true;
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
        listener.Client.ReceiveTimeout = 1000;
        int ssdpGot = 0;
        try { listener.JoinMulticastGroup(IPAddress.Parse(MulticastSadp)); } catch { }

        // Gui lai M-SEARCH moi ~2s
        var senderTask = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var ep in targets)
                {
                    try { await sender.SendAsync(payload, payload.Length, ep); }
                    catch { }
                }
                await Task.Delay(2000, token);
            }
        }, CancellationToken.None);

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var result = await ReceiveWithTimeoutAsync(listener, 1000, token);
            if (result == null) continue;
            try
            {
                ssdpGot++;
                var headers = Encoding.UTF8.GetString(result.Value.Buffer);
                Log($"SSDP recv #{ssdpGot} from {result.Value.RemoteEndPoint}: {headers.Length} bytes");
                ParseSsdpResponse(headers, result.Value.RemoteEndPoint.Address.ToString(), found);
            }
            catch { }
        }
        try { await senderTask; } catch { }
        Log($"SSDP: ket thuc, nhan {ssdpGot} goi.");
    }

    // Parse SSDP response: trich xuat LOCATION (device description XML), SERVER, ST
    private static void ParseSsdpResponse(string headers, string fallbackIp, Dictionary<string, CameraDevice> found)
    {
        // Chi quan tam NOTIFY va 200 OK (tra loi M-SEARCH)
        if (!headers.Contains("NOTIFY", StringComparison.OrdinalIgnoreCase)
            && !headers.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string GetHeader(string name)
        {
            var m = Regex.Match(headers, $"^{name}:\\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        var location = GetHeader("LOCATION");
        var server = GetHeader("SERVER");
        var usn = GetHeader("USN");
        var st = GetHeader("ST");

        // Trich IP tu LOCATION: http://192.168.x.x:port/path
        var ip = fallbackIp;
        var ipMatch = Regex.Match(location, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        if (ipMatch.Success) ip = ipMatch.Groups[1].Value;

        if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0" || ip.StartsWith("127.")) return;

        // Nhan dien vendor tu server
        var vendor = "UPnP";
        var text = $"{server} {usn} {st} {location}";
        if (text.Contains("Provision", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bamboo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ISR", StringComparison.OrdinalIgnoreCase))
            vendor = "Provision";
        else if (text.Contains("Hikvision", StringComparison.OrdinalIgnoreCase))
            vendor = "Hikvision";
        else if (text.Contains("Dahua", StringComparison.OrdinalIgnoreCase))
            vendor = "Dahua";

        // Bo qua device khong phai camera (router, printer, SSDP root...)
        var looksLikeCamera = text.Contains("camera", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nvt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NetworkVideo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Provision", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bamboo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IPCam", StringComparison.OrdinalIgnoreCase)
            // Camera IP dung upnp sdk nhanh (Linux + UPnP/Portable SDK) — dac trung Provision/HiSilicon
            || (text.Contains("Linux", StringComparison.OrdinalIgnoreCase) && text.Contains("UPnP", StringComparison.OrdinalIgnoreCase))
            || text.Contains("hi3535", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hi3536", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HiSilicon", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeCamera)
        {
            Log($"SSDP parse: bo qua non-camera device ({ip}, server={Truncate(server, 60)}, st={Truncate(st, 40)})");
            return;
        }

        var info = new StringBuilder("SSDP/UPnP");
        if (!string.IsNullOrEmpty(server)) info.Append($" | {server}");
        if (!string.IsNullOrEmpty(location)) info.Append($" | {location}");
        if (!string.IsNullOrEmpty(st)) info.Append($" | ST: {st}");

        var key = $"{ip}:{usn}";
        lock (_lock)
        {
            if (!found.ContainsKey(key))
            {
                found[key] = new CameraDevice
                {
                    Ip = ip,
                    Mac = "",
                    Vendor = vendor,
                    Name = string.IsNullOrEmpty(server) ? ip : server,
                    Info = info.ToString()
                };
                Log($"SSDP parse: them {ip} vendor={vendor} server={Truncate(server, 60)}");
            }
        }
    }

    // ONVIF WS-Discovery (Provision / IP Manager / Bamboo)
    private static async Task OnvifDiscoveryAsync(Dictionary<string, CameraDevice> found, CancellationToken token)
    {
        var probe =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:d=\"http://schemas.xmlsoap.org/ws/2004/08/discovery\" " +
            "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">\r\n" +
            "  <e:Header>\r\n" +
            "    <w:MessageID>uuid:" + Guid.NewGuid() + "</w:MessageID>\r\n" +
            "    <w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>\r\n" +
            "    <w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>\r\n" +
            "  </e:Header>\r\n" +
            "  <e:Body>\r\n" +
            "    <d:Probe>\r\n" +
            "      <d:Types>dn:NetworkVideoTransmitter</d:Types>\r\n" +
            "    </d:Probe>\r\n" +
            "  </e:Body>\r\n" +
            "</e:Envelope>";

        var payload = Encoding.UTF8.GetBytes(probe);
        var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastSadp), OnvifPort);

        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        // GUI den multicast va broadcast cua tung interface
        var targets = new List<IPEndPoint> { multicastEp };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null) continue;
                    var ip = uni.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    var b = uni.Address.GetAddressBytes();
                    var m = uni.IPv4Mask.GetAddressBytes();
                    var broadcast = new IPAddress(new byte[]
                    {
                        (byte)(b[0] | ~m[0]), (byte)(b[1] | ~m[1]),
                        (byte)(b[2] | ~m[2]), (byte)(b[3] | ~m[3])
                    });
                    targets.Add(new IPEndPoint(broadcast, OnvifPort));
                }
            }
        }
        catch { }

        Log($"ONVIF: gui lien tuc {targets.Count} dia chi, lang nghe 8s tren port {OnvifPort}...");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        using var listener = new UdpClient();
        listener.EnableBroadcast = true;
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, OnvifPort));
        listener.Client.ReceiveTimeout = 1000;
        int onvifGot = 0;
        try { listener.JoinMulticastGroup(IPAddress.Parse(MulticastSadp)); } catch { }

        // Gui lai probe moi ~1.5s (cameras Provision/Bamboo tra loi cham hoac can hoi lai)
        var senderTask = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var ep in targets)
                {
                    try { await sender.SendAsync(payload, payload.Length, ep); }
                    catch { }
                }
                await Task.Delay(1000, token);
            }
        }, CancellationToken.None);

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var result = await ReceiveWithTimeoutAsync(listener, 1000, token);
            if (result == null) continue;
            try
            {
                onvifGot++;
                var xml = Encoding.UTF8.GetString(result.Value.Buffer);
                Log($"ONVIF recv #{onvifGot} from {result.Value.RemoteEndPoint}: {xml.Length} bytes");
                ParseOnvifResponse(xml, result.Value.RemoteEndPoint.Address.ToString(), found);
            }
            catch { }
        }
        try { await senderTask; } catch { }
        Log($"ONVIF: ket thuc, nhan {onvifGot} goi.");
    }

    private static void ParseOnvifResponse(string xml, string fallbackIp, Dictionary<string, CameraDevice> found)
    {
        if (!xml.Contains("ProbeMatch", StringComparison.OrdinalIgnoreCase)) return;

        string Get(string tag)
        {
            var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value.Trim()) : "";
        }

        // XAddrs chua http://ip:port (co the nhieu dia chi cach nhau space)
        var xaddrs = Get("XAddrs");
        var ip = fallbackIp;
        var ipMatch = Regex.Match(xaddrs, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        if (ipMatch.Success) ip = ipMatch.Groups[1].Value;

        // Extract MAC tu Scopes (onvif://www.onvif.org/name/... hoac hardware)
        var scopes = Get("Scopes");
        var mac = "";
        var macMatch = Regex.Match(xaddrs, @"([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}");
        if (macMatch.Success) mac = macMatch.Groups[0].Value;
        var hwMatch = Regex.Match(scopes, @"hardware=([^\s;]+)");
        var nameMatch = Regex.Match(scopes, @"name=([^\s;]+)");
        var model = hwMatch.Success ? Uri.UnescapeDataString(hwMatch.Groups[1].Value) : "";
        var name = nameMatch.Success ? Uri.UnescapeDataString(nameMatch.Groups[1].Value) : "";

        var isUnknown = ip == "0.0.0.0" || string.IsNullOrEmpty(ip);

        // Nhan dien vendor tu model/name/hardware (Provision/Bamboo/ISR, Hikvision, Dahua...)
        var text = $"{model} {name} {scopes}";
        var vendor = "ONVIF";
        if (text.Contains("Provision", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bamboo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ISR", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LTS", StringComparison.OrdinalIgnoreCase))
            vendor = "Provision";
        else if (text.Contains("Hikvision", StringComparison.OrdinalIgnoreCase)
                 || text.Contains("DS-", StringComparison.OrdinalIgnoreCase))
            vendor = "Hikvision";
        else if (text.Contains("Dahua", StringComparison.OrdinalIgnoreCase))
            vendor = "Dahua";
        else if (text.Contains("Uniview", StringComparison.OrdinalIgnoreCase)
                 || text.Contains("UNV", StringComparison.OrdinalIgnoreCase))
            vendor = "Uniview";
        var macVendor = VendorFromMac(mac);
        if (macVendor != null) vendor = macVendor;

        var info = new StringBuilder("UDP ONVIF");
        if (!string.IsNullOrEmpty(xaddrs)) info.Append($" | {xaddrs}");
        if (!string.IsNullOrEmpty(name)) info.Append($" | {name}");
        if (!string.IsNullOrEmpty(model)) info.Append($" | {model}");

        var dev = new CameraDevice
        {
            Ip = isUnknown ? "Unknown" : ip,
            Mac = mac,
            Vendor = isUnknown ? $"{vendor} (Unknown)" : vendor,
            Name = string.IsNullOrEmpty(model) ? (isUnknown ? "Unknown" : ip) : model,
            Info = info.ToString()
        };

        var key = string.IsNullOrEmpty(mac) ? $"{dev.Ip}:{name}" : mac;
        lock (_lock)
        {
            if (!found.ContainsKey(key))
                found[key] = dev;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // HELPER
    // ────────────────────────────────────────────────────────────────

    /// <summary>Tim base IP cua subnet dang duoc cap (WiFi/USB-LAN), vi du "192.168.1."</summary>
    private static string? GetCurrentSubnetBase(out string? hostIp, out string? mask)
    {
        hostIp = null;
        mask = null;
        try
        {
            // Uu tien interface co default gateway (la subnet dang dung)
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    var props = nic.GetIPProperties();
                    if (props == null || props.GatewayAddresses.Count == 0) continue;
                    foreach (var uni in props.UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = uni.Address.ToString();
                        if (ip.StartsWith("127.")) continue;
                        hostIp = ip;
                        mask = uni.IPv4Mask?.ToString() ?? "";
                        var parts = ip.Split('.');
                        return $"{parts[0]}.{parts[1]}.{parts[2]}.";
                    }
                }
            }

            // Fallback: bat ky interface Up nao
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                if (props == null) continue;
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = uni.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    hostIp = ip;
                    mask = uni.IPv4Mask?.ToString() ?? "";
                    var parts = ip.Split('.');
                    return $"{parts[0]}.{parts[1]}.{parts[2]}.";
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Acquire MulticastLock de nhan duoc multicast packet tren Android.</summary>
    private static IDisposable? AcquireMulticastLock()
    {
        try
        {
            var context = Android.App.Application.Context;
            var wifi = context.GetSystemService(Android.Content.Context.WifiService) as Android.Net.Wifi.WifiManager;
            var multiLock = wifi?.CreateMulticastLock("MauiScanner.Multicast");
            multiLock?.SetReferenceCounted(false);
            multiLock?.Acquire();
            if (multiLock != null)
                return new MulticastLockDisposable(multiLock);
        }
        catch { }
        return null;
    }

    private sealed class MulticastLockDisposable : IDisposable
    {
        private readonly Android.Net.Wifi.WifiManager.MulticastLock _lock;
        public MulticastLockDisposable(Android.Net.Wifi.WifiManager.MulticastLock l) => _lock = l;
        public void Dispose()
        {
            try { if (_lock.IsHeld) _lock.Release(); } catch { }
        }
    }

    private static async Task<string> GetHttpInfoAsync(string host, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(1200);
            using var resp = await _http.GetAsync($"http://{host}/", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            // Nhan dien vendor tu body trang web
            if (body.Contains("Provision", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Bamboo", StringComparison.OrdinalIgnoreCase)
                || body.Contains("ISR", StringComparison.OrdinalIgnoreCase)
                || body.Contains("LTS", StringComparison.OrdinalIgnoreCase))
                return "Provision";
            if (body.Contains("Hikvision", StringComparison.OrdinalIgnoreCase)
                || body.Contains("HIKVISION", StringComparison.OrdinalIgnoreCase))
                return "Hikvision";
            if (body.Contains("Dahua", StringComparison.OrdinalIgnoreCase)
                || body.Contains("dss", StringComparison.OrdinalIgnoreCase))
                return "Dahua";
            if (body.Contains("Uniview", StringComparison.OrdinalIgnoreCase)
                || body.Contains("unv", StringComparison.OrdinalIgnoreCase))
                return "Uniview";
            if (body.Contains("axis-", StringComparison.OrdinalIgnoreCase))
                return "Axis";
        }
        catch { }
        return "";
    }

    // Chuyen chuoi vendor tu HTTP thanh ten nhan dien chuan
    private static string? HttpVendorHint(string httpInfo)
    {
        if (string.IsNullOrEmpty(httpInfo)) return null;
        return httpInfo == "Hikvision" || httpInfo == "Provision" || httpInfo == "Dahua"
            || httpInfo == "Uniview" || httpInfo == "Axis" ? httpInfo : null;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var delayTask = Task.Delay(timeoutMs, token);
            var done = await Task.WhenAny(connectTask, delayTask);
            if (done != connectTask)
            {
                // Connect bi bo di: swallow exception trach unobserved-task crash
                _ = connectTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            await connectTask;
            return client.Connected;
        }
        catch { return false; }
    }

    // UdpClient.ReceiveAsync(CancellationToken) KHONG ton trong token tren Android khi khong co du lieu
    // (treo vo han). Dung Task.WhenAny + Task.Delay de dam bao timeout chat che.
    private static async Task<UdpReceiveResult?> ReceiveWithTimeoutAsync(UdpClient listener, int timeoutMs, CancellationToken token)
    {
        try
        {
            var recvTask = listener.ReceiveAsync();
            var delayTask = Task.Delay(timeoutMs, token);
            var done = await Task.WhenAny(recvTask, delayTask);
            if (done != recvTask)
            {
                // Khong co du lieu trong timeout: swallow exception cua task receive con treo
                _ = recvTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            return await recvTask;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private static async Task<string> ResolveMacAsync(string ip, CancellationToken token)
    {
        try
        {
            var output = await RunShellAsync($"cat /proc/net/arp | grep \"{ip}\"", token);
            if (!string.IsNullOrEmpty(output))
            {
                var parts = output.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[3] != "00:00:00:00:00:00")
                    return parts[3];
            }
        }
        catch { }
        return "";
    }

    private static async Task<string> RunShellAsync(string command, CancellationToken token)
    {
        try
        {
            var java = new Java.Lang.ProcessBuilder("/system/bin/sh", "-c", command);
            java.RedirectErrorStream(true); // gop stderr vao stdout
            var proc = java.Start();
            using var reader = new System.IO.StreamReader(proc.InputStream);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(3000);
            var result = await reader.ReadToEndAsync().WaitAsync(cts.Token);
            try { proc.Destroy(); } catch { }
            return result?.Trim() ?? "";
        }
        catch { return ""; }
    }
}
