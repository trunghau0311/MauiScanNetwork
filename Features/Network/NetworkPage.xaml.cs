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
                if (cm == null) return;

                Android.Net.Network targetNetwork = null;
                Android.Net.NetworkCapabilities targetCaps = null;

                if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;

                var networks = cm.GetAllNetworks();
                if (networks == null) return;

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

                    // Fallback for Ethernet: check interface name
                    if (!match && type == NetworkType.Ethernet)
                    {
                        var lp = cm.GetLinkProperties(net);
                        if (lp?.InterfaceName != null)
                        {
                            var iface = lp.InterfaceName.ToLower();
                            if (iface.StartsWith("eth") || iface.StartsWith("usb") ||
                                iface.StartsWith("rndis") || iface.Contains("lan"))
                                match = true;
                        }
                    }

                    if (match)
                    {
                        targetNetwork = net;
                        targetCaps = caps;
                        break;
                    }
                }

                if (targetNetwork == null) return;

                var linkProps = cm.GetLinkProperties(targetNetwork);
                var details = GetDetailsFromLinkProperties(linkProps, type);

                // If no link properties (Oppo USB-C LAN case), try NetworkInterface fallback
                if (string.IsNullOrEmpty(details.Ip) && type == NetworkType.Ethernet)
                {
                    details = GetDetailsFromNetworkInterface(type);
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    switch (type)
                    {
                        case NetworkType.Wifi:
                            LblWifiIp.Text = $"IP: {details.Ip ?? "Khong lay duoc"}";
                            LblWifiSubnet.Text = $"Subnet: {details.Subnet ?? "Khong lay duoc"}";
                            LblWifiGateway.Text = $"Gateway: {details.Gateway ?? "Khong lay duoc"}";
                            LblWifiDns.Text = $"DNS: {details.Dns ?? "Khong lay duoc"}";
                            LblWifiStatus.Text = $"Trang thai: {GetStatusText(access)}" + (details.Extra != null ? $" | {details.Extra}" : "");
                            break;

                        case NetworkType.Mobile:
                            LblMobileIp.Text = $"IP: {details.Ip ?? "Khong lay duoc"}";
                            LblMobileSubnet.Text = $"Subnet: {details.Subnet ?? "Khong lay duoc"}";
                            LblMobileGateway.Text = $"Gateway: {details.Gateway ?? "Khong lay duoc"}";
                            LblMobileDns.Text = $"DNS: {details.Dns ?? "Khong lay duoc"}";
                            LblMobileStatus.Text = $"Trang thai: {GetStatusText(access)}" + (details.Extra != null ? $" | {details.Extra}" : "");
                            break;

                        case NetworkType.Ethernet:
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

                bool match = type switch
                {
                    NetworkType.Wifi => iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    NetworkType.Mobile => iface.NetworkInterfaceType == NetworkInterfaceType.Unknown,
                    NetworkType.Ethernet => iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                        || iface.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx
                        || iface.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet,
                    _ => false
                };

                if (!match && type == NetworkType.Mobile)
                {
                    var name = iface.Name.ToLower();
                    if (name.StartsWith("rmnet") || name.StartsWith("ccmni") || name.StartsWith("pdp"))
                        match = true;
                }

                if (!match && type == NetworkType.Ethernet)
                {
                    var name = iface.Name.ToLower();
                    if (name.Contains("eth") || name.Contains("usb") ||
                        name.Contains("rndis") || name.Contains("lan"))
                        match = true;
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
        LblWifiIp.Text = "IP: --";
        LblWifiSubnet.Text = "Subnet: --";
        LblWifiGateway.Text = "Gateway: --";
        LblWifiDns.Text = "DNS: --";
        LblWifiStatus.Text = "Trang thai: --";

        LblMobileIp.Text = "IP: --";
        LblMobileSubnet.Text = "Subnet: --";
        LblMobileGateway.Text = "Gateway: --";
        LblMobileDns.Text = "DNS: --";
        LblMobileStatus.Text = "Trang thai: --";

        LblUsbIp.Text = "IP: --";
        LblUsbSubnet.Text = "Subnet: --";
        LblUsbGateway.Text = "Gateway: --";
        LblUsbDns.Text = "DNS: --";
        LblUsbStatus.Text = "Trang thai: --";
    }
}
