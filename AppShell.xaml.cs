using MauiScannetwork.Features.Network;
using MauiScannetwork.Features.Camera;
using MauiScannetwork.Features.Nmap;
using MauiScannetwork.Features.Diagnostics;

namespace MauiScannetwork;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute(nameof(NetworkPage), typeof(NetworkPage));
		Routing.RegisterRoute(nameof(CameraPage), typeof(CameraPage));
		Routing.RegisterRoute(nameof(CameraDiscoveryPage), typeof(CameraDiscoveryPage));
		Routing.RegisterRoute(nameof(CameraPreviewPage), typeof(CameraPreviewPage));
		Routing.RegisterRoute(nameof(NmapPage), typeof(NmapPage));
		Routing.RegisterRoute(nameof(DiagnosticsPage), typeof(DiagnosticsPage));
	}
}
