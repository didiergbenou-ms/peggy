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
        double x = SystemParameters.WorkArea.Right - w + 24; // sit all the way to the right edge
        double y = SystemParameters.WorkArea.Bottom - h + 18; // feet rest on the taskbar top
        return new Point(x, y);
    }

    private void PositionOnTaskbar()
    {
        var r = Rest();
        Left = r.X;
        Top = r.Y;
    }

    /// <summary>Screen point (DIP) just above Pippa's head, for speech-bubble anchoring.</summary>
    public Point HeadAnchor() => new(Left + ActualWidth * 0.52, Top + ActualHeight * 0.12);

    /// <summary>Swoop in from the top-right and land on the taskbar with a bounce.</summary>
    public void ShowAnimated()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();

        var r = Rest();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        double sx = r.X + 240, sy = r.Y - 340;
        Left = sx;
        Top = sy;
        const double dur = 1.05;

        // Glide left into place by ~65%, then drop the final stretch straight down onto the taskbar.
        var lx = new DoubleAnimationUsingKeyFrames();
        lx.KeyFrames.Add(new LinearDoubleKeyFrame(sx, KeyTime.FromPercent(0)));
        lx.KeyFrames.Add(new EasingDoubleKeyFrame(r.X, KeyTime.FromPercent(0.65)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        lx.KeyFrames.Add(new LinearDoubleKeyFrame(r.X, KeyTime.FromPercent(1.0)));

        var ty = new DoubleAnimationUsingKeyFrames();
        ty.KeyFrames.Add(new LinearDoubleKeyFrame(sy, KeyTime.FromPercent(0)));
        ty.KeyFrames.Add(new EasingDoubleKeyFrame(r.Y - 130, KeyTime.FromPercent(0.4)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        ty.KeyFrames.Add(new EasingDoubleKeyFrame(r.Y - 150, KeyTime.FromPercent(0.6)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        ty.KeyFrames.Add(new EasingDoubleKeyFrame(r.Y, KeyTime.FromPercent(1.0)) { EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 4 } });
        ty.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = r.X;
            Top = r.Y;
            BodyTilt.BeginAnimation(RotateTransform.AngleProperty, null);
            BodyTilt.Angle = 0;
        };

        BeginAnimation(LeftProperty, lx);
        BeginAnimation(TopProperty, ty);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)));

        // Lean into the dive, then level out to land.
        var tilt = new DoubleAnimationUsingKeyFrames();
        tilt.KeyFrames.Add(new LinearDoubleKeyFrame(-16, KeyTime.FromPercent(0)));
        tilt.KeyFrames.Add(new EasingDoubleKeyFrame(-8, KeyTime.FromPercent(0.55)));
        tilt.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        BodyTilt.BeginAnimation(RotateTransform.AngleProperty, tilt);

        ContinuousFlap(dur);
    }

    /// <summary>Lift straight up, then peel away off the top-right, wings flapping, then hide.</summary>
    public void HideAnimated()
    {
        var r = Rest();
        const double dur = 0.95;

        // Phase 1 (to 30%): rise up in place. Phase 2: accelerate up and off the top-right.
        var lx = new DoubleAnimationUsingKeyFrames();
        lx.KeyFrames.Add(new LinearDoubleKeyFrame(Left, KeyTime.FromPercent(0)));
        lx.KeyFrames.Add(new LinearDoubleKeyFrame(r.X, KeyTime.FromPercent(0.3)));
        lx.KeyFrames.Add(new EasingDoubleKeyFrame(r.X + 340, KeyTime.FromPercent(1.0)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });

        var ty = new DoubleAnimationUsingKeyFrames();
        ty.KeyFrames.Add(new LinearDoubleKeyFrame(Top, KeyTime.FromPercent(0)));
        ty.KeyFrames.Add(new EasingDoubleKeyFrame(r.Y - 70, KeyTime.FromPercent(0.3)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        ty.KeyFrames.Add(new EasingDoubleKeyFrame(r.Y - 460, KeyTime.FromPercent(1.0)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        BeginAnimation(LeftProperty, lx);
        BeginAnimation(TopProperty, ty);

        BodyTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -18, TimeSpan.FromSeconds(0.35)));
        ContinuousFlap(dur);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5)) { BeginTime = TimeSpan.FromSeconds(0.42) };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            BodyTilt.BeginAnimation(RotateTransform.AngleProperty, null);
            BodyTilt.Angle = 0;
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
        StopThinking();

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
                AnimateHead(-8);
                Bob(-3, 0.7);
                Thinking.Visibility = Visibility.Visible;
                StartThinking();
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

    private void StartThinking()
    {
        AnimateDot(Dot1, 0.0);
        AnimateDot(Dot2, 0.18);
        AnimateDot(Dot3, 0.36);
    }

    private static void AnimateDot(UIElement dot, double beginSeconds)
    {
        var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(0.42))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(beginSeconds)
        };
        dot.BeginAnimation(UIElement.OpacityProperty, a);
    }

    private void StopThinking()
    {
        Thinking.Visibility = Visibility.Collapsed;
        foreach (var d in new UIElement[] { Dot1, Dot2, Dot3 })
        {
            d.BeginAnimation(UIElement.OpacityProperty, null);
            d.Opacity = 1;
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
        var r = new DoubleAnimation(0, -36, TimeSpan.FromSeconds(0.1))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(flaps), EasingFunction = new SineEase() };
        var l = new DoubleAnimation(0, 36, TimeSpan.FromSeconds(0.1))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(flaps), EasingFunction = new SineEase() };
        WingRotate.BeginAnimation(RotateTransform.AngleProperty, r);
        WingRotateL.BeginAnimation(RotateTransform.AngleProperty, l);
    }

    private void ContinuousFlap(double seconds)
    {
        int flaps = Math.Max(3, (int)(seconds / 0.14));
        var r = new DoubleAnimation(-2, -44, TimeSpan.FromSeconds(0.07))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(flaps), EasingFunction = new SineEase() };
        var l = new DoubleAnimation(2, 44, TimeSpan.FromSeconds(0.07))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(flaps), EasingFunction = new SineEase() };
        WingRotate.BeginAnimation(RotateTransform.AngleProperty, r);
        WingRotateL.BeginAnimation(RotateTransform.AngleProperty, l);
    }

    private void Hop()
    {
        BodyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-26, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.48))) { EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 1, Bounciness = 3 } });
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
