using Microsoft.UI.Xaml;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.ApplyTheme(ElementTheme.Default);
        _window = window;
        _window.Activate();
    }
}
