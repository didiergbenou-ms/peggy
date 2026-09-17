using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;

#if PEGGY_INSTALLER
namespace Peggy.PrerequisiteCheck;
#else
namespace Peggy.Tray;
#endif

internal enum SetupStepState
{
    Idle,
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Skipped
}

internal sealed class SetupStepIndicator : Grid
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SetupStepState), typeof(SetupStepIndicator),
        new FrameworkPropertyMetadata(SetupStepState.Idle, OnVisualPropertyChanged),
        value => Enum.IsDefined(typeof(SetupStepState), value));

    public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
        nameof(AnimationEnabled), typeof(bool), typeof(SetupStepIndicator),
        new FrameworkPropertyMetadata(true, OnVisualPropertyChanged));

    private readonly RotateTransform _rotation = new();
    private readonly Path _spinner;
    private readonly TextBlock _symbol;

    public SetupStepIndicator()
    {
        Width = Height = 20;
        Margin = new Thickness(0, 3, 8, 0);
        VerticalAlignment = VerticalAlignment.Top;
        Focusable = false;
        _spinner = new Path
        {
            Data = Geometry.Parse("M 10,2 A 8,8 0 1 1 2,10"),
            Stroke = new SolidColorBrush(Color.FromRgb(50, 102, 155)),
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = _rotation,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed
        };
        _symbol = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Children.Add(_spinner);
        Children.Add(_symbol);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => RefreshVisual();
        RefreshVisual();
    }

    public SetupStepState State
    {
        get => (SetupStepState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    // An additional opt-out permits hosts to apply their own reduced-motion setting.
    public bool AnimationEnabled
    {
        get => (bool)GetValue(AnimationEnabledProperty);
        set => SetValue(AnimationEnabledProperty, value);
    }

    internal bool IsAnimating { get; private set; }

    private static void OnVisualPropertyChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
        => ((SetupStepIndicator)owner).RefreshVisual();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        RefreshVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        StopAnimation();
    }

    private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null or "")
        {
            if (Dispatcher.CheckAccess()) RefreshVisual();
            else Dispatcher.BeginInvoke(new Action(RefreshVisual));
        }
    }

    private void StopAnimation()
    {
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _rotation.Angle = 0;
        IsAnimating = false;
    }

    private void RefreshVisual()
    {
        var (name, glyph, color) = State switch
        {
            SetupStepState.Running => ("Running", "", "#32669B"),
            SetupStepState.WaitingForUser => ("Waiting for you", "↗", "#805900"),
            SetupStepState.Succeeded => ("Succeeded", "✓", "#26723C"),
            SetupStepState.Failed => ("Failed — retry", "⚠", "#9A4E00"),
            SetupStepState.Skipped => ("Skipped", "–", "#626262"),
            _ => ("Not started", "○", "#626262")
        };
        _symbol.Text = glyph;
        _symbol.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        _symbol.Visibility = State == SetupStepState.Running ? Visibility.Collapsed : Visibility.Visible;
        _spinner.Visibility = State == SetupStepState.Running ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetItemStatus(this, name);
        ToolTip = name;
        var animate = State == SetupStepState.Running && IsLoaded && IsVisible &&
            AnimationEnabled && SystemParameters.ClientAreaAnimation;
        if (!animate) StopAnimation();
        else if (!IsAnimating)
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
            IsAnimating = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StepAutomationPeer(this);

    private sealed class StepAutomationPeer(SetupStepIndicator owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SetupStepIndicator);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
