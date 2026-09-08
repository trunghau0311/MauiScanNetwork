using Microsoft.Extensions.Logging;
using LibVLCSharp.MAUI;

namespace MauiScannetwork;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseLibVLCSharp()
			.ConfigureMauiHandlers(handlers =>
			{
#if ANDROID
				handlers.AddHandler<MauiScannetwork.Features.Camera.VideoPlayerView, MauiScannetwork.Platforms.Android.VideoPlayerViewHandler>();
#endif
			})
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
