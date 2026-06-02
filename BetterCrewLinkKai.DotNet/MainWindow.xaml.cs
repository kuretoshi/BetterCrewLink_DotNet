using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterCrewLinkKai.DotNet.ViewModels;

namespace BetterCrewLinkKai.DotNet;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer overlayRefreshTimer;
    private OverlayWindow? overlayWindow;
    private MainWindowViewModel? currentViewModel;
    private bool? lastHardwareAcceleration;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        overlayRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        overlayRefreshTimer.Tick += (_, _) =>
        {
            ApplyHardwareAcceleration();
            RefreshOverlay();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.LoadCommand.CanExecute(null))
        {
            currentViewModel = viewModel;
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.LoadCommand.Execute(null);
            ApplyHardwareAcceleration();
            overlayRefreshTimer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        overlayRefreshTimer.Stop();
        if (currentViewModel is not null)
        {
            currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        overlayWindow?.Close();
        overlayWindow = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ShowVoiceView) or
            nameof(MainWindowViewModel.IsDiscussionView) or
            nameof(MainWindowViewModel.AlwaysOnTop) or
            nameof(MainWindowViewModel.EnableOverlay) or
            nameof(MainWindowViewModel.CompactOverlay) or
            nameof(MainWindowViewModel.MeetingOverlay) or
            nameof(MainWindowViewModel.OverlayPosition) or
            nameof(MainWindowViewModel.HardwareAcceleration) or
            nameof(MainWindowViewModel.Settings))
        {
            ApplyHardwareAcceleration();
            RefreshOverlay();
        }
    }

    private void ApplyHardwareAcceleration()
    {
        if (currentViewModel is null || lastHardwareAcceleration == currentViewModel.Settings.HardwareAcceleration)
        {
            return;
        }

        lastHardwareAcceleration = currentViewModel.Settings.HardwareAcceleration;
        System.Windows.Media.RenderOptions.ProcessRenderMode = currentViewModel.Settings.HardwareAcceleration
            ? RenderMode.Default
            : RenderMode.SoftwareOnly;
    }

    private void RefreshOverlay()
    {
        if (currentViewModel is null)
        {
            return;
        }

        var settings = currentViewModel.Settings;
        var shouldShow = settings.EnableOverlay &&
                         currentViewModel.ShowVoiceView &&
                         (settings.MeetingOverlay || !currentViewModel.IsDiscussionView);

        if (!shouldShow)
        {
            overlayWindow?.Hide();
            return;
        }

        overlayWindow ??= new OverlayWindow
        {
            Owner = this,
            DataContext = currentViewModel
        };

        overlayWindow.DataContext = currentViewModel;
        overlayWindow.ApplyCompactMode(settings.CompactOverlay);
        PositionOverlay(overlayWindow, settings.OverlayPosition);

        if (!overlayWindow.IsVisible)
        {
            overlayWindow.Show();
        }
    }

    private static void PositionOverlay(Window window, string overlayPosition)
    {
        var workArea = SystemParameters.WorkArea;
        var position = overlayPosition?.Trim().ToLowerInvariant() ?? string.Empty;
        var width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
        if (double.IsNaN(height) || height <= 1)
        {
            height = 180;
        }

        var isLeft = ContainsAny(position, "left", "左");
        var isBottom = ContainsAny(position, "bottom", "下");
        var isTop = ContainsAny(position, "top", "上");

        window.Left = isLeft
            ? workArea.Left + 12
            : workArea.Right - width - 12;

        if (isBottom)
        {
            window.Top = workArea.Bottom - height - 24;
            return;
        }

        window.Top = isTop ? workArea.Top + 24 : workArea.Top + 88;
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = FormatShortcutKey(key);
        if (shortcut is null)
        {
            return;
        }

        ApplyShortcut(sender, shortcut);
        e.Handled = true;
    }

    private void OnShortcutPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var shortcut = e.ChangedButton switch
        {
            MouseButton.XButton1 => "MouseButton4",
            MouseButton.XButton2 => "MouseButton5",
            _ => null
        };
        if (shortcut is null)
        {
            return;
        }

        ApplyShortcut(sender, shortcut);
        e.Handled = true;
    }

    private void ApplyShortcut(object sender, string shortcut)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string settingName)
        {
            return;
        }

        textBox.Text = shortcut;
        textBox.CaretIndex = textBox.Text.Length;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UpdateShortcut(settingName, shortcut);
        }
    }

    private static string? FormatShortcutKey(Key key)
    {
        return key switch
        {
            Key.Escape => "Disabled",
            Key.Space => "Space",
            Key.LeftCtrl => "LControl",
            Key.RightCtrl => "RControl",
            Key.LeftAlt => "LAlt",
            Key.RightAlt => "RAlt",
            Key.LeftShift => "LShift",
            Key.RightShift => "RShift",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Numpad{(int)key - (int)Key.NumPad0}",
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null
        };
    }
}

