using Microsoft.Extensions.Logging;
using Motorcito.Data;
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

		builder.Services.AddSingleton<GeolocationAltitudeProvider>();
		builder.Services.AddSingleton(sp => CreateLoggingService(
			sp.GetRequiredService<GeolocationAltitudeProvider>()));

		// The real adapter is wrapped so every exchange can be recorded before
		// parsing. Registered as IObdAdapter so nothing downstream knows the
		// difference — the decorator exists to observe behaviour, never to
		// change it.
		builder.Services.AddSingleton<IObdAdapter>(sp => new LoggingObdAdapter(
			CreateAdapter(),
			sp.GetRequiredService<LoggingService>().ObdLog));
		builder.Services.AddSingleton<LiveDataViewModel>();
		builder.Services.AddTransient<MainPage>();

		return builder.Build();
	}

	/// <summary>
	/// Opens the on-device database and repairs anything a previous run left
	/// broken.
	///
	/// Recovery runs here, before the first connection, because a trip left open
	/// by a terminated session must be closed before a new one starts — and
	/// because doing it once at startup is simpler to reason about than doing it
	/// lazily.
	/// </summary>
	private static LoggingService CreateLoggingService(IAltitudeProvider altitude)
	{
		var service = new LoggingService(
			MotorcitoDatabase.DefaultPath(FileSystem.AppDataDirectory), altitude);

		var repaired = service.RecoverOrphanedTrips();
		if (repaired > 0)
			System.Diagnostics.Debug.WriteLine($"[motorcito] closed {repaired} trip(s) left open by an unclean shutdown");

		return service;
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
