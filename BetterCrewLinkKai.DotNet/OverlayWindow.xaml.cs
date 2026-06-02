using System.Windows;

namespace BetterCrewLinkKai.DotNet;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void ApplyCompactMode(bool compact)
    {
        Width = compact ? 150 : 220;
        OverlayPanel.Padding = compact ? new Thickness(6) : new Thickness(9);
        LocalPlayerPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
