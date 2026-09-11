using Microsoft.Extensions.DependencyInjection;

namespace Motorcito.App;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

		// Flush buffered samples whenever the app leaves the foreground.
		//
		// Recording continues in the background — that is what the
		// external-accessory background mode is for — but iOS can terminate a
		// background app without warning, and batched rows live only in memory
		// until written. Stopped fires on backgrounding; Destroying is the last
		// notification before teardown. Neither ends the trip: being
		// backgrounded is not the drive ending, and if the app never returns,
		// startup orphan recovery closes the trip from its last written sample.
		window.Stopped += (_, _) => Flush();
		window.Destroying += (_, _) => Flush();

		return window;
	}

	private void Flush()
	{
		// Resolved lazily rather than injected: the view model is a singleton
		// that may not have been constructed yet if the app is backgrounded
		// before the dashboard is first shown.
		var viewModel = _services.GetService<LiveDataViewModel>();
		viewModel?.FlushForLifecycleEvent();
	}
}
