using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Peggy.Tray;

public partial class SpeechBubbleWindow : Window
{
    private readonly DispatcherTimer _dismiss = new();
    private Action? _activate;
    private long _presentation;

    public SpeechBubbleWindow()
    {
        InitializeComponent();
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); FadeOut(); };
        Closed += (_, _) => ClearActivation();
    }

    /// <summary>Show a speech bubble whose tail points at <paramref name="tailTip"/> (screen DIP).</summary>
    public void Say(string text, Point tailTip, double seconds = 6, Action? onActivate = null)
    {
        _presentation++;
        _dismiss.Stop();
        _activate = onActivate;
        BubbleBodyButton.IsEnabled = onActivate is not null;
        BubbleBodyButton.ToolTip = onActivate is null ? null : "Open this alert in Peggy";
        Text.Text = text;
        Show();
        UpdateLayout();

        // The tail sits near the right edge; align its tip to tailTip.
        double tailX = ActualWidth - 38;
        Left = tailTip.X - tailX;
        Top = tailTip.Y - ActualHeight;
        if (Left < 8) Left = 8;
        if (Top < 8) Top = 8;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var pop = new DoubleAnimation(0.6, 1.0, TimeSpan.FromSeconds(0.28))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)));

        _dismiss.Interval = TimeSpan.FromSeconds(seconds);
        _dismiss.Stop();
        _dismiss.Start();
    }

    public void HideNow()
    {
        _presentation++;
        ClearActivation();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void ClearActivation()
    {
        _dismiss.Stop();
        _activate = null;
        BubbleBodyButton.IsEnabled = false;
        BubbleBodyButton.ToolTip = null;
    }

    private void BubbleBody_Click(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || _activate is null) return;
        var activate = _activate;
        HideNow();
        e.Handled = true;
        activate();
    }

    private void CloseBubble_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideNow();
    }

    private void Bubble_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        HideNow();
    }

    private void FadeOut()
    {
        var presentation = _presentation;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
        fade.Completed += (_, _) =>
        {
            if (presentation != _presentation) return;
            HideNow();
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
