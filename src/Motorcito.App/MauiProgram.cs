using Microsoft.Extensions.Logging;
using Motorcito.Obd;
using Motorcito.Obd.Testing;

namespace Motorcito.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton(CreateAdapter());
		builder.Services.AddSingleton<LiveDataViewModel>();
		builder.Services.AddTransient<MainPage>();

		return builder.Build();
	}

	/// <summary>
	/// Chooses the transport for this run.
	///
	/// The iOS Simulator has no Bluetooth stack and no ExternalAccessory support,
	/// so a simulator build always gets the in-memory vehicle. On a physical
	/// device the real MX+ adapter is used.
	///
	/// This method is the only place in the app that decides which transport is
	/// in play; nothing downstream can tell the difference.
	/// </summary>
	private static IObdAdapter CreateAdapter()
	{
#if IOS
		if (DeviceInfo.Current.DeviceType != DeviceType.Virtual)
			return new Platforms.iOS.ExternalAccessoryObdAdapter();
#endif

		// A 2021-ish inline-six: no bank 2 trims, no oil temp. Quirks are set to
		// clone grade deliberately — if the UI only looks right against a
		// perfect adapter, it is not finished.
		return new SimulatedObdAdapter(
			name: "Simulated Vehicle (BMW M3 G80)",
			quirks: SimulatorQuirks.CheapClone,
			storedDtcs: ["P0171"]);
	}
}
