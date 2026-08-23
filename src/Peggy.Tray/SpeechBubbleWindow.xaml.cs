using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Peggy.Tray;

public partial class SpeechBubbleWindow : Window
{
    private readonly DispatcherTimer _dismiss = new();

    public SpeechBubbleWindow()
    {
        InitializeComponent();
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); FadeOut(); };
    }

    /// <summary>Show a speech bubble whose tail points at <paramref name="tailTip"/> (screen DIP).</summary>
    public void Say(string text, Point tailTip, double seconds = 6)
    {
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
        _dismiss.Stop();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
