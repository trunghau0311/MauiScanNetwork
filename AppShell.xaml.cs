using MauiScannetwork.Features.Network;

namespace MauiScannetwork;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute(nameof(NetworkPage), typeof(NetworkPage));
	}
}