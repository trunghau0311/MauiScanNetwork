using Android.App;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Java.Net;
using System.Net.NetworkInformation;
using Microsoft.Maui.Networking;

namespace MauiScannetwork.Features.Network;

public partial class NetworkPage : ContentPage
{
    public NetworkPage()
    {
        InitializeComponent();
    }

    private async void OnCheckClicked(object? sender, EventArgs e)
    {
        BtnCheck.IsEnabled = false;
        BtnCheck.Text = "DANG KIEM TRA...";
        LblResult.Text = "Dang quet cac ket noi mang...";
        NetworkDetailsPanel.IsVisible = false;
        ResetLabels();

        try
        {
            var connectivity = Connectivity.Current;
            if (connectivity == null || connectivity.NetworkAccess == NetworkAccess.Unknown)
            {
                LblResult.Text = "Khong tim thay ket noi mang nao.";
                BtnCheck.IsEnabled = true;
                BtnCheck.Text = "CHECK";
                return;
            }

            var access = connectivity.NetworkAccess;
            var profiles = connectivity.ConnectionProfiles.ToList();

            var hasWifi = profiles.Contains(ConnectionProfile.WiFi);
            var hasMobile = profiles.Contains(ConnectionProfile.Cellular);

            bool hasEthernet = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                hasEthernet = CheckEthernetAvailable();
            }

            if (!hasWifi && !hasMobile && !hasEthernet)
            {
                LblResult.Text = "Khong tim thay ket noi mang nao hoat dong.";
                BtnCheck.IsEnabled = true;
                BtnCheck.Text = "CHECK";
                return;
            }

            NetworkDetailsPanel.IsVisible = true;
            var found = new List<string>();

            if (hasWifi)
            {
                found.Add("WiFi");
                await LoadNetworkDetails(NetworkType.Wifi, access);
            }

            if (hasMobile)
            {
                found.Add("Mobile Data");
                await LoadNetworkDetails(NetworkType.Mobile, access);
            }

            if (hasEthernet)
            {
                found.Add("USB-C/LAN");
                await LoadNetworkDetails(NetworkType.Ethernet, access);
            }

            LblResult.Text = $"Tim thay: {string.Join(", ", found)} | Truy van: {(access == NetworkAccess.Internet ? "Co Internet" : access == NetworkAccess.ConstrainedInternet ? "Han che" : "Khong co Internet")}";
        }
        catch (Exception ex)
        {
            LblResult.Text = $"Loi: {ex.Message}";
        }
        finally
        {
            BtnCheck.IsEnabled = true;
            BtnCheck.Text = "CHECK";
        }
    }

    private bool CheckEthernetAvailable()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return false;

        try
        {
            var cm = Android.App.Application.Context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (cm == null) return false;

            var networks = cm.GetAllNetworks();
            if (networks == null) return false;

            foreach (var net in networks)
            {
                var caps = cm.GetNetworkCapabilities(net);
                if (caps != null && caps.HasTransport(Android.Net.TransportType.Ethernet))
                    return true;
            }

            // Fallback: check known ethernet interface names
            foreach (var net in networks)
            {
                var lp = cm.GetLinkProperties(net);
                if (lp != null)
                {
                    var iface = lp.InterfaceName;
                    if (iface != null && (iface.StartsWith("eth") || iface.StartsWith("usb") ||
                        iface.StartsWith("rndis") || iface.Contains("lan")))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private enum NetworkType { Wifi, Mobile, Ethernet }

    private async Task LoadNetworkDetails(NetworkType type, NetworkAccess access)
    {
        await Task.Run(() =>
        {
            try
            {
                var context = Android.App.Application.Context;
                var cm = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

                var details = new NetworkDetails();
                string? ifaceName = null;

                if (cm != null)
                {
                    var networks = cm.GetAllNetworks();
                    if (networks != null)
                    {
                        // Pass 1: match by interface NAME first (reliable on Android/Oppo;
                        // NetworkInterfaceType.Ethernet often labels wlan0 as Ethernet too).
                        foreach (var net in networks)
                        {
                            var lp = cm.GetLinkProperties(net);
                            if (lp?.InterfaceName == null) continue;
                            if (!InterfaceMatchesName(lp.InterfaceName, type)) continue;

                            var cand = GetDetailsFromLinkProperties(lp, type);
                            if (string.IsNullOrEmpty(details.Ip))
                            {
                                details = cand;
                                ifaceName = lp.InterfaceName;
                            }
                        }

                        // Pass 2: fallback by transport capability (interface names unknown)
                        if (details.Ip == null)
                        {
                            foreach (var net in networks)
                            {
                                var caps = cm.GetNetworkCapabilities(net);
                                if (caps == null) continue;

                                bool match = type switch
                                {
                                    NetworkType.Wifi => caps.HasTransport(Android.Net.TransportType.Wifi),
                                    NetworkType.Mobile => caps.HasTransport(Android.Net.TransportType.Cellular),
                                    NetworkType.Ethernet => OperatingSystem.IsAndroidVersionAtLeast(28)
                                        && caps.HasTransport(Android.Net.TransportType.Ethernet),
                                    _ => false
                                };
                                if (!match) continue;

                                var lp = cm.GetLinkProperties(net);
                                var cand = GetDetailsFromLinkProperties(lp, type);
                                details = cand;
                                ifaceName = lp?.InterfaceName;
                                break;
                            }
                        }
                    }
                }

                // Pass 3: NetworkInterface enumeration fallback (Oppo USB-C LAN case,
                // where ConnectivityManager may not report the wired interface).
                if (details.Ip == null)
                {
                    var fallback = GetDetailsFromNetworkInterface(type);
                    if (fallback.Ip != null)
                    {
                        details = fallback;
                        ifaceName = fallback.Iface ?? ifaceName;
                    }
                }

                var finalIface = ifaceName;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    switch (type)
                    {
                        case NetworkType.Wifi:
                            LblWifiIface.Text = string.IsNullOrEmpty(finalIface) ? "" : finalIface;
                            LblWifiIp.Text = $"IP: {details.Ip ?? "Khong lay duoc"}";
                            LblWifiSubnet.Text = $"Subnet: {details.Subnet ?? "Khong lay duoc"}";
                            LblWifiGateway.Text = $"Gateway: {details.Gateway ?? "Khong lay duoc"}";
                            LblWifiDns.Text = $"DNS: {details.Dns ?? "Khong lay duoc"}";
                            LblWifiStatus.Text = $"Trang thai: {GetStatusText(access)}" + (details.Extra != null ? $" | {details.Extra}" : "");
                            break;

                        case NetworkType.Mobile:
                            LblMobileIface.Text = string.IsNullOrEmpty(finalIface) ? "" : finalIface;
                            LblMobileIp.Text = $"IP: {details.Ip ?? "Khong lay duoc"}";
                            LblMobileSubnet.Text = $"Subnet: {details.Subnet ?? "Khong lay duoc"}";
                            LblMobileGateway.Text = $"Gateway: {details.Gateway ?? "Khong lay duoc"}";
                            LblMobileDns.Text = $"DNS: {details.Dns ?? "Khong lay duoc"}";
                            LblMobileStatus.Text = $"Trang thai: {GetStatusText(access)}" + (details.Extra != null ? $" | {details.Extra}" : "");
                            break;

                        case NetworkType.Ethernet:
                            LblUsbIface.Text = string.IsNullOrEmpty(finalIface) ? "" : finalIface;
                            LblUsbIp.Text = $"IP: {details.Ip ?? "Khong lay duoc"}";
                            LblUsbSubnet.Text = $"Subnet: {details.Subnet ?? "Khong lay duoc"}";
                            LblUsbGateway.Text = $"Gateway: {details.Gateway ?? "Khong lay duoc"}";
                            LblUsbDns.Text = $"DNS: {details.Dns ?? "Khong lay duoc"}";
                            LblUsbStatus.Text = $"Trang thai: {GetStatusText(access)}" + (details.Extra != null ? $" | {details.Extra}" : "");
                            break;
                    }
                });
            }
            catch { }
        });
    }

    private class NetworkDetails
    {
        public string? Ip { get; set; }
        public string? Subnet { get; set; }
        public string? Gateway { get; set; }
        public string? Dns { get; set; }
        public string? Extra { get; set; }
        public string? Iface { get; set; }
    }

    private static bool InterfaceMatchesName(string iface, NetworkType type)
    {
        var name = iface.ToLowerInvariant();
        return type switch
        {
            NetworkType.Wifi => name.StartsWith("wlan") || name.StartsWith("wlp"),
            NetworkType.Mobile => name.StartsWith("rmnet") || name.StartsWith("ccmni")
                || name.StartsWith("pdp") || name.StartsWith("cell")
                || name.StartsWith("wwan"),
            NetworkType.Ethernet => name.StartsWith("eth") || name.StartsWith("usb")
                || name.StartsWith("rndis") || name.Contains("lan"),
            _ => false
        };
    }

    private NetworkDetails GetDetailsFromLinkProperties(LinkProperties? lp, NetworkType type)
    {
        var details = new NetworkDetails();
        if (lp == null) return details;

        details.Ip = GetIPv4FromLinkProperties(lp);
        details.Subnet = GetSubnetFromLinkProperties(lp);
        details.Gateway = GetGatewayFromLinkProperties(lp);
        details.Dns = GetDnsFromLinkProperties(lp);

        if (type == NetworkType.Wifi && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                var wm = Android.App.Application.Context.GetSystemService(Context.WifiService) as WifiManager;
                var info = wm?.ConnectionInfo;
                if (info != null)
                {
                    details.Extra = $"Toc do: {info.LinkSpeed} Mbps | Tin hieu: {info.Rssi} dBm";
                }
            }
            catch { }
        }

        return details;
    }

    private string? GetIPv4FromLinkProperties(LinkProperties lp)
    {
        foreach (var addr in lp.LinkAddresses)
        {
            var ip = addr?.Address?.HostAddress;
            if (ip != null && ip.Contains('.'))
                return ip;
        }
        return null;
    }

    private string? GetSubnetFromLinkProperties(LinkProperties lp)
    {
        foreach (var addr in lp.LinkAddresses)
        {
            if (addr?.Address?.HostAddress != null && addr.Address.HostAddress.Contains('.'))
                return PrefixLengthToSubnetMask(addr.PrefixLength);
        }
        return null;
    }

    private string? GetGatewayFromLinkProperties(LinkProperties lp)
    {
        foreach (var route in lp.Routes)
        {
            if (route?.Gateway != null)
                return route.Gateway.HostAddress;
        }
        return null;
    }

    private string? GetDnsFromLinkProperties(LinkProperties lp)
    {
        var dnsList = new List<string>();
        foreach (var dns in lp.DnsServers)
        {
            if (dns != null)
            {
                var host = dns.HostAddress;
                if (host != null && host.Contains('.'))
                    dnsList.Add(host);
            }
        }
        return dnsList.Count > 0 ? string.Join(", ", dnsList) : null;
    }

    private NetworkDetails GetDetailsFromNetworkInterface(NetworkType type)
    {
        var details = new NetworkDetails();
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                bool match;
                if (type == NetworkType.Wifi)
                {
                    match = InterfaceMatchesName(iface.Name, type)
                        || iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                }
                else if (type == NetworkType.Mobile)
                {
                    match = InterfaceMatchesName(iface.Name, type);
                }
                else
                {
                    match = InterfaceMatchesName(iface.Name, type)
                        || iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                        || iface.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx
                        || iface.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet
                        || iface.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT;
                }

                // Quan trọng: network không dây (wlan/wlp) KHÔNG được tính là Ethernet
                // dù Android báo NetworkInterfaceType.Ethernet cho wlan0.
                if (type == NetworkType.Ethernet
                    && (iface.Name.ToLowerInvariant().StartsWith("wlan")
                        || iface.Name.ToLowerInvariant().StartsWith("wlp")))
                {
                    match = false;
                }

                if (!match) continue;

                var props = iface.GetIPProperties();
                if (props == null) continue;

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        details.Ip = uni.Address.ToString();
                        details.Subnet = PrefixLengthToSubnetMask(uni.PrefixLength);
                        break;
                    }
                }

                if (props.GatewayAddresses.Count > 0)
                {
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            details.Gateway = gw.Address.ToString();
                            break;
                        }
                    }
                }

                var dnsList = new List<string>();
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        dnsList.Add(dns.ToString());
                }
                if (dnsList.Count > 0)
                    details.Dns = string.Join(", ", dnsList);

                details.Iface = iface.Name;

                if (!string.IsNullOrEmpty(details.Ip))
                    break;
            }
        }
        catch { }

        return details;
    }

    private static string PrefixLengthToSubnetMask(int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > 32) return "N/A";
        uint mask = prefixLength == 0 ? 0 : ~((1u << (32 - prefixLength)) - 1);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    private static string GetStatusText(NetworkAccess access)
    {
        return access switch
        {
            NetworkAccess.Internet => "Co Internet",
            NetworkAccess.ConstrainedInternet => "Han che Internet",
            NetworkAccess.Local => "Mang noi bo",
            NetworkAccess.None => "Khong co Internet",
            _ => "Khong xac dinh"
        };
    }

    private void ResetLabels()
    {
        LblWifiIface.Text = "";
        LblWifiIp.Text = "IP: --";
        LblWifiSubnet.Text = "Subnet: --";
        LblWifiGateway.Text = "Gateway: --";
        LblWifiDns.Text = "DNS: --";
        LblWifiStatus.Text = "Trang thai: --";

        LblMobileIface.Text = "";
        LblMobileIp.Text = "IP: --";
        LblMobileSubnet.Text = "Subnet: --";
        LblMobileGateway.Text = "Gateway: --";
        LblMobileDns.Text = "DNS: --";
        LblMobileStatus.Text = "Trang thai: --";

        LblUsbIface.Text = "";
        LblUsbIp.Text = "IP: --";
        LblUsbSubnet.Text = "Subnet: --";
        LblUsbGateway.Text = "Gateway: --";
        LblUsbDns.Text = "DNS: --";
        LblUsbStatus.Text = "Trang thai: --";
    }
}
