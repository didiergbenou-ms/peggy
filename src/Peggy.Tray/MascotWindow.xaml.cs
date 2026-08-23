using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Peggy.Tray;

public enum PeggyState
{
    Idle,
    Listening,
    Thinking,
    NewDigest,
    Sleeping,
    Waking
}

public partial class MascotWindow : Window
{
    private readonly DispatcherTimer _blink =
        new() { Interval = TimeSpan.FromSeconds(2.3) };
    private readonly DispatcherTimer _flap =
        new() { Interval = TimeSpan.FromSeconds(2.0) };
    private readonly DispatcherTimer _hop =
        new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly DispatcherTimer _wake =
        new() { Interval = TimeSpan.FromSeconds(1.45) };
    private readonly Random _rng = new();
    private PeggyState _state = PeggyState.Idle;

    public MascotWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionOnTaskbar();
        MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was released before WPF began the drag.
            }
        };

        _blink.Tick += (_, _) =>
        {
            if (_state is not PeggyState.Sleeping and
                not PeggyState.Waking)
            {
                Blink(_rng.NextDouble() < 0.35);
            }
        };
        _flap.Tick += (_, _) =>
        {
            if (_state == PeggyState.NewDigest)
            {
                FlapBurst(2);
            }
        };
        _hop.Tick += (_, _) =>
        {
            if (_state == PeggyState.NewDigest)
            {
                Hop();
            }
        };
        _wake.Tick += (_, _) =>
        {
            _wake.Stop();
            if (_state == PeggyState.Waking)
            {
                WakingCompleted?.Invoke();
            }
        };
        _blink.Start();
        _flap.Start();
        _hop.Start();

        StartTailWiggle();
        StartCrestWiggle();
    }

    public event Action? WakingCompleted;

    public PeggyState CurrentState => _state;

    private Point Rest()
    {
        var width = ActualWidth > 0 ? ActualWidth : 140;
        var height = ActualHeight > 0 ? ActualHeight : 160;
        var x = SystemParameters.WorkArea.Right - width + 24;
        var y = SystemParameters.WorkArea.Bottom - height + 18;
        return new Point(x, y);
    }

    private void PositionOnTaskbar()
    {
        var rest = Rest();
        Left = rest.X;
        Top = rest.Y;
    }

    public Point HeadAnchor()
        => new(
            Left + (ActualWidth * 0.52),
            Top + (ActualHeight * 0.12));

    public void ShowAnimated()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();

        var rest = Rest();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        var startX = rest.X + 240;
        var startY = rest.Y - 340;
        Left = startX;
        Top = startY;
        const double duration = 1.05;

        var horizontal = new DoubleAnimationUsingKeyFrames();
        horizontal.KeyFrames.Add(
            new LinearDoubleKeyFrame(startX, KeyTime.FromPercent(0)));
        horizontal.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.X,
                KeyTime.FromPercent(0.65))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        horizontal.KeyFrames.Add(
            new LinearDoubleKeyFrame(rest.X, KeyTime.FromPercent(1)));

        var vertical = new DoubleAnimationUsingKeyFrames();
        vertical.KeyFrames.Add(
            new LinearDoubleKeyFrame(startY, KeyTime.FromPercent(0)));
        vertical.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.Y - 130,
                KeyTime.FromPercent(0.4))
            {
                EasingFunction = new SineEase
                {
                    EasingMode = EasingMode.EaseInOut
                }
            });
        vertical.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.Y - 150,
                KeyTime.FromPercent(0.6))
            {
                EasingFunction = new SineEase
                {
                    EasingMode = EasingMode.EaseInOut
                }
            });
        vertical.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.Y,
                KeyTime.FromPercent(1))
            {
                EasingFunction = new BounceEase
                {
                    EasingMode = EasingMode.EaseOut,
                    Bounces = 2,
                    Bounciness = 4
                }
            });
        vertical.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = rest.X;
            Top = rest.Y;
            BodyTilt.BeginAnimation(
                RotateTransform.AngleProperty,
                null);
            BodyTilt.Angle = 0;
        };

        BeginAnimation(LeftProperty, horizontal);
        BeginAnimation(TopProperty, vertical);
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                1,
                TimeSpan.FromSeconds(0.2)));

        var tilt = new DoubleAnimationUsingKeyFrames();
        tilt.KeyFrames.Add(
            new LinearDoubleKeyFrame(-16, KeyTime.FromPercent(0)));
        tilt.KeyFrames.Add(
            new EasingDoubleKeyFrame(-8, KeyTime.FromPercent(0.55)));
        tilt.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                0,
                KeyTime.FromPercent(1))
            {
                EasingFunction = new SineEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        BodyTilt.BeginAnimation(
            RotateTransform.AngleProperty,
            tilt);

        ContinuousFlap(duration);
    }

    public void HideAnimated()
    {
        var rest = Rest();
        const double duration = 0.95;

        var horizontal = new DoubleAnimationUsingKeyFrames();
        horizontal.KeyFrames.Add(
            new LinearDoubleKeyFrame(Left, KeyTime.FromPercent(0)));
        horizontal.KeyFrames.Add(
            new LinearDoubleKeyFrame(rest.X, KeyTime.FromPercent(0.3)));
        horizontal.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.X + 340,
                KeyTime.FromPercent(1))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            });

        var vertical = new DoubleAnimationUsingKeyFrames();
        vertical.KeyFrames.Add(
            new LinearDoubleKeyFrame(Top, KeyTime.FromPercent(0)));
        vertical.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.Y - 70,
                KeyTime.FromPercent(0.3))
            {
                EasingFunction = new SineEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        vertical.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                rest.Y - 460,
                KeyTime.FromPercent(1))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            });
        BeginAnimation(LeftProperty, horizontal);
        BeginAnimation(TopProperty, vertical);

        BodyTilt.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(
                0,
                -18,
                TimeSpan.FromSeconds(0.35)));
        ContinuousFlap(duration);

        var fade = new DoubleAnimation(
            1,
            0,
            TimeSpan.FromSeconds(0.5))
        {
            BeginTime = TimeSpan.FromSeconds(0.42)
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            BodyTilt.BeginAnimation(
                RotateTransform.AngleProperty,
                null);
            BodyTilt.Angle = 0;
            var currentRest = Rest();
            Left = currentRest.X;
            Top = currentRest.Y;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void SetState(PeggyState state)
    {
        _wake.Stop();
        _state = state;
        Ripples.Visibility = Visibility.Collapsed;
        StopThinking();
        StopSleeping();
        ResetEyesAndWings();

        switch (state)
        {
            case PeggyState.Idle:
                StartBlinking();
                AnimateHead(0);
                Bob(-7, 0.7);
                break;
            case PeggyState.Listening:
                StartBlinking();
                AnimateHead(-13);
                Bob(-4, 1.4);
                Ripples.Visibility = Visibility.Visible;
                PulseRipples();
                break;
            case PeggyState.Thinking:
                StartBlinking();
                AnimateHead(-8);
                Bob(-3, 0.7);
                Thinking.Visibility = Visibility.Visible;
                StartThinking();
                break;
            case PeggyState.NewDigest:
                StartBlinking();
                AnimateHead(0);
                FlapBurst(4);
                Hop();
                break;
            case PeggyState.Sleeping:
                _blink.Stop();
                AnimateHead(8);
                EyeScale.ScaleY = 0.08;
                WingRotateL.Angle = 14;
                WingRotate.Angle = -14;
                SleepOverlay.Visibility = Visibility.Visible;
                AnimateSleepOverlay();
                Bob(-2, 2.2);
                break;
            case PeggyState.Waking:
                StartWaking();
                break;
        }
    }

    private void ResetEyesAndWings()
    {
        EyeScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        EyeScale.ScaleY = 1;
        WingRotateL.BeginAnimation(
            RotateTransform.AngleProperty,
            null);
        WingRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            null);
        WingRotateL.Angle = 0;
        WingRotate.Angle = 0;
    }

    private void StartBlinking()
    {
        if (!_blink.IsEnabled)
        {
            _blink.Start();
        }
    }

    private void StartWaking()
    {
        _blink.Stop();
        EyeScale.ScaleY = 0.08;
        var openEyes = new DoubleAnimation(
            0.08,
            1,
            TimeSpan.FromSeconds(0.35))
        {
            BeginTime = TimeSpan.FromSeconds(0.18),
            EasingFunction = new BackEase
            {
                EasingMode = EasingMode.EaseOut,
                Amplitude = 0.35
            }
        };
        EyeScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            openEyes);
        AnimateHead(0);
        Perk();
        FlapBurst(3);
        _wake.Start();
    }

    private void StartThinking()
    {
        AnimateDot(Dot1, 0);
        AnimateDot(Dot2, 0.18);
        AnimateDot(Dot3, 0.36);
    }

    private static void AnimateDot(
        UIElement dot,
        double beginSeconds)
    {
        var animation = new DoubleAnimation(
            0.25,
            1,
            TimeSpan.FromSeconds(0.42))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(beginSeconds)
        };
        dot.BeginAnimation(
            UIElement.OpacityProperty,
            animation);
    }

    private void StopThinking()
    {
        Thinking.Visibility = Visibility.Collapsed;
        foreach (var dot in new UIElement[] { Dot1, Dot2, Dot3 })
        {
            dot.BeginAnimation(
                UIElement.OpacityProperty,
                null);
            dot.Opacity = 1;
        }
    }

    private void AnimateSleepOverlay()
    {
        AnimateSleepZ(SleepZ1, 0);
        AnimateSleepZ(SleepZ2, 0.45);
        AnimateSleepZ(SleepZ3, 0.9);
    }

    private static void AnimateSleepZ(
        UIElement element,
        double beginSeconds)
    {
        var animation = new DoubleAnimation(
            0.2,
            1,
            TimeSpan.FromSeconds(1.2))
        {
            BeginTime = TimeSpan.FromSeconds(beginSeconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        element.BeginAnimation(
            UIElement.OpacityProperty,
            animation);
    }

    private void StopSleeping()
    {
        SleepOverlay.Visibility = Visibility.Collapsed;
        foreach (var element in new UIElement[]
                 {
                     SleepZ1,
                     SleepZ2,
                     SleepZ3
                 })
        {
            element.BeginAnimation(
                UIElement.OpacityProperty,
                null);
            element.Opacity = 1;
        }
    }

    private void StartTailWiggle()
    {
        var animation = new DoubleAnimation(
            -8,
            8,
            TimeSpan.FromSeconds(0.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        TailRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            animation);
    }

    private void StartCrestWiggle()
    {
        var animation = new DoubleAnimation(
            -6,
            6,
            TimeSpan.FromSeconds(0.65))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        CrestRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            animation);
    }

    private void FlapBurst(int flaps)
    {
        var right = new DoubleAnimation(
            0,
            -36,
            TimeSpan.FromSeconds(0.1))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(flaps),
            EasingFunction = new SineEase()
        };
        var left = new DoubleAnimation(
            0,
            36,
            TimeSpan.FromSeconds(0.1))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(flaps),
            EasingFunction = new SineEase()
        };
        WingRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            right);
        WingRotateL.BeginAnimation(
            RotateTransform.AngleProperty,
            left);
    }

    private void ContinuousFlap(double seconds)
    {
        var flaps = Math.Max(3, (int)(seconds / 0.14));
        var right = new DoubleAnimation(
            -2,
            -44,
            TimeSpan.FromSeconds(0.07))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(flaps),
            EasingFunction = new SineEase()
        };
        var left = new DoubleAnimation(
            2,
            44,
            TimeSpan.FromSeconds(0.07))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(flaps),
            EasingFunction = new SineEase()
        };
        WingRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            right);
        WingRotateL.BeginAnimation(
            RotateTransform.AngleProperty,
            left);
    }

    private void Hop()
    {
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(
            new LinearDoubleKeyFrame(
                0,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
        hop.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                -26,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        hop.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                0,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.48)))
            {
                EasingFunction = new BounceEase
                {
                    EasingMode = EasingMode.EaseOut,
                    Bounces = 1,
                    Bounciness = 3
                }
            });
        hop.Completed += (_, _) =>
        {
            if (_state == PeggyState.NewDigest)
            {
                Bob(-7, 0.7);
            }
        };
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            hop);
        FlapBurst(2);
    }

    private void Bob(double toY, double seconds)
    {
        var animation = new DoubleAnimation(
            0,
            toY,
            TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            animation);
    }

    private void Perk()
    {
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        var up = new DoubleAnimation(
            0,
            -18,
            TimeSpan.FromSeconds(0.16))
        {
            AutoReverse = true,
            EasingFunction = new BackEase
            {
                EasingMode = EasingMode.EaseOut,
                Amplitude = 0.8
            }
        };
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            up);
    }

    private void AnimateHead(double angle)
    {
        var animation = new DoubleAnimation(
            HeadRotate.Angle,
            angle,
            TimeSpan.FromSeconds(0.3))
        {
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        HeadRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            animation);
    }

    private void Blink(bool doubleBlink)
    {
        var animation = new DoubleAnimation(
            1,
            0.1,
            TimeSpan.FromSeconds(0.085))
        {
            AutoReverse = true,
            RepeatBehavior = doubleBlink
                ? new RepeatBehavior(2)
                : new RepeatBehavior(1)
        };
        EyeScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            animation);
    }

    private void PulseRipples()
    {
        var animation = new DoubleAnimation(
            0.25,
            1,
            TimeSpan.FromSeconds(0.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Ripples.BeginAnimation(
            UIElement.OpacityProperty,
            animation);
    }
}
