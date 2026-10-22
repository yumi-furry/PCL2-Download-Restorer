using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

public partial class MyButton : Border
{
    public enum ColorState { Normal = 0, Highlight = 1, Red = 2 }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register("Text", typeof(string), typeof(MyButton),
            new PropertyMetadata("", (s, e) => ((MyButton)s).LabText.Text = (string)e.NewValue));

    public string Text
    {
        get { return (string)GetValue(TextProperty); }
        set { SetValue(TextProperty, value); }
    }

    private ColorState _colorType = ColorState.Normal;
    public ColorState ColorType
    {
        get { return _colorType; }
        set { _colorType = value; RefreshColor(); }
    }

    public event EventHandler Click;

    private bool _isMouseDown;

    public MyButton()
    {
        InitializeComponent();
        // 资源字典里的 Brush 是冻结的，无法做动画；替换为可写副本
        PanFore.BorderBrush = new SolidColorBrush(((SolidColorBrush)FindResource("ColorBrush4")).Color);
        PanFore.Background = new SolidColorBrush(((SolidColorBrush)FindResource("ColorBrushHalfWhite")).Color);
        MouseEnter += (s, e) => { AnimateBackground(true); RefreshColor(); };
        MouseLeave += (s, e) => { AnimateBackground(false); RefreshColor(); if (_isMouseDown) { _isMouseDown = false; AnimateScale(1.0, 300); } };
        MouseLeftButtonDown += (s, e) => { _isMouseDown = true; Focus(); AnimateScale(0.955, 80); };
        MouseLeftButtonUp += (s, e) =>
        {
            if (!_isMouseDown) return;
            _isMouseDown = false;
            AnimateScale(1.0, 300);
            if (IsMouseOver) { var h = Click; if (h != null) h(this, EventArgs.Empty); }
        };
    }

    private void RefreshColor()
    {
        string res;
        if (!IsEnabled) res = "ColorBrushGray4";
        else if (IsMouseOver) res = "ColorBrush3";
        else if (_colorType == ColorState.Highlight) res = "ColorBrush2";
        else if (_colorType == ColorState.Red) res = "ColorBrushRedDark";
        else res = "ColorBrush1";

        if (IsMouseOver && _colorType == ColorState.Red) res = "ColorBrushRedLight";
        if (!IsMouseOver && _colorType == ColorState.Red) res = "ColorBrushRedDark";

        var brush = (Brush)FindResource(res);
        var anim = new ColorAnimation(((SolidColorBrush)brush).Color, TimeSpan.FromMilliseconds(150));
        var sb = PanFore.BorderBrush as SolidColorBrush;
        if (sb != null && !sb.IsFrozen) sb.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        else { var nb = new SolidColorBrush(((SolidColorBrush)brush).Color); PanFore.BorderBrush = nb; }
    }

    private void AnimateBackground(bool enter)
    {
        string res = _colorType == ColorState.Red ? "ColorBrushRedBack" : (enter ? "ColorBrush7" : "ColorBrushHalfWhite");
        var brush = (Brush)FindResource(res);
        var sb = PanFore.Background as SolidColorBrush;
        if (sb != null && !sb.IsFrozen) sb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(((SolidColorBrush)brush).Color, TimeSpan.FromMilliseconds(150)));
        else { var nb = new SolidColorBrush(((SolidColorBrush)brush).Color); PanFore.Background = nb; }
    }

    private void AnimateScale(double to, int ms)
    {
        var st = PanFore.RenderTransform as ScaleTransform;
        if (st == null) { st = new ScaleTransform(); PanFore.RenderTransform = st; }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
