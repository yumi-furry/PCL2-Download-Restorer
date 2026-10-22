using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

public partial class MyIconButton : Border
{
    public enum Themes { Color, White, Black, Red, Custom }

    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register("Logo", typeof(string), typeof(MyIconButton),
            new PropertyMetadata("", (s, e) => ((MyIconButton)s).Path.Data = Geometry.Parse((string)e.NewValue)));

    public string Logo
    {
        get { return (string)GetValue(LogoProperty); }
        set { SetValue(LogoProperty, value); }
    }

    public static readonly DependencyProperty LogoScaleProperty =
        DependencyProperty.Register("LogoScale", typeof(double), typeof(MyIconButton),
            new PropertyMetadata(1.0, (s, e) =>
            {
                var b = (MyIconButton)s;
                var st = b.Path.RenderTransform as ScaleTransform;
                if (st == null) { st = new ScaleTransform(); b.Path.RenderTransform = st; }
                st.ScaleX = st.ScaleY = (double)e.NewValue;
            }));

    public double LogoScale
    {
        get { return (double)GetValue(LogoScaleProperty); }
        set { SetValue(LogoScaleProperty, value); }
    }

    private Themes _theme = Themes.Color;
    public Themes Theme { get { return _theme; } set { _theme = value; } }

    public event EventHandler Click;

    private bool _isMouseDown;

    public MyIconButton()
    {
        InitializeComponent();
        // 资源字典里的 Brush 是冻结的，无法做动画；替换为可写副本
        var pf = Path.Fill as SolidColorBrush;
        if (pf != null) Path.Fill = new SolidColorBrush(pf.Color);
        var pb = PanBack.Background as SolidColorBrush;
        if (pb != null) PanBack.Background = new SolidColorBrush(pb.Color);
        Loaded += (s, e) => RefreshColor();
        MouseEnter += (s, e) => RefreshColor();
        MouseLeave += (s, e) => { RefreshColor(); if (_isMouseDown) { _isMouseDown = false; AnimateScale(1.0); } };
        IsEnabledChanged += (s, e) => RefreshColor();
        MouseLeftButtonDown += (s, e) => { _isMouseDown = true; Focus(); AnimateScale(0.8); };
        MouseLeftButtonUp += (s, e) =>
        {
            if (!_isMouseDown) return;
            _isMouseDown = false;
            AnimateScale(1.05, () => AnimateScale(1.0));
            if (IsMouseOver) { var h = Click; if (h != null) h(this, EventArgs.Empty); }
        };
    }

    private void RefreshColor()
    {
        string fillRes;
        if (!IsEnabled) fillRes = "ColorBrushGray5";
        else if (IsMouseOver)
        {
            switch (Theme)
            {
                case Themes.Color: fillRes = "ColorBrush2"; break;
                case Themes.Red: fillRes = "ColorBrushRedLight"; break;
                case Themes.Black: fillRes = "ColorBrush1"; break;
                default: fillRes = "ColorBrush8"; break;
            }
        }
        else
        {
            switch (Theme)
            {
                case Themes.Color: fillRes = "ColorBrush5"; break;
                case Themes.White: fillRes = "ColorBrush8"; break;
                case Themes.Red: fillRes = "ColorBrushRedLight"; break;
                case Themes.Black: fillRes = "ColorBrush1"; break;
                default: fillRes = "ColorBrush5"; break;
            }
        }

        var brush = (Brush)FindResource(fillRes);
        var sb = Path.Fill as SolidColorBrush;
        if (sb != null && !sb.IsFrozen) sb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(((SolidColorBrush)brush).Color, TimeSpan.FromMilliseconds(120)));
        else Path.Fill = new SolidColorBrush(((SolidColorBrush)brush).Color);

        if (Theme == Themes.White)
        {
            var bg = PanBack.Background as SolidColorBrush;
            Color target = IsMouseOver ? Color.FromArgb(50, 255, 255, 255) : Colors.Transparent;
            if (bg != null && !bg.IsFrozen) bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(120)));
            else PanBack.Background = new SolidColorBrush(target);
        }
    }

    private void AnimateScale(double to, Action done = null)
    {
        var st = PanBack.RenderTransform as ScaleTransform;
        if (st == null) { st = new ScaleTransform(); PanBack.RenderTransform = st; }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) anim.Completed += (s, e) => done();
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
