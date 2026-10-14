using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace MacDock
{
    public enum DockKind { Pinned, Running, Separator, Header }

    public class DockEntry
    {
        public DockKind Kind;
        public string Path, Resolved, Name, Key;
        public ImageSource Icon;
        public List<IntPtr> Windows = new List<IntPtr>();
        public bool IsRunning => Windows.Count > 0;
        public string Category = "";
        public bool Collapsed;
        public double HeaderLen;
        public double Size, Target;
        public Grid Root;
        public Image Img;
        public Ellipse Dot;
        public Border Line;
        public ToolTip Tip;
        public TranslateTransform Bounce, Slide;
        public Ellipse Glow;
        public System.Windows.Media.Effects.DropShadowEffect GlowFx;
        public bool Hover;
        public double GlowT = -1;
    }

    // Lays the scrolling panel out at its full size: a normal Grid gives it only the viewport size,
    // and WPF then layout-clips it in its own (scrolled) coordinates, hiding the icons beyond the first screenful.
    public class UnclippedGrid : Grid
    {
        protected override Size MeasureOverride(Size c)
        {
            double w = 0, h = 0;
            foreach (UIElement ch in InternalChildren)
            {
                ch.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = Math.Max(w, ch.DesiredSize.Width); h = Math.Max(h, ch.DesiredSize.Height);
            }
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size f)
        {
            foreach (UIElement ch in InternalChildren)
                ch.Arrange(new Rect(0, 0, Math.Max(f.Width, ch.DesiredSize.Width), Math.Max(f.Height, ch.DesiredSize.Height)));
            return f;
        }
    }

    public class DockWindow : Window
    {
        struct WinInfo { public IntPtr Hwnd; public string Path; public string Class; }

        const double Gap = 6, Pad = 8, ItemGap = 4, SepLen = 12, HandleLen = 16;

        readonly Settings _s;
        readonly List<DockEntry> _pinned = new List<DockEntry>();
        readonly Dictionary<string, DockEntry> _running = new Dictionary<string, DockEntry>();
        readonly Dictionary<string, string> _descCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<DockEntry> _entries = new List<DockEntry>();
        readonly DockEntry _separator = new DockEntry { Kind = DockKind.Separator, Key = "sep" };
        readonly Grid _root = new Grid();
        readonly Border _bar = new Border();
        readonly Border _grip = new Border();
        readonly StackPanel _gripDots = new StackPanel();
        readonly Grid _viewport = new UnclippedGrid();
        readonly TranslateTransform _scrollTx = new TranslateTransform();
        bool _overflow;
        Dictionary<string, double> _rank = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int _tickN;
        bool _usageDirty;
        double _scroll, _scrollTarget, _scrollMin;
        readonly StackPanel _panel = new StackPanel();
        readonly System.Windows.Threading.DispatcherTimer _timer = new System.Windows.Threading.DispatcherTimer();
        readonly System.Windows.Threading.DispatcherTimer _edgeTimer = new System.Windows.Threading.DispatcherTimer();
        readonly TranslateTransform _hideTx = new TranslateTransform();
        bool _hidden, _menuOpen, _dragging;
        DateTime _leftAt = DateTime.MaxValue;
        int _wx, _wy, _ww, _wh;
        double _edgeVel;

        IntPtr _hwnd;
        ClockWidget _clock;
        bool _appBarRegistered, _animating, _inited;
        string _sig = "";

        DockEntry _downEntry; Point _downPt;
        bool _barDown, _barMoving; Native.POINT _barStart;

        bool Horiz => _s.Edge == DockEdge.Bottom || _s.Edge == DockEdge.Top;
        double S => _s.IconSize;
        double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public DockWindow(Settings s)
        {
            _s = s;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            AllowDrop = true;
            UseLayoutRounding = true;
            Title = "MacDock";

            _bar.CornerRadius = new CornerRadius(18);
            _bar.BorderThickness = new Thickness(1);
            _bar.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            _bar.Background = new LinearGradientBrush(Color.FromArgb(150, 245, 247, 255), Color.FromArgb(95, 200, 210, 235), 90);
            _bar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Direction = 270 };
            // Drag handle at the start of the bar: grab it to move the dock to another screen edge.
            _grip.Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0));
            _grip.Cursor = Cursors.SizeAll;
            _gripDots.HorizontalAlignment = HorizontalAlignment.Center;
            _gripDots.VerticalAlignment = VerticalAlignment.Center;
            _gripDots.IsHitTestVisible = false;
            for (int i = 0; i < 3; i++)
                _gripDots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Margin = new Thickness(1.5), Fill = new SolidColorBrush(Color.FromArgb(150, 70, 78, 100)) });
            _grip.Child = _gripDots;
            _bar.Child = _grip;
            _panel.Background = Brushes.Transparent;
            _panel.RenderTransform = _scrollTx;
            _viewport.Children.Add(_panel);
            _viewport.SizeChanged += (a, b) => UpdateClip();
            _root.Children.Add(_bar);
            _root.Children.Add(_viewport);
            _root.RenderTransform = _hideTx;
            Content = _root;

            foreach (var p in _s.Pinned.ToList()) AddPinned(p, -1, false);
            if (!_s.Imported) { ImportDesktop(); _s.Imported = true; _s.Save(); }

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (a, b) => Tick();
            _edgeTimer.Interval = TimeSpan.FromMilliseconds(50);
            _edgeTimer.Tick += (a, b) => EdgeTick();

            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
                ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);
            };
            Loaded += (a, b) =>
            {
                _inited = true;
                ApplyAlignment();
                Tick();
                Place();
                ApplyAppBar();
                _timer.Start();
                _edgeTimer.Start();
                if (_s.Clock != ClockStyle.Off) SetClock(_s.Clock);
            };
            _rank = new Dictionary<string, double>(_s.Usage, StringComparer.OrdinalIgnoreCase);
            Closed += (a, b) => { _clock?.Close(); _clock = null; if (_usageDirty) _s.Save(); };
            DpiChanged += (a, b) => { Place(); ApplyAppBar(); };
            SystemEvents.DisplaySettingsChanged += (a, b) => Dispatcher.BeginInvoke(new Action(() => { Place(); ApplyAppBar(); }));

            MouseMove += OnMouseMoveWin;
            MouseWheel += OnWheel;
            // DoDragDrop raises MouseLeave on start; resetting scroll then would slide the dragged icon out of view.
            MouseLeave += (a, b) => { if (!_barDown && !_dragging) { ShrinkAll(); ResetScroll(); } };
            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((a, b) => _menuOpen = true), true);
            AddHandler(ContextMenuClosingEvent, new ContextMenuEventHandler((a, b) => _menuOpen = false), true);
            MouseLeftButtonDown += OnWinDown;
            MouseLeftButtonUp += OnWinUp;
            // WPF skips ContextMenuOpening's result when ContextMenu starts null, so seed one up front.
            ContextMenu = new ContextMenu();
            ContextMenuOpening += (a, b) => ContextMenu = BuildMenu(null);
        }

        // ---------------------------------------------------------------- entries

        void ImportDesktop()
        {
            foreach (var dir in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                try
                {
                    foreach (var f in Directory.GetFileSystemEntries(dir))
                    {
                        var name = System.IO.Path.GetFileName(f);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if ((File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        }
                        catch { }
                        AddPinned(f, -1, false);
                    }
                }
                catch { }
            }
            SavePinned();
        }

        string EnsureCat(string c)
        {
            if (string.IsNullOrEmpty(c)) return "";
            var ex = _s.Categories.FirstOrDefault(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase));
            if (ex != null) return ex;
            _s.Categories.Add(c);
            return c;
        }

        void AddPinned(string path, int index, bool save, string cat = null)
        {
            if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
            if (_pinned.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var e = new DockEntry
            {
                Kind = DockKind.Pinned,
                Path = path,
                Key = "p:" + path,
                Name = IconService.DisplayName(path),
                Icon = IconService.GetIcon(path),
                Resolved = IconService.ResolveShortcut(path),
                Category = EnsureCat(cat ?? (_s.PinCats.TryGetValue(path, out var pc) ? pc : ""))
            };
            if (index < 0 || index > _pinned.Count) _pinned.Add(e); else _pinned.Insert(index, e);
            if (save) { SavePinned(); _sig = ""; Tick(); }
        }

        void SavePinned()
        {
            _s.Pinned = _pinned.Select(p => p.Path).ToList();
            _s.PinCats = _pinned.Where(p => !string.IsNullOrEmpty(p.Category))
                                .ToDictionary(p => p.Path, p => p.Category, StringComparer.OrdinalIgnoreCase);
            _s.Save();
        }

        void Tick()
        {
            SystemTweaks.Enforce(_s.HideTaskbar, _s.HideDesktopIcons);
            if (_hwnd != IntPtr.Zero && !Topmost) Topmost = true;
            TrackUsage();
            Sync(_s.ShowRunning ? EnumerateWindows() : new List<WinInfo>());
        }

        // ---------------------------------------------------------------- smart order

        static string UsageKey(DockEntry e) => e.Resolved ?? e.Path;

        void AddUsage(string path, double v)
        {
            if (string.IsNullOrEmpty(path)) return;
            _s.Usage.TryGetValue(path, out var cur);
            _s.Usage[path] = cur + v;
            _usageDirty = true;
        }

        double Rank(DockEntry e)
        {
            string k = UsageKey(e);
            return k != null && _rank.TryGetValue(k, out var v) ? v : 0;
        }

        void TrackUsage()
        {
            _tickN++;
            try
            {
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(fg, out uint pid);
                    if (pid != (uint)Environment.ProcessId) AddUsage(Native.GetProcessPath(pid), 1);
                }
            }
            catch { }
            // Re-rank at startup and every 10 min, never while the pointer is on the dock (icons would jump).
            if ((_tickN == 1 || _tickN % 600 == 0) && !_bar.IsMouseOver) { _rank = new Dictionary<string, double>(_s.Usage, StringComparer.OrdinalIgnoreCase); _sig = ""; }
            if (_tickN % 30 == 0 && _usageDirty) { _usageDirty = false; _s.Save(); }
        }

        List<WinInfo> EnumerateWindows()
        {
            var list = new List<WinInfo>();
            uint self = (uint)Environment.ProcessId;
            var sb = new System.Text.StringBuilder(256);
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                if (Native.GetWindowTextLength(h) == 0) return true;
                if (Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == self) return true;
                sb.Clear();
                Native.GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
                string path = Native.GetProcessPath(pid);
                if (path == null) return true;
                list.Add(new WinInfo { Hwnd = h, Path = path, Class = cls });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        string Describe(string path)
        {
            if (_descCache.TryGetValue(path, out var d)) return d;
            try
            {
                d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (string.IsNullOrWhiteSpace(d)) d = null;
            }
            catch { }
            d = d ?? System.IO.Path.GetFileNameWithoutExtension(path);
            _descCache[path] = d;
            return d;
        }

        void Sync(List<WinInfo> wins)
        {
            foreach (var p in _pinned) p.Windows.Clear();
            var groups = new Dictionary<string, List<WinInfo>>();
            foreach (var w in wins)
            {
                var pin = _pinned.FirstOrDefault(x => x.Resolved != null &&
                    string.Equals(x.Resolved, w.Path, StringComparison.OrdinalIgnoreCase));
                if (pin != null) { pin.Windows.Add(w.Hwnd); continue; }
                string key = w.Class == "ApplicationFrameHost" ? "uwp:" + w.Hwnd : "r:" + w.Path.ToLowerInvariant();
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new List<WinInfo>();
                g.Add(w);
            }

            foreach (var k in _running.Keys.Where(k => !groups.ContainsKey(k)).ToList()) _running.Remove(k);
            foreach (var kv in groups)
            {
                if (!_running.TryGetValue(kv.Key, out var e))
                {
                    var first = kv.Value[0];
                    e = new DockEntry { Kind = DockKind.Running, Key = kv.Key, Path = first.Path, Resolved = first.Path, Name = Describe(first.Path) };
                    e.Icon = first.Class == "ApplicationFrameHost"
                        ? (IconService.FromWindow(first.Hwnd) ?? IconService.GetIcon(first.Path))
                        : IconService.GetIcon(first.Path);
                    if (e.Icon == null) e.Icon = IconService.FromWindow(first.Hwnd);
                    _running[kv.Key] = e;
                }
                e.Windows = kv.Value.Select(w => w.Hwnd).ToList();
            }

            // Running apps come first; OrderBy is stable so ties keep manual order.
            Func<IEnumerable<DockEntry>, IEnumerable<DockEntry>> order = src =>
            {
                var o = src.OrderByDescending(x => x.IsRunning ? 1 : 0);
                return _s.SmartSort ? o.ThenByDescending(Rank) : o;
            };
            var list = new List<DockEntry>();
            if (_running.Count > 0)
            {
                list.AddRange(_s.SmartSort ? _running.Values.OrderByDescending(Rank).ThenBy(r => r.Name) : _running.Values.OrderBy(r => r.Name));
                list.Add(_separator);
            }
            list.AddRange(order(_pinned.Where(p => string.IsNullOrEmpty(p.Category))));
            foreach (var cat in _s.Categories)
            {
                bool col = _s.Collapsed.Contains(cat);
                list.Add(new DockEntry { Kind = DockKind.Header, Key = "h:" + cat + ":" + (col ? 1 : 0), Name = cat, Category = cat, Collapsed = col });
                if (!col) list.AddRange(order(_pinned.Where(p => p.Category == cat)));
            }
            string sig = string.Join("|", list.Select(x => x.Key));
            if (sig != _sig && !_dragging)
            {
                _sig = sig;
                _entries = list;
                Rebuild();
            }
            foreach (var e in _entries)
            {
                if (e.Dot != null) e.Dot.Visibility = e.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                UpdateGlow(e);
            }
        }

        void UpdateGlow(DockEntry e)
        {
            if (e.Glow == null) return;
            double t = e.Hover ? 1.0 : e.IsRunning ? 0.85 : 0.0;
            if (e.GlowT == t) return;
            e.GlowT = t;
            var dur = TimeSpan.FromMilliseconds(220);
            e.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(t, dur));
            // The effect is attached only while glowing: hundreds of idle effects would cost GPU time.
            if (t > 0 && e.Img.Effect == null) e.Img.Effect = e.GlowFx;
            var fx = new DoubleAnimation(t, dur);
            if (t == 0) fx.Completed += (a, b) => { if (e.GlowT == 0) e.Img.Effect = null; };
            e.GlowFx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fx);
        }

        // ---------------------------------------------------------------- visuals

        void EnsureElement(DockEntry e)
        {
            if (e.Root != null) return;
            var root = new Grid { Tag = e, Background = Brushes.Transparent };
            e.Root = root;
            e.Slide = new TranslateTransform();
            root.RenderTransform = e.Slide;

            if (e.Kind == DockKind.Separator)
            {
                e.Line = new Border { Background = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), CornerRadius = new CornerRadius(1),
                                      HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                root.Children.Add(e.Line);
                return;
            }

            if (e.Kind == DockKind.Header)
            {
                string label = (e.Collapsed ? "\u25B8 " : "\u25BE ") + e.Name;
                var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 13, Brushes.White, 1.0);
                e.HeaderLen = Math.Ceiling(ft.Width) + 28;
                root.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10), Height = 26, Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                    Background = new SolidColorBrush(Color.FromArgb(170, 30, 32, 44)),
                    Child = new TextBlock
                    {
                        Text = label, Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI Semibold"),
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, 0)
                    }
                });
                WireMouse(root, e);
                return;
            }

            e.Bounce = new TranslateTransform();
            e.Img = new Image { Source = e.Icon, Stretch = Stretch.Uniform, RenderTransform = e.Bounce, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(e.Img, BitmapScalingMode.HighQuality);
            e.Dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromArgb(255, 30, 120, 255)),
                                  IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            e.Glow = new Ellipse
            {
                Margin = new Thickness(-S * 0.25), IsHitTestVisible = false, Opacity = 0,
                Fill = new RadialGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(Color.FromArgb(235, 90, 170, 255), 0.0),
                        new GradientStop(Color.FromArgb(140, 90, 170, 255), 0.55),
                        new GradientStop(Color.FromArgb(0, 90, 170, 255), 1.0) })
            };
            e.GlowFx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(40, 160, 255), ShadowDepth = 0, BlurRadius = Math.Max(18, S * 0.45), Opacity = 0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            root.Children.Add(e.Glow);
            root.Children.Add(e.Img);
            root.Children.Add(e.Dot);

            e.Tip = new ToolTip
            {
                Content = e.Name,
                Background = new SolidColorBrush(Color.FromArgb(235, 40, 40, 44)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 4, 10, 4),
                Placement = PlacementMode.Top,
                PlacementTarget = root
            };
            root.ToolTip = e.Tip;
            ToolTipService.SetInitialShowDelay(root, 120);

            WireMouse(root, e);
        }

        void WireMouse(Grid root, DockEntry e)
        {
            if (e.Kind != DockKind.Header)
            {
                root.MouseEnter += (a, b) => { e.Hover = true; UpdateGlow(e); };
                root.MouseLeave += (a, b) => { e.Hover = false; UpdateGlow(e); };
            }
            root.MouseLeftButtonDown += (a, b) => { _downEntry = e; _downPt = b.GetPosition(this); };
            root.MouseLeftButtonUp += (a, b) =>
            {
                if (_downEntry == e) { _downEntry = null; Click(e); }
            };
            root.ContextMenu = new ContextMenu();
            root.ContextMenuOpening += (a, b) => root.ContextMenu = BuildMenu(e);
        }

        void Rebuild()
        {
            // Remember where every icon was so it can slide to its new slot.
            var old = new Dictionary<DockEntry, Point>();
            foreach (var ch in _panel.Children)
                if (ch is Grid g && g.Tag is DockEntry de && g.IsLoaded)
                    try { old[de] = g.TranslatePoint(new Point(0, 0), _panel); } catch { }

            _panel.Children.Clear();
            foreach (var e in _entries)
            {
                EnsureElement(e);
                if (e.Size <= 0) e.Size = e.Target = BaseSize(e);
                _panel.Children.Add(e.Root);
            }
            ApplyAlignment();
            UpdateSizes();
            Place();

            if (old.Count == 0) return;
            _panel.UpdateLayout();
            var dur = TimeSpan.FromMilliseconds(380);
            foreach (var e in _entries)
            {
                if (e.Root == null || e.Slide == null) continue;
                if (old.TryGetValue(e, out var op))
                {
                    var np = e.Root.TranslatePoint(new Point(0, 0), _panel);
                    double delta = Horiz ? op.X - np.X : op.Y - np.Y;
                    if (Math.Abs(delta) < 1) continue;
                    var anim = new DoubleAnimation(delta, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                    e.Slide.BeginAnimation(Horiz ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
                }
                else if (e.Kind != DockKind.Separator)
                {
                    e.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { FillBehavior = FillBehavior.Stop });
                    var pop = new DoubleAnimation(S * 0.5, 0, dur) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }, FillBehavior = FillBehavior.Stop };
                    e.Slide.BeginAnimation(Horiz ? TranslateTransform.YProperty : TranslateTransform.XProperty, pop);
                }
            }
        }

        double BaseSize(DockEntry e) => e.Kind == DockKind.Separator ? SepLen : e.Kind == DockKind.Header ? (Horiz ? e.HeaderLen : 30) : S;

        void ResetSizes()
        {
            foreach (var e in _entries.Concat(_pinned).Concat(_running.Values)) e.Size = e.Target = BaseSize(e);
            UpdateSizes();
        }

        Thickness EdgeMargin(double d)
        {
            switch (_s.Edge)
            {
                case DockEdge.Bottom: return new Thickness(0, 0, 0, d);
                case DockEdge.Top: return new Thickness(0, d, 0, 0);
                case DockEdge.Left: return new Thickness(d, 0, 0, 0);
                default: return new Thickness(0, 0, d, 0);
            }
        }

        void ApplyAlignment()
        {
            var edge = _s.Edge;
            var ha = edge == DockEdge.Left ? HorizontalAlignment.Left : edge == DockEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            var va = edge == DockEdge.Top ? VerticalAlignment.Top : edge == DockEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            _panel.Orientation = Horiz ? Orientation.Horizontal : Orientation.Vertical;
            _bar.HorizontalAlignment = _viewport.HorizontalAlignment = ha;
            _bar.VerticalAlignment = _viewport.VerticalAlignment = va;
            _panel.HorizontalAlignment = Horiz ? HorizontalAlignment.Left : ha;
            _panel.VerticalAlignment = Horiz ? va : VerticalAlignment.Top;
            _bar.Margin = EdgeMargin(Gap);
            _grip.HorizontalAlignment = Horiz ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _grip.VerticalAlignment = Horiz ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            _grip.Width = Horiz ? HandleLen : double.NaN;
            _grip.Height = Horiz ? double.NaN : HandleLen;
            _grip.CornerRadius = Horiz ? new CornerRadius(17, 0, 0, 17) : new CornerRadius(17, 17, 0, 0);
            _gripDots.Orientation = Horiz ? Orientation.Vertical : Orientation.Horizontal;
            _viewport.Margin = Horiz
                ? new Thickness(Pad + HandleLen, edge == DockEdge.Top ? Gap : 0, Pad, edge == DockEdge.Bottom ? Gap : 0)
                : new Thickness(edge == DockEdge.Left ? Gap : 0, Pad + HandleLen, edge == DockEdge.Right ? Gap : 0, Pad);
            _panel.Margin = EdgeMargin(Pad);

            var tipMode = edge == DockEdge.Bottom ? PlacementMode.Top : edge == DockEdge.Top ? PlacementMode.Bottom
                        : edge == DockEdge.Left ? PlacementMode.Right : PlacementMode.Left;
            var itemMargin = Horiz ? new Thickness(ItemGap / 2, 0, ItemGap / 2, 0) : new Thickness(0, ItemGap / 2, 0, ItemGap / 2);
            foreach (var e in _entries)
            {
                if (e.Root == null) continue;
                e.Root.HorizontalAlignment = Horiz ? HorizontalAlignment.Left : (edge == DockEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right);
                e.Root.VerticalAlignment = Horiz ? (edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom) : VerticalAlignment.Top;
                e.Root.Margin = itemMargin;
                if (e.Dot != null)
                {
                    e.Dot.HorizontalAlignment = ha;
                    e.Dot.VerticalAlignment = va;
                    e.Dot.Margin = EdgeMargin(-Pad * 0.7);
                }
                if (e.Tip != null) e.Tip.Placement = tipMode;
            }
        }

        double BaseLen() => _entries.Sum(e => BaseSize(e) + ItemGap);

        void UpdateSizes()
        {
            double len = 0;
            foreach (var e in _entries)
            {
                if (e.Root == null) continue;
                len += e.Size + ItemGap;
                if (e.Kind == DockKind.Separator)
                {
                    e.Root.Width = Horiz ? SepLen : S;
                    e.Root.Height = Horiz ? S : SepLen;
                    e.Line.Width = Horiz ? 1.5 : S * 0.7;
                    e.Line.Height = Horiz ? S * 0.7 : 1.5;
                }
                else if (e.Kind == DockKind.Header)
                {
                    e.Root.Width = Horiz ? e.Size : S;
                    e.Root.Height = Horiz ? S : e.Size;
                }
                else
                {
                    e.Root.Width = e.Root.Height = e.Size;
                }
            }
            double content = Math.Max(len - ItemGap, 0);
            double max = Math.Max((Horiz ? Native.GetSystemMetrics(0) : Native.GetSystemMetrics(1)) / Scale - 24, 100);
            _overflow = content + 2 * Pad + HandleLen > max;
            double along = _overflow ? max : Math.Max(content + 2 * Pad + HandleLen, 2 * Pad + HandleLen);
            double cross = S + 2 * Pad;
            _bar.Width = Horiz ? along : cross;
            _bar.Height = Horiz ? cross : along;
            _viewport.Width = _overflow && Horiz ? max - 2 * Pad - HandleLen : double.NaN;
            _viewport.Height = _overflow && !Horiz ? max - 2 * Pad - HandleLen : double.NaN;
            // each item carries ItemGap/2 margin on both ends, so the panel is `len` long (trailing gap included)
            _scrollMin = _overflow ? -Math.Max(len - (max - 2 * Pad - HandleLen), 0) : 0;
            _scrollTarget = ClampOver(_scrollTarget);
            _scroll = ClampOver(_scroll);
            ApplyScroll();
        }

        double ClampScroll(double v) => Math.Max(_scrollMin, Math.Min(0, v));

        // Rubber-band: the wheel may push past either end by up to this much, then it springs back on release.
        double OverMax => S * 1.2;
        long _lastWheel;
        double ClampOver(double v) => Math.Max(_scrollMin - OverMax, Math.Min(OverMax, v));

        void ApplyScroll()
        {
            _scrollTx.X = Horiz ? _scroll : 0;
            _scrollTx.Y = Horiz ? 0 : _scroll;
        }

        // Visible length of the viewport along the dock. Not ActualWidth/Height: WPF arranges an element at least
        // as large as its unclipped desired size, so UnclippedGrid's Actual* is the whole panel, not the viewport.
        double ViewLen => Horiz
            ? (double.IsNaN(_viewport.Width) ? _viewport.ActualWidth : _viewport.Width)
            : (double.IsNaN(_viewport.Height) ? _viewport.ActualHeight : _viewport.Height);

        void UpdateClip()
        {
            if (!_overflow) { _viewport.Clip = null; return; }
            _viewport.Clip = Horiz
                ? new RectangleGeometry(new Rect(0, -500, ViewLen, _viewport.ActualHeight + 1000))
                : new RectangleGeometry(new Rect(-500, 0, _viewport.ActualWidth + 1000, ViewLen));
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_overflow) return;
            double d = e.Delta / 120.0 * (S + ItemGap) * 2;
            double t = _scrollTarget, n = t + d;
            if ((t > 0 && d > 0) || (t < _scrollMin && d < 0))
            {
                double over = t > 0 ? t : _scrollMin - t;
                n = t + d * Math.Max(0, 1 - over / OverMax) * 0.5;
            }
            else if (d > 0 && n > 0) n = n * 0.4;
            else if (d < 0 && n < _scrollMin) n = _scrollMin + (n - _scrollMin) * 0.4;
            _scrollTarget = ClampOver(n);
            _lastWheel = Environment.TickCount64;
            StartAnim();
            e.Handled = true;
        }

        void ResetScroll()
        {
            _edgeVel = 0;
            _scrollTarget = 0;
            StartAnim();
        }

        // Pointer near either end of an overflowing dock keeps scrolling, faster the closer it gets.
        void UpdateEdgeScroll(Point p)
        {
            if (!_overflow) { _edgeVel = 0; return; }
            double m = Horiz ? p.X : p.Y;
            double len = ViewLen;
            double zone = S * 1.6, max = S * 0.28;
            if (m < zone) _edgeVel = max * Math.Min(1, (zone - m) / zone);
            else if (m > len - zone) _edgeVel = -max * Math.Min(1, (m - (len - zone)) / zone);
            else _edgeVel = 0;
            if (_edgeVel != 0) StartAnim();
        }

        void StartAnim()
        {
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += OnRender;
        }

        void OnRender(object sender, EventArgs ev)
        {
            bool changed = false;
            foreach (var e in _entries)
            {
                double d = e.Target - e.Size;
                if (Math.Abs(d) < 0.15) { if (e.Size != e.Target) { e.Size = e.Target; changed = true; } continue; }
                e.Size += d * 0.3;
                changed = true;
            }
            if (_edgeVel != 0)
            {
                double nt = ClampScroll(_scrollTarget + _edgeVel);
                if (nt != _scrollTarget) { _scrollTarget = nt; changed = true; if (!_dragging) MagnifyAt(Mouse.GetPosition(_panel)); }
            }
            if (_scrollTarget > 0 || _scrollTarget < _scrollMin)
            {
                // still overscrolled: wait for the wheel to go quiet, then bounce back
                if (Environment.TickCount64 - _lastWheel > 140) _scrollTarget = ClampScroll(_scrollTarget);
                changed = true;
            }
            double sd = _scrollTarget - _scroll;
            if (Math.Abs(sd) < 0.3) { if (_scroll != _scrollTarget) { _scroll = _scrollTarget; changed = true; } }
            else { _scroll += sd * 0.2; changed = true; }
            if (changed) UpdateSizes();
            else { CompositionTarget.Rendering -= OnRender; _animating = false; }
        }

        void ShrinkAll()
        {
            foreach (var e in _entries) e.Target = BaseSize(e);
            StartAnim();
        }

        void MagnifyAt(Point p)
        {
            if (!_s.Magnify) return;
            double m = Horiz ? p.X : p.Y;
            // Map the pointer (in current, magnified layout) back to un-magnified coordinates.
            double baseM = 0, cur = 0, bpos = 0;
            bool found = false;
            foreach (var e in _entries)
            {
                double bs = BaseSize(e) + ItemGap, cs = e.Size + ItemGap;
                if (!found && m < cur + cs) { baseM = bpos + Math.Max(0, (m - cur)) / cs * bs; found = true; }
                cur += cs; bpos += bs;
            }
            if (!found) baseM = m < 0 ? m : bpos + (m - cur);
            double R = S * 2.6, pos = 0;
            foreach (var e in _entries)
            {
                double bs = BaseSize(e);
                double c = pos + (bs + ItemGap) / 2;
                pos += bs + ItemGap;
                if (e.Kind == DockKind.Separator || e.Kind == DockKind.Header) { e.Target = bs; continue; }
                double d = Math.Abs(baseM - c);
                double t = d >= R ? 0 : 0.5 * (1 + Math.Cos(Math.PI * d / R));
                e.Target = S + S * (_s.Zoom - 1) * t;
            }
            StartAnim();
        }

        // ---------------------------------------------------------------- placement & appbar

        void Place()
        {
            if (_hwnd == IntPtr.Zero || !_inited) return;
            double sc = Scale;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            double zoom = _s.Magnify ? _s.Zoom : 1;
            double len = BaseLen() + 2 * Pad + HandleLen + (zoom - 1) * S * 2.8 + 40;
            double thick = Gap + Pad + S * zoom + Pad + S * 0.5 + 6;
            int lenPx = (int)Math.Min(Math.Ceiling(len * sc), Horiz ? sw : sh);
            int thickPx = (int)Math.Ceiling(thick * sc);
            int x, y, w, h;
            switch (_s.Edge)
            {
                case DockEdge.Bottom: w = lenPx; h = thickPx; x = (sw - w) / 2; y = sh - h; break;
                case DockEdge.Top: w = lenPx; h = thickPx; x = (sw - w) / 2; y = 0; break;
                case DockEdge.Left: w = thickPx; h = lenPx; x = 0; y = (sh - h) / 2; break;
                default: w = thickPx; h = lenPx; x = sw - w; y = (sh - h) / 2; break;
            }
            _wx = x; _wy = y; _ww = w; _wh = h;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (_hidden) HideDock(true);
        }

        // ---------------------------------------------------------------- auto-hide

        void EdgeTick()
        {
            if (_hwnd == IntPtr.Zero || _ww == 0) return;
            if (!_s.AutoHide) { if (_hidden) ShowDock(); return; }
            Native.GetCursorPos(out var cp);
            if (_hidden)
            {
                int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
                bool inX = cp.X >= _wx && cp.X < _wx + _ww, inY = cp.Y >= _wy && cp.Y < _wy + _wh;
                bool hit;
                switch (_s.Edge)
                {
                    case DockEdge.Bottom: hit = inX && cp.Y >= sh - 2; break;
                    case DockEdge.Top: hit = inX && cp.Y <= 1; break;
                    case DockEdge.Left: hit = inY && cp.X <= 1; break;
                    default: hit = inY && cp.X >= sw - 2; break;
                }
                if (hit) ShowDock();
                return;
            }
            bool over = cp.X >= _wx && cp.X < _wx + _ww && cp.Y >= _wy && cp.Y < _wy + _wh;
            if (over || _barDown || _menuOpen || _dragging) { _leftAt = DateTime.MaxValue; return; }
            if (_leftAt == DateTime.MaxValue) _leftAt = DateTime.Now;
            else if ((DateTime.Now - _leftAt).TotalMilliseconds > 700) HideDock(false);
        }

        void HideDock(bool instant)
        {
            bool was = _hidden;
            _hidden = true;
            _leftAt = DateTime.MaxValue;
            if (!was)
            {
                foreach (var e in _entries) { e.Hover = false; UpdateGlow(e); if (e.Tip != null) e.Tip.IsOpen = false; }
                ShrinkAll();
                ResetScroll();
            }
            double sc = Scale;
            double off;
            switch (_s.Edge)
            {
                case DockEdge.Bottom: off = _wh / sc; break;
                case DockEdge.Top: off = -_wh / sc; break;
                case DockEdge.Left: off = -_ww / sc; break;
                default: off = _ww / sc; break;
            }
            var main = Horiz ? TranslateTransform.YProperty : TranslateTransform.XProperty;
            var other = Horiz ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            _hideTx.BeginAnimation(other, null);
            _hideTx.SetValue(other, 0.0);
            if (instant) { _hideTx.BeginAnimation(main, null); _hideTx.SetValue(main, off); }
            else _hideTx.BeginAnimation(main, new DoubleAnimation(off, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        void ShowDock()
        {
            _hidden = false;
            _leftAt = DateTime.MaxValue;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _hideTx.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _hideTx.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        void ApplyAppBar()
        {
            if (_hwnd == IntPtr.Zero || !_inited) return;
            if (_s.AutoHide)
            {
                // A hidden dock must not reserve screen space.
                if (_appBarRegistered)
                {
                    var rm = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
                    Native.SHAppBarMessage(Native.ABM_REMOVE, ref rm);
                    _appBarRegistered = false;
                }
                return;
            }
            var abd = new Native.APPBARDATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
                hWnd = _hwnd,
                uCallbackMessage = 0x8001
            };
            if (!_appBarRegistered) { Native.SHAppBarMessage(Native.ABM_NEW, ref abd); _appBarRegistered = true; }

            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            int t = (int)Math.Ceiling((Gap + Pad * 2 + S + 4) * Scale);
            switch (_s.Edge)
            {
                case DockEdge.Bottom: abd.uEdge = Native.ABE_BOTTOM; abd.rc = new Native.RECT { Left = 0, Top = sh - t, Right = sw, Bottom = sh }; break;
                case DockEdge.Top: abd.uEdge = Native.ABE_TOP; abd.rc = new Native.RECT { Left = 0, Top = 0, Right = sw, Bottom = t }; break;
                case DockEdge.Left: abd.uEdge = Native.ABE_LEFT; abd.rc = new Native.RECT { Left = 0, Top = 0, Right = t, Bottom = sh }; break;
                default: abd.uEdge = Native.ABE_RIGHT; abd.rc = new Native.RECT { Left = sw - t, Top = 0, Right = sw, Bottom = sh }; break;
            }
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
            switch (_s.Edge)
            {
                case DockEdge.Bottom: abd.rc.Top = abd.rc.Bottom - t; break;
                case DockEdge.Top: abd.rc.Bottom = abd.rc.Top + t; break;
                case DockEdge.Left: abd.rc.Right = abd.rc.Left + t; break;
                default: abd.rc.Left = abd.rc.Right - t; break;
            }
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
        }

        public void Cleanup()
        {
            _timer.Stop();
            if (_appBarRegistered && _hwnd != IntPtr.Zero)
            {
                var abd = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
                Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
                _appBarRegistered = false;
            }
        }

        // ---------------------------------------------------------------- mouse: magnify, click, move dock, drag items

        bool IsOnItem(DependencyObject o)
        {
            while (o != null)
            {
                if (o is Grid g && g.Tag is DockEntry de && de.Kind != DockKind.Separator) return true;
                o = (o is Visual || o is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
            }
            return false;
        }

        void OnWinDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOnItem(e.OriginalSource as DependencyObject)) return;
            _barDown = true;
            _barMoving = false;
            Native.GetCursorPos(out _barStart);
            CaptureMouse();
        }

        void OnWinUp(object sender, MouseButtonEventArgs e)
        {
            _downEntry = null;
            if (!_barDown) return;
            _barDown = false;
            ReleaseMouseCapture();
            if (_barMoving)
            {
                _barMoving = false;
                ApplyAppBar();
                _s.Save();
            }
        }

        void OnMouseMoveWin(object sender, MouseEventArgs e)
        {
            Native.GetCursorPos(out var cp);

            // A lost mouse-up (context menu, drag) must not freeze hover/magnify forever.
            if (_barDown && e.LeftButton != MouseButtonState.Pressed)
            {
                _barDown = false; _barMoving = false;
                ReleaseMouseCapture();
            }
            if (_downEntry != null && e.LeftButton != MouseButtonState.Pressed) _downEntry = null;

            if (_barDown)
            {
                if (!_barMoving && Math.Abs(cp.X - _barStart.X) + Math.Abs(cp.Y - _barStart.Y) > 8)
                {
                    _barMoving = true;
                    ShrinkAll();
                }
                if (_barMoving)
                {
                    var best = NearestEdge(cp, out _);
                    if (best != _s.Edge)
                    {
                        _s.Edge = best;
                        _scroll = _scrollTarget = 0;
                        ApplyAlignment();
                        ResetSizes();
                        Place();
                    }
                }
                return;
            }

            if (_downEntry != null && e.LeftButton == MouseButtonState.Pressed && _downEntry.Kind == DockKind.Pinned)
            {
                var pt = e.GetPosition(this);
                if (Math.Abs(pt.X - _downPt.X) + Math.Abs(pt.Y - _downPt.Y) > 8)
                {
                    var ent = _downEntry; _downEntry = null;
                    DoItemDrag(ent);
                    return;
                }
            }
            if (_hidden) return;
            MagnifyAt(e.GetPosition(_panel));
            UpdateEdgeScroll(e.GetPosition(_viewport));
        }

        static DockEdge NearestEdge(Native.POINT cp, out int dist)
        {
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            var best = DockEdge.Bottom; int bd = sh - cp.Y;
            if (cp.Y < bd) { best = DockEdge.Top; bd = cp.Y; }
            if (cp.X < bd) { best = DockEdge.Left; bd = cp.X; }
            if (sw - cp.X < bd) { best = DockEdge.Right; bd = sw - cp.X; }
            dist = bd;
            return best;
        }

        void DoItemDrag(DockEntry ent)
        {
            ShrinkAll();
            ent.Root.Opacity = 0.4;
            _edgeVel = 0;
            var before = _pinned.Select(p => p.Path).ToList();
            _dragging = true;
            try { DragDrop.DoDragDrop(ent.Root, new DataObject("MacDockEntry", ent), DragDropEffects.Move); }
            finally { _dragging = false; _edgeVel = 0; }
            ent.Root.Opacity = 1;

            // Dragging off the dock never deletes (that lost icons when users meant to move the dock);
            // releasing near another screen edge moves the dock there instead. Removal is via the menu.
            Native.GetCursorPos(out var cp);
            double sc = Scale;
            var tl = _bar.PointToScreen(new Point(0, 0));
            double margin = 60 * sc;
            bool inside = cp.X >= tl.X - margin && cp.X <= tl.X + _bar.ActualWidth * sc + margin &&
                          cp.Y >= tl.Y - margin && cp.Y <= tl.Y + _bar.ActualHeight * sc + margin;
            if (!inside)
            {
                var edge = NearestEdge(cp, out int dist);
                if (edge != _s.Edge && dist < 150 * sc) { _scroll = _scrollTarget = 0; SetEdge(edge); }
            }

            bool reordered = !before.SequenceEqual(_pinned.Select(p => p.Path));
            // Smart order would immediately undo a manual reorder, so a deliberate drag switches it off.
            if (reordered && _s.SmartSort) _s.SmartSort = false;
            SavePinned();
            _sig = ""; Tick();
            if (!inside) { ShrinkAll(); ResetScroll(); }
        }

        int InsertIndex(Point p)
        {
            double m = Horiz ? p.X : p.Y;
            int idx = 0;
            foreach (var e in _entries)
            {
                if (e.Root == null) { idx++; continue; }
                var c = e.Root.TranslatePoint(new Point(e.Root.ActualWidth / 2, e.Root.ActualHeight / 2), _panel);
                if ((Horiz ? c.X : c.Y) < m) idx++;
            }
            return idx;
        }

        string CatBefore(int idx)
        {
            if (idx <= 0 || idx > _entries.Count) return "";
            var prev = _entries[idx - 1];
            return prev.Kind == DockKind.Header || prev.Kind == DockKind.Pinned ? prev.Category : "";
        }

        int PinnedIndexAt(int idx, DockEntry skip)
        {
            for (int i = idx; i < _entries.Count; i++)
                if (_entries[i].Kind == DockKind.Pinned && _entries[i] != skip) return _pinned.IndexOf(_entries[i]);
            return -1;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent("MacDockEntry"))
            {
                var ent = (DockEntry)e.Data.GetData("MacDockEntry");
                int cur = _entries.IndexOf(ent);
                int idx = InsertIndex(e.GetPosition(_panel));
                if (idx > cur) idx--;
                int start = _entries.FindIndex(x => x.Kind == DockKind.Separator) + 1;
                idx = Math.Max(start, Math.Min(idx, _entries.Count - 1));
                if (cur >= 0 && idx != cur)
                {
                    _entries.RemoveAt(cur);
                    _entries.Insert(idx, ent);
                    _panel.Children.Remove(ent.Root);
                    _panel.Children.Insert(idx, ent.Root);
                    ent.Category = CatBefore(idx);
                    _pinned.Remove(ent);
                    int pi = PinnedIndexAt(idx + 1, ent);
                    if (pi < 0) _pinned.Add(ent); else _pinned.Insert(pi, ent);
                    _sig = string.Join("|", _entries.Select(x => x.Key));
                }
                e.Effects = DragDropEffects.Move;
                UpdateEdgeScroll(e.GetPosition(_viewport));
            }
            else e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            _edgeVel = 0;
            base.OnDragLeave(e);
        }

        protected override void OnDrop(DragEventArgs e)
        {
            _edgeVel = 0;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                int idx = Math.Max(InsertIndex(e.GetPosition(_panel)), _entries.FindIndex(x => x.Kind == DockKind.Separator) + 1);
                string cat = CatBefore(idx);
                int pi = PinnedIndexAt(idx, null);
                foreach (var f in files) { AddPinned(f, pi, false, cat); if (pi >= 0) pi++; }
                SavePinned();
                _sig = ""; Tick();
            }
            e.Handled = true;
        }

        // ---------------------------------------------------------------- actions

        void Click(DockEntry e)
        {
            if (e.Kind == DockKind.Header) { ToggleCollapse(e.Category); return; }
            AddUsage(UsageKey(e), 30);
            if (e.IsRunning) { Activate(e.Windows[0]); return; }
            if (e.Kind != DockKind.Pinned) return;
            try
            {
                Process.Start(new ProcessStartInfo(e.Path) { UseShellExecute = true });
                Bounce(e);
            }
            catch { }
        }

        void Refresh() { _sig = ""; Tick(); }

        void ToggleCollapse(string cat)
        {
            if (!_s.Collapsed.Remove(cat)) _s.Collapsed.Add(cat);
            _s.Save();
            Refresh();
        }

        void SetCategory(DockEntry e, string cat)
        {
            e.Category = EnsureCat(cat);
            SavePinned();
            Refresh();
        }

        void NewCategory(DockEntry forEntry)
        {
            var n = Prompt("New category", "");
            if (n == null) return;
            var c = EnsureCat(n);
            if (forEntry != null) forEntry.Category = c;
            SavePinned();
            Refresh();
        }

        void RenameCategory(string old)
        {
            var n = Prompt("Rename category", old);
            if (n == null || n == old) return;
            int i = _s.Categories.IndexOf(old);
            if (i < 0 || _s.Categories.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) return;
            _s.Categories[i] = n;
            foreach (var p in _pinned.Where(p => p.Category == old)) p.Category = n;
            if (_s.Collapsed.Remove(old)) _s.Collapsed.Add(n);
            SavePinned();
            Refresh();
        }

        void AutoOrganize()
        {
            if (_pinned.Count == 0) return;
            if (_pinned.Any(p => !string.IsNullOrEmpty(p.Category)) &&
                MessageBox.Show("จัดหมวดไอคอนใหม่ทั้งหมดอัตโนมัติ?\nหมวดที่ตั้งเองไว้จะถูกแทนที่", "MacDock",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var guess = _pinned.ToDictionary(p => p, CategoryClassifier.Classify);
            // A category holding a single icon only adds clutter; fold those into "Other".
            var counts = guess.Values.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
            foreach (var p in _pinned)
            {
                var c = guess[p];
                p.Category = counts[c] >= 2 ? c : CategoryClassifier.Other;
            }
            if (_pinned.All(p => p.Category == CategoryClassifier.Other))
                foreach (var p in _pinned) p.Category = "";

            var used = new HashSet<string>(_pinned.Select(p => p.Category).Where(c => c != ""));
            _s.Categories = CategoryClassifier.Order.Where(used.Contains).ToList();
            _s.Collapsed.RemoveAll(c => !used.Contains(c));
            SavePinned();
            Refresh();
        }

        void DeleteCategory(string cat)
        {
            foreach (var p in _pinned.Where(p => p.Category == cat)) p.Category = "";
            _s.Categories.Remove(cat);
            _s.Collapsed.Remove(cat);
            SavePinned();
            Refresh();
        }

        static string Prompt(string title, string initial)
        {
            string result = null;
            var tb = new TextBox { Text = initial, MinWidth = 260, Margin = new Thickness(0, 0, 0, 12) };
            var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
            var w = new Window
            {
                Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false
            };
            ok.Click += (a, b) => { result = tb.Text.Trim(); w.DialogResult = true; };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(tb);
            sp.Children.Add(btns);
            w.Content = sp;
            w.Loaded += (a, b) => { tb.Focus(); tb.SelectAll(); };
            w.ShowDialog();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        static void Activate(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Native.SetForegroundWindow(h);
        }

        void Bounce(DockEntry e)
        {
            if (e.Bounce == null) return;
            double sign = (_s.Edge == DockEdge.Bottom || _s.Edge == DockEdge.Right) ? -1 : 1;
            var anim = new DoubleAnimation(0, sign * S * 0.45, TimeSpan.FromMilliseconds(260))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            e.Bounce.BeginAnimation(Horiz ? TranslateTransform.YProperty : TranslateTransform.XProperty, anim);
        }

        void Poof(int px, int py)
        {
            double sc = Scale;
            var el = new Ellipse
            {
                Width = 80, Height = 80, RenderTransformOrigin = new Point(0.5, 0.5),
                Fill = new RadialGradientBrush(Color.FromArgb(220, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)),
                RenderTransform = new ScaleTransform(0.3, 0.3)
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, Topmost = true, ShowActivated = false, IsHitTestVisible = false,
                Width = 80, Height = 80, Left = px / sc - 40, Top = py / sc - 40, Content = el
            };
            w.Show();
            var dur = TimeSpan.FromMilliseconds(450);
            var grow = new DoubleAnimation(0.3, 1.5, dur);
            var fade = new DoubleAnimation(1, 0, dur);
            fade.Completed += (a, b) => w.Close();
            el.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            el.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            el.BeginAnimation(OpacityProperty, fade);
        }

        // ---------------------------------------------------------------- menu

        internal static MenuItem Item(string header, Action act, bool? check = null)
        {
            var mi = new MenuItem { Header = header };
            if (check.HasValue) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (a, b) => act();
            return mi;
        }

        ContextMenu BuildMenu(DockEntry e)
        {
            var m = new ContextMenu();
            if (e != null && e.Kind == DockKind.Header)
            {
                string cat = e.Category;
                m.Items.Add(Item(e.Collapsed ? "Expand" : "Collapse", () => ToggleCollapse(cat)));
                m.Items.Add(Item("Rename category...", () => RenameCategory(cat)));
                m.Items.Add(Item("Delete category", () => DeleteCategory(cat)));
                m.Items.Add(new Separator());
            }
            else if (e != null)
            {
                if (e.Kind == DockKind.Pinned)
                {
                    var cm = new MenuItem { Header = "Category" };
                    cm.Items.Add(Item("None", () => SetCategory(e, ""), string.IsNullOrEmpty(e.Category)));
                    foreach (var c in _s.Categories.ToList())
                    {
                        var cc = c;
                        cm.Items.Add(Item(cc, () => SetCategory(e, cc), e.Category == cc));
                    }
                    cm.Items.Add(new Separator());
                    cm.Items.Add(Item("New category...", () => NewCategory(e)));
                    m.Items.Add(cm);
                    m.Items.Add(Item(e.IsRunning ? "Show" : "Open", () => Click(e)));
                    m.Items.Add(Item("Show in Explorer", () =>
                    {
                        try { Process.Start("explorer.exe", "/select,\"" + e.Path + "\""); } catch { }
                    }));
                }
                else
                {
                    m.Items.Add(Item("Show", () => { if (e.IsRunning) Activate(e.Windows[0]); }));
                    if (!string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
                        m.Items.Add(Item("Keep in Dock", () => AddPinned(e.Path, -1, true)));
                }
                if (e.IsRunning)
                    m.Items.Add(Item("Quit", () => { foreach (var h in e.Windows.ToList()) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
                if (e.Kind == DockKind.Pinned)
                    m.Items.Add(Item("Remove from Dock", () => { _pinned.Remove(e); SavePinned(); _sig = ""; Tick(); }));
                m.Items.Add(new Separator());
            }

            var pos = new MenuItem { Header = "Position on screen" };
            foreach (DockEdge ed in Enum.GetValues(typeof(DockEdge)))
            {
                var edge = ed;
                var mi = Item(edge == DockEdge.Bottom ? "Bottom" : edge.ToString(), () => SetEdge(edge), _s.Edge == edge);
                mi.StaysOpenOnClick = false;
                pos.Items.Add(mi);
            }
            m.Items.Add(pos);
            m.Items.Add(Item("New category...", () => NewCategory(null)));
            m.Items.Add(Item("จัดหมวดไอคอนอัตโนมัติ (Auto-organize)", AutoOrganize));

            var size = new MenuItem { Header = "Icon size" };
            foreach (var (label, v) in new[] { ("Small", 40.0), ("Medium", 56.0), ("Large", 72.0), ("Extra large", 96.0) })
            {
                double val = v;
                size.Items.Add(Item(label, () => SetSize(val), Math.Abs(_s.IconSize - val) < 0.5));
            }
            m.Items.Add(size);

            m.Items.Add(Item("Auto-hide (show at screen edge)", () => { _s.AutoHide = !_s.AutoHide; _s.Save(); ApplyAppBar(); if (!_s.AutoHide) ShowDock(); }, _s.AutoHide));
            m.Items.Add(Item("Magnification", () => { _s.Magnify = !_s.Magnify; ResetSizes(); Place(); _s.Save(); }, _s.Magnify));
            m.Items.Add(Item("Smart order (most used first)", () =>
            {
                _s.SmartSort = !_s.SmartSort;
                _rank = new Dictionary<string, double>(_s.Usage, StringComparer.OrdinalIgnoreCase);
                _s.Save(); Refresh();
            }, _s.SmartSort));
            var clock = new MenuItem { Header = "Clock widget" };
            foreach (var (label, st) in new[] { ("Off", ClockStyle.Off), ("Analog (เข็ม)", ClockStyle.Analog), ("Digital (ดิจิตอล)", ClockStyle.Digital) })
            {
                var cs = st;
                clock.Items.Add(Item(label, () => SetClock(cs), _s.Clock == cs));
            }
            m.Items.Add(clock);
            m.Items.Add(Item("Show running apps", () => { _s.ShowRunning = !_s.ShowRunning; _s.Save(); _sig = ""; Tick(); }, _s.ShowRunning));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Hide desktop icons", () => SetHideIcons(!_s.HideDesktopIcons), _s.HideDesktopIcons));
            m.Items.Add(Item("Hide Windows taskbar", () => SetHideTaskbar(!_s.HideTaskbar), _s.HideTaskbar));
            m.Items.Add(Item("Re-import Desktop items", () => { ImportDesktop(); _sig = ""; Tick(); }));
            m.Items.Add(Item("Start with Windows", () => SetAutoStart(!GetAutoStart()), GetAutoStart()));
            m.Items.Add(new Separator());
            m.Items.Add(Item("ไลเซ่น & อัปเดต...", () => LicenseWindow.ShowSingle(_s)));
            m.Items.Add(Item("เกี่ยวกับ MacDock (About)...", AboutWindow.ShowSingle));
            m.Items.Add(Item("Quit MacDock", () => Application.Current.Shutdown()));
            return m;
        }

        public void SetEdge(DockEdge edge)
        {
            ShowDock();
            _s.Edge = edge;
            _scroll = _scrollTarget = 0;
            ApplyAlignment();
            ResetSizes();
            Place();
            ApplyAppBar();
            _s.Save();
        }

        void SetSize(double v)
        {
            _s.IconSize = v;
            ResetSizes();
            ApplyAlignment();
            Place();
            ApplyAppBar();
            _s.Save();
        }

        public void SetHideIcons(bool hide)
        {
            _s.HideDesktopIcons = hide;
            SystemTweaks.SetDesktopIconsHidden(hide);
            _s.Save();
        }

        public void SetHideTaskbar(bool hide)
        {
            _s.HideTaskbar = hide;
            SystemTweaks.SetTaskbarHidden(hide);
            _s.Save();
            ApplyAppBar();
        }

        public ClockStyle Clock => _s.Clock;

        public void SetClock(ClockStyle st)
        {
            _s.Clock = st;
            _s.Save();
            if (st == ClockStyle.Off) { _clock?.Close(); _clock = null; return; }
            if (_clock == null)
            {
                _clock = new ClockWidget(_s);
                _clock.HideRequested += () => SetClock(ClockStyle.Off);
                _clock.Show();
            }
            else _clock.ApplyStyle();
        }

        public bool HideIcons => _s.HideDesktopIcons;
        public bool HideTaskbar => _s.HideTaskbar;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool GetAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("MacDock") != null;
        }

        static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue("MacDock", "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue("MacDock", false);
            }
        }
    }
}
