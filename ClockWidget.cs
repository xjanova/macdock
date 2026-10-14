using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacDock
{
    public enum ClockStyle { Off, Analog, Digital }

    // Desktop clock widget: frameless, draggable, remembers its position. Right-click for options.
    public class ClockWidget : Window
    {
        public event Action HideRequested;

        static readonly CultureInfo Thai = new CultureInfo("th-TH");
        static readonly FontFamily Face = new FontFamily("Segoe UI Variable Display, Segoe UI");

        readonly Settings _s;
        readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Render);
        Action<DateTime, bool> _update; // (now, animate second hand)
        int _lastSec = -1;

        public ClockWidget(Settings s)
        {
            _s = s;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            Title = "MacDock Clock";
            Topmost = _s.ClockOnTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (_s.ClockX.HasValue && _s.ClockY.HasValue) { Left = _s.ClockX.Value; Top = _s.ClockY.Value; }

            SourceInitialized += (a, b) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_TOOLWINDOW));
            };
            Loaded += (a, b) => ClampToScreen();
            DateTime lastClick = DateTime.MinValue;
            MouseLeftButtonDown += (a, b) =>
            {
                // Double-click: reveal the taskbar's notification area (volume, network, tray apps).
                // Timed by hand: DragMove's modal loop eats the second click, so ClickCount rarely reaches 2.
                if (b.ClickCount == 2 || (DateTime.Now - lastClick).TotalMilliseconds <= Native.GetDoubleClickTime())
                {
                    lastClick = DateTime.MinValue;
                    b.Handled = true;
                    SystemTweaks.PeekTray();
                    return;
                }
                double l = Left, t = Top;
                try { DragMove(); } catch { }
                bool moved = Math.Abs(Left - l) > 2 || Math.Abs(Top - t) > 2;
                lastClick = moved ? DateTime.MinValue : DateTime.Now;
                if (moved) { _s.ClockX = Left; _s.ClockY = Top; _s.Save(); }
            };
            // WPF skips ContextMenuOpening's result when ContextMenu starts null, so seed one up front.
            ContextMenu = BuildMenu();
            ContextMenuOpening += (a, b) => ContextMenu = BuildMenu();
            _timer.Tick += (a, b) => Tick(true);
            Closed += (a, b) => { _timer.Stop(); _calendar?.Close(); };
            ApplyStyle();
        }

        public void ApplyStyle()
        {
            _lastSec = -1;
            Content = _s.Clock == ClockStyle.Digital ? BuildDigital() : BuildAnalog();
            Tick(false);
            _timer.Start();
        }

        void Tick(bool animate)
        {
            var now = DateTime.Now;
            bool next = _lastSec >= 0 && (now.Second - _lastSec + 60) % 60 == 1;
            _update?.Invoke(now, animate && next);
            _lastSec = now.Second;
            // Fire just after the next whole second so the display never lags behind the system clock.
            _timer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 15);
        }

        void ClampToScreen()
        {
            var wa = SystemParameters.WorkArea;
            // An auto-hidden dock does not shrink the work area, so keep the default spot clear of it.
            // Same thickness DockWindow gives its own window (Gap+Pad+S*zoom+Pad+S*0.5+6), so the default spot never touches it.
            double dock = _s.IconSize * ((_s.Magnify ? _s.Zoom : 1) + 0.5) + 28;
            double l = _s.ClockX ?? wa.Right - ActualWidth - 30 - (_s.Edge == DockEdge.Right ? dock : 0);
            double t = _s.ClockY ?? wa.Top + 30 + (_s.Edge == DockEdge.Top ? dock : 0);
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            Left = Math.Max(vl, Math.Min(l, vl + SystemParameters.VirtualScreenWidth - ActualWidth));
            Top = Math.Max(vt, Math.Min(t, vt + SystemParameters.VirtualScreenHeight - ActualHeight));
        }

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            m.Items.Add(DockWindow.Item("Analog (เข็ม)", () => SetStyle(ClockStyle.Analog), _s.Clock == ClockStyle.Analog));
            m.Items.Add(DockWindow.Item("Digital (ดิจิตอล)", () => SetStyle(ClockStyle.Digital), _s.Clock == ClockStyle.Digital));
            m.Items.Add(new Separator());
            m.Items.Add(DockWindow.Item("เปิดปฏิทิน", ShowCalendar));
            m.Items.Add(DockWindow.Item("แสดงไอคอนถาดระบบ (ดับเบิลคลิกนาฬิกา)", SystemTweaks.PeekTray));
            var set = new MenuItem { Header = "ตั้งเวลา" };
            set.Items.Add(DockWindow.Item("ตั้งปลุก / จับเวลา (Clock)", () => Brand.OpenUrl("ms-clock:alarm")));
            set.Items.Add(DockWindow.Item("ตั้งค่าวันและเวลาของ Windows", () => Brand.OpenUrl("ms-settings:dateandtime")));
            m.Items.Add(set);
            m.Items.Add(new Separator());
            m.Items.Add(DockWindow.Item("Always on top", () =>
            {
                _s.ClockOnTop = !_s.ClockOnTop;
                Topmost = _s.ClockOnTop;
                _s.Save();
            }, _s.ClockOnTop));
            m.Items.Add(DockWindow.Item("Hide clock", () => HideRequested?.Invoke()));
            return m;
        }

        Window _calendar;

        // Month calendar popup beside the clock; closes when it loses focus.
        void ShowCalendar()
        {
            if (_calendar != null) { _calendar.Activate(); return; }
            var cal = new System.Windows.Controls.Calendar { IsTodayHighlighted = true, DisplayDate = DateTime.Today, SelectedDate = DateTime.Today };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, Title = "ปฏิทิน",
                Content = new Border
                {
                    Child = new Viewbox { Child = cal, Width = 300 },
                    Background = Solid(0xF5, 0xFF, 0xFF, 0xFF), CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(10), Margin = new Thickness(12),
                    Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.45, Direction = 270 }
                }
            };
            w.Loaded += (a, b) =>
            {
                var wa = SystemParameters.WorkArea;
                double l = Left + (ActualWidth - w.ActualWidth) / 2, t = Top + ActualHeight - 8;
                if (t + w.ActualHeight > wa.Bottom) t = Top - w.ActualHeight + 8;
                w.Left = Math.Max(wa.Left, Math.Min(l, wa.Right - w.ActualWidth));
                w.Top = Math.Max(wa.Top, t);
            };
            w.Deactivated += (a, b) => w.Close();
            w.KeyDown += (a, b) => { if (b.Key == System.Windows.Input.Key.Escape) w.Close(); };
            w.Closed += (a, b) => _calendar = null;
            _calendar = w;
            w.Show();
            w.Activate();
        }

        void SetStyle(ClockStyle st)
        {
            _s.Clock = st;
            _s.Save();
            ApplyStyle();
        }

        static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        static LinearGradientBrush Linear(double angle, params (byte a, byte r, byte g, byte b, double at)[] stops)
        {
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0) };
            double rad = angle * Math.PI / 180;
            br.StartPoint = new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2);
            br.EndPoint = new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2);
            foreach (var st in stops) br.GradientStops.Add(new GradientStop(Color.FromArgb(st.a, st.r, st.g, st.b), st.at));
            br.Freeze();
            return br;
        }

        // ---------------------------------------------------------------- analog

        UIElement BuildAnalog()
        {
            const double D = 200, C = D / 2;
            var root = new Grid { Width = D, Height = D, Margin = new Thickness(16) };

            // Metallic bezel
            root.Children.Add(new Ellipse
            {
                Fill = Linear(90, (255, 0xF4, 0xF6, 0xFA, 0), (255, 0x8A, 0x90, 0xA0, 0.45), (255, 0x3A, 0x3F, 0x4C, 0.55), (255, 0xC8, 0xCD, 0xD8, 1)),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.5, Direction = 270 }
            });
            // Dial
            var faceBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.25), Center = new Point(0.5, 0.4), RadiusX = 0.8, RadiusY = 0.8
            };
            faceBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x34, 0x3B, 0x52), 0));
            faceBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x13, 0x1D), 1));
            faceBrush.Freeze();
            root.Children.Add(new Ellipse { Margin = new Thickness(5), Fill = faceBrush, Stroke = Solid(0x90, 0, 0, 0), StrokeThickness = 2 });

            // Minute / hour ticks
            var dial = new Canvas { Width = D, Height = D, IsHitTestVisible = false };
            var minor = Solid(0x70, 0xFF, 0xFF, 0xFF);
            var major = Solid(0xFF, 0xF2, 0xF4, 0xFA);
            for (int i = 0; i < 60; i++)
            {
                bool hour = i % 5 == 0;
                double w = hour ? 3 : 1.2, h = hour ? 11 : 5, top = 13;
                var r = new Rectangle { Width = w, Height = h, RadiusX = w / 2, RadiusY = w / 2, Fill = hour ? major : minor };
                Canvas.SetLeft(r, C - w / 2);
                Canvas.SetTop(r, top);
                r.RenderTransform = new RotateTransform(i * 6, w / 2, C - top);
                dial.Children.Add(r);
            }
            root.Children.Add(dial);

            // Numerals + brand + date window (at 3 o'clock)
            var labels = new Grid { IsHitTestVisible = false };
            void Label(string text, double x, double y, double size, Brush fg, FontWeight wt)
            {
                labels.Children.Add(new TextBlock
                {
                    Text = text, FontFamily = Face, FontSize = size, FontWeight = wt, Foreground = fg,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new TranslateTransform(x, y)
                });
            }
            var numFg = Solid(0xFF, 0xE8, 0xEC, 0xF5);
            Label("12", 0, -C + 40, 20, numFg, FontWeights.Light);
            Label("6", 0, C - 40, 20, numFg, FontWeights.Light);
            Label("9", -C + 38, 0, 20, numFg, FontWeights.Light);
            var dateText = new TextBlock { FontFamily = Face, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Solid(0xFF, 0xFF, 0x9F, 0x0A) };
            labels.Children.Add(new Border
            {
                Child = dateText, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                Background = Solid(0xFF, 0x0A, 0x0C, 0x13), BorderBrush = Solid(0x50, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform(C - 42, 0)
            });
            root.Children.Add(labels);

            // Brand logo above the centre, below the hands so they still sweep over it
            var logo = Brand.GlowingLogo(22);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            logo.VerticalAlignment = VerticalAlignment.Center;
            logo.RenderTransform = new TranslateTransform(0, -32);
            root.Children.Add(logo);

            // Hands
            var hands = new Canvas
            {
                Width = D, Height = D, IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 3, Opacity = 0.6, Direction = 300 }
            };
            RotateTransform Hand(double w, double len, double tail, Brush fill)
            {
                var r = new Rectangle { Width = w, Height = len + tail, RadiusX = w / 2, RadiusY = w / 2, Fill = fill };
                Canvas.SetLeft(r, C - w / 2);
                Canvas.SetTop(r, C - len);
                var rt = new RotateTransform(0, w / 2, len);
                r.RenderTransform = rt;
                hands.Children.Add(r);
                return rt;
            }
            var handFill = Linear(0, (255, 0xFF, 0xFF, 0xFF, 0), (255, 0xC9, 0xCF, 0xDC, 1));
            var orange = Solid(0xFF, 0xFF, 0x9F, 0x0A);
            var hourRt = Hand(6, 50, 10, handFill);
            var minRt = Hand(4, 74, 12, handFill);
            var secRt = Hand(1.6, 84, 22, orange);
            void Dot(double size, Brush fill)
            {
                var e = new Ellipse { Width = size, Height = size, Fill = fill };
                Canvas.SetLeft(e, C - size / 2);
                Canvas.SetTop(e, C - size / 2);
                hands.Children.Add(e);
            }
            Dot(11, orange);
            Dot(4, Solid(0xFF, 0x10, 0x13, 0x1D));
            root.Children.Add(hands);

            // Glass reflection
            root.Children.Add(new Ellipse
            {
                Width = D * 0.78, Height = D * 0.46, Margin = new Thickness(0, 9, 0, 0), IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
                Fill = Linear(90, (0x26, 0xFF, 0xFF, 0xFF, 0), (0x00, 0xFF, 0xFF, 0xFF, 1))
            });

            _update = (now, animate) =>
            {
                dateText.Text = now.Day.ToString();
                double min = now.Minute + now.Second / 60.0;
                hourRt.Angle = (now.Hour % 12 + min / 60) * 30;
                minRt.Angle = min * 6;
                double to = now.Second * 6;
                if (animate)
                {
                    secRt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(to - 6, to, TimeSpan.FromMilliseconds(280))
                    {
                        EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut }
                    });
                }
                else
                {
                    secRt.BeginAnimation(RotateTransform.AngleProperty, null);
                    secRt.Angle = to;
                }
            };
            return root;
        }

        // ---------------------------------------------------------------- digital

        UIElement BuildDigital()
        {
            var neon = Color.FromRgb(0x5E, 0xF2, 0xFF);
            var timeBrush = Linear(0, (255, 0x5E, 0xF2, 0xFF, 0), (255, 0xB5, 0x7C, 0xFF, 1));
            var dimBrush = Solid(0x40, 0x9A, 0xB8, 0xFF);

            var hh = new Run();
            var colon = new Run(":");
            var mm = new Run();
            var time = new TextBlock
            {
                FontFamily = Face, FontSize = 72, FontWeight = FontWeights.Light, Foreground = timeBrush,
                Effect = new DropShadowEffect { Color = neon, ShadowDepth = 0, BlurRadius = 18, Opacity = 0.8 }
            };
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            time.Inlines.Add(hh);
            time.Inlines.Add(colon);
            time.Inlines.Add(mm);

            var sec = new TextBlock
            {
                FontFamily = Face, FontSize = 24, FontWeight = FontWeights.Normal,
                Foreground = Solid(0xFF, 0xB5, 0x7C, 0xFF), Margin = new Thickness(8, 0, 0, 16),
                VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 30
            };
            Typography.SetNumeralAlignment(sec, FontNumeralAlignment.Tabular);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(time);
            row.Children.Add(sec);

            var date = new TextBlock
            {
                FontFamily = Face, FontSize = 15, Foreground = Solid(0xFF, 0xA8, 0xBC, 0xD4), Margin = new Thickness(3, -6, 0, 0)
            };

            var fill = new Border
            {
                CornerRadius = new CornerRadius(1.5), Background = timeBrush,
                RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1),
                Effect = new DropShadowEffect { Color = neon, ShadowDepth = 0, BlurRadius = 8, Opacity = 0.9 }
            };
            var bar = new Grid { Height = 3, Margin = new Thickness(2, 12, 2, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Solid(0x26, 0xFF, 0xFF, 0xFF) });
            bar.Children.Add(fill);

            var dateRow = new DockPanel { LastChildFill = true };
            var logo = Brand.GlowingLogo(22);
            logo.Margin = new Thickness(12, -6, 0, 0);
            logo.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(logo, Dock.Right);
            dateRow.Children.Add(logo);
            dateRow.Children.Add(date);

            var stack = new StackPanel();
            stack.Children.Add(row);
            stack.Children.Add(dateRow);
            stack.Children.Add(bar);

            var card = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(24, 6, 24, 18),
                Margin = new Thickness(16),
                Background = Linear(35, (0xEB, 0x0D, 0x11, 0x1E, 0), (0xEB, 0x1C, 0x15, 0x34, 1)),
                BorderBrush = Linear(0, (0xA0, 0x5E, 0xF2, 0xFF, 0), (0xA0, 0xB5, 0x7C, 0xFF, 1)),
                BorderThickness = new Thickness(1.2),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.5, Direction = 270 }
            };

            _update = (now, animate) =>
            {
                hh.Text = now.ToString("HH");
                mm.Text = now.ToString("mm");
                colon.Foreground = now.Second % 2 == 0 ? timeBrush : dimBrush;
                sec.Text = now.ToString("ss");
                date.Text = now.ToString("dddd d MMMM", Thai);
                double from = (now.Second + now.Millisecond / 1000.0) / 60, to = (now.Second + 1) / 60.0;
                fill.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, 1000 - now.Millisecond))));
            };
            return card;
        }
    }
}
