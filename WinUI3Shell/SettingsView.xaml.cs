using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace WinUI3Shell;

/// <summary>Dev-C++ 风格的设置面板：左侧导航（编译选项 / 编辑器），右侧 WinUI 3 卡片式内容。</summary>
public sealed partial class SettingsView : UserControl
{
    private const string TemplateHostName = "mny.settings";

    private AppSettings _settings = new();
    private bool _loading;
    private bool _templateReady;
    private string _pendingTemplate = string.Empty;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    /// <summary>打开面板时把当前设置填进控件。</summary>
    public void Load(AppSettings settings)
    {
        _loading = true;
        _settings = settings;

        CompilerFlagsBox.Text = settings.CompilerFlags;
        LinkerFlagsBox.Text = settings.LinkerFlags;
        SendTemplateToEditor(settings.TemplateCode);

        _loading = false;
    }

    public event Action? SettingsChanged;
    public event Action? CloseRequested;

    private void RaiseChanged()
    {
        if (!_loading)
        {
            SettingsChanged?.Invoke();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPane(tag);
        }
    }

    private string _activePaneTag = "compile";

    private void ShowPane(string tag)
    {
        if (tag == _activePaneTag)
        {
            return;
        }
        _activePaneTag = tag;

        bool compile = tag == "compile";
        ScrollViewer incoming = compile ? CompilePane : EditorPane;
        ScrollViewer outgoing = compile ? EditorPane : CompilePane;

        outgoing.Visibility = Visibility.Collapsed;

        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        AnimatePaneIn(incoming);
    }

    /// <summary>切页时淡入并轻微上移，和窗口其它地方的过渡保持一致。</summary>
    private static void AnimatePaneIn(ScrollViewer pane)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var storyboard = new Storyboard();

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, pane);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation { From = 12, To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(slide, pane);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        storyboard.Children.Add(slide);

        storyboard.Begin();
    }

    private void CompilerFlags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.CompilerFlags = CompilerFlagsBox.Text;
        RaiseChanged();
    }

    private void LinkerFlags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.LinkerFlags = LinkerFlagsBox.Text;
        RaiseChanged();
    }

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await TemplateWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = TemplateWebView.CoreWebView2;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                TemplateHostName,
                Path.Combine(AppContext.BaseDirectory, "Assets", "monaco"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += TemplateWebMessageReceived;
            core.Navigate($"https://{TemplateHostName}/template.html?v={DateTime.Now.Ticks}");
        }
        catch
        {
            // 编辑器初始化失败时保持空白，不影响其它设置项
        }
    }

    private void TemplateWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (message is null)
        {
            return;
        }

        switch (message["type"]?.GetValue<string>())
        {
            case "ready":
                _templateReady = true;
                SendTemplateToEditor(_settings.TemplateCode);
                break;

            case "templateChanged":
                _settings.TemplateCode = message["text"]?.GetValue<string>() ?? string.Empty;
                RaiseChanged();
                break;

            case "escape":
                CloseRequested?.Invoke();
                break;
        }
    }

    private void SendTemplateToEditor(string text)
    {
        _pendingTemplate = text;
        if (!_templateReady)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "setTemplate",
            text = _pendingTemplate,
        });
        TemplateWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// <summary>打开面板后把键盘焦点放到第一个输入框，Esc 才能生效。</summary>
    public void FocusFirst()
    {
        CompilerFlagsBox.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }
}
