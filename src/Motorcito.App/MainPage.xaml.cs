namespace Motorcito.App;

public partial class MainPage : ContentPage
{
    private readonly LiveDataViewModel _viewModel;

    public MainPage(LiveDataViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
        => await _viewModel.ConnectAsync();

    private async void OnDisconnectClicked(object? sender, EventArgs e)
        => await _viewModel.DisconnectAsync();
}
