using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace WinUI3Shell;

/// <summary>
/// 调试控制窗口：独立 WinUI 窗口，显示单步控制、局部变量和调用栈。
/// 左侧栏只负责监视表达式；关闭窗口时隐藏而不销毁，方便再次唤出。
/// </summary>
public sealed partial class DebugWindow : Window
{
    private bool _allowClose;

    public event Action? StartRequested;

    public event Action? StopRequested;

    public event Action? StepOverRequested;

    public event Action? StepIntoRequested;

    public event Action? StepOutRequested;

    public event Action<DebugFrameItem>? FrameActivated;

    public DebugWindow()
    {
        InitializeComponent();

        Title = "调试控制";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(440, 420));
        AppWindow.Closing += AppWindow_Closing;
    }

    /// <summary>显示并激活窗口（首次显示或从隐藏状态恢复）。</summary>
    public void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void SetLocals(IReadOnlyList<DebugVariableItem> items) => LocalsList.ItemsSource = items;

    public void SetFrames(IReadOnlyList<DebugFrameItem> items) => StackList.ItemsSource = items;

    /// <summary>主窗口退出时真正关闭本窗口。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        // 用户点关闭按钮时只隐藏，保留窗口实例和其中的调试状态
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke();

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void StepOverButton_Click(object sender, RoutedEventArgs e) => StepOverRequested?.Invoke();

    private void StepIntoButton_Click(object sender, RoutedEventArgs e) => StepIntoRequested?.Invoke();

    private void StepOutButton_Click(object sender, RoutedEventArgs e) => StepOutRequested?.Invoke();

    private void StackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DebugFrameItem item)
        {
            FrameActivated?.Invoke(item);
        }
    }

    private void Shortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        switch (sender.Key)
        {
            case Windows.System.VirtualKey.F5 when sender.Modifiers == Windows.System.VirtualKeyModifiers.Shift:
                StopRequested?.Invoke();
                break;

            case Windows.System.VirtualKey.F5:
                StartRequested?.Invoke();
                break;

            case Windows.System.VirtualKey.F7:
                StepOverRequested?.Invoke();
                break;

            case Windows.System.VirtualKey.F8:
                StepIntoRequested?.Invoke();
                break;
        }
    }
}

public sealed class DebugVariableItem
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

public sealed class DebugFrameItem
{
    public string Text { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public int Line { get; init; }

    public int Level { get; init; }
}
