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
        // 主题由 MainWindow 构造时按设置套用，这里不要再覆盖成 ElementTheme.Default
        var window = new MainWindow();
        _window = window;
        _window.Activate();
    }
}
