using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pippa.Tray;

public enum PippaState
{
    Idle,
    Listening,
    Thinking,
    NewDigest
}

public partial class MascotWindow : Window
{
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromSeconds(2.3) };
    private readonly DispatcherTimer _flap = new() { Interval = TimeSpan.FromSeconds(2.0) };
    private readonly DispatcherTimer _hop = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly Random _rng = new();
    private PippaState _state = PippaState.Idle;

    public MascotWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionOnTaskbar();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        _blink.Tick += (_, _) => Blink(_rng.NextDouble() < 0.35);
        _flap.Tick += (_, _) => { if (_state is PippaState.Idle or PippaState.NewDigest) FlapBurst(2); };
        _hop.Tick += (_, _) => { if (_state == PippaState.Idle) Hop(); };
        _blink.Start();
        _flap.Start();
        _hop.Start();

        StartTailWiggle();
        StartCrestWiggle();
    }

    private Point Rest()
    {
        double w = ActualWidth > 0 ? ActualWidth : 140;
        double h = ActualHeight > 0 ? ActualHeight : 160;
        double x = SystemParameters.WorkArea.Right - w - 40;
        double y = SystemParameters.WorkArea.Bottom - h + 18; // feet rest on the taskbar top
        return new Point(x, y);
    }

    private void PositionOnTaskbar()
    {
        var r = Rest();
        Left = r.X;
        Top = r.Y;
    }

    /// <summary>Fly Pippa in from the right, wings flapping.</summary>
    public void ShowAnimated()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();

        var r = Rest();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = r.X + 480;
        Top = r.Y - 24;

        var lx = new DoubleAnimation(Left, r.X, TimeSpan.FromSeconds(0.62))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var ty = new DoubleAnimation(Top, r.Y, TimeSpan.FromSeconds(0.62))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        BeginAnimation(LeftProperty, lx);
        BeginAnimation(TopProperty, ty);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.22)));
        FlapBurst(5);
    }

    /// <summary>Fly Pippa up and away to the right, then hide.</summary>
    public void HideAnimated()
    {
        var r = Rest();
        var lx = new DoubleAnimation(Left, r.X + 500, TimeSpan.FromSeconds(0.5))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var ty = new DoubleAnimation(Top, r.Y - 90, TimeSpan.FromSeconds(0.5))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        BeginAnimation(LeftProperty, lx);
        BeginAnimation(TopProperty, ty);
        FlapBurst(6);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            var rr = Rest();
            Left = rr.X;
            Top = rr.Y;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Switch Pippa's animated state.</summary>
    public void SetState(PippaState state)
    {
        _state = state;
        Ripples.Visibility = Visibility.Collapsed;
        Envelope.Visibility = Visibility.Collapsed;
        Sparkles.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case PippaState.Idle:
                AnimateHead(0);
                Bob(-7, 0.7);
                break;
            case PippaState.Listening:
                AnimateHead(-13);
                Bob(-4, 1.4);
                Ripples.Visibility = Visibility.Visible;
                PulseRipples();
                break;
            case PippaState.Thinking:
                AnimateHead(0);
                Bob(-3, 0.45);
                break;
            case PippaState.NewDigest:
                AnimateHead(0);
                Perk();
                Envelope.Visibility = Visibility.Visible;
                Sparkles.Visibility = Visibility.Visible;
                PopIn(Envelope);
                PopIn(Sparkles);
                FlapBurst(4);
                break;
        }
    }

    private void StartTailWiggle()
    {
        var a = new DoubleAnimation(-8, 8, TimeSpan.FromSeconds(0.5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        TailRotate.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private void StartCrestWiggle()
    {
        var a = new DoubleAnimation(-6, 6, TimeSpan.FromSeconds(0.65))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        CrestRotate.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private void FlapBurst(int flaps)
    {
        var a = new DoubleAnimation(0, -36, TimeSpan.FromSeconds(0.1))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(flaps), EasingFunction = new SineEase() };
        WingRotate.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private void Hop()
    {
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-26, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.48)))
        { EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 1, Bounciness = 3 } });
        hop.Completed += (_, _) => { if (_state == PippaState.Idle) Bob(-7, 0.7); };
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, hop);
        FlapBurst(2);
    }

    private void Bob(double toY, double seconds)
    {
        var a = new DoubleAnimation(0, toY, TimeSpan.FromSeconds(seconds))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void Perk()
    {
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        var up = new DoubleAnimation(0, -18, TimeSpan.FromSeconds(0.16))
        { AutoReverse = true, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } };
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, up);
    }

    private void AnimateHead(double angle)
    {
        var a = new DoubleAnimation(HeadRotate.Angle, angle, TimeSpan.FromSeconds(0.3))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        HeadRotate.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private void Blink(bool doubleBlink)
    {
        var a = new DoubleAnimation(1, 0.1, TimeSpan.FromSeconds(0.085))
        { AutoReverse = true, RepeatBehavior = doubleBlink ? new RepeatBehavior(2) : new RepeatBehavior(1) };
        EyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void PulseRipples()
    {
        var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(0.7))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Ripples.BeginAnimation(UIElement.OpacityProperty, a);
    }

    private static void PopIn(UIElement el)
    {
        var st = new ScaleTransform(0, 0);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
