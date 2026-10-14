using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacDock
{
    // XMAN Studio branding shared by the clock widget and the About window.
    public static class Brand
    {
        public const string Company = "XMAN Studio";
        public const string Url = "https://xman4289.com";

        public static string Version
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? asm.GetName().Version?.ToString() ?? "1.0.0";
                int plus = v.IndexOf('+'); // drop the source-revision suffix the SDK may append
                return plus > 0 ? v.Substring(0, plus) : v;
            }
        }

        public const string License =
            "MacDock — ลิขสิทธิ์ © 2026 XMAN Studio สงวนลิขสิทธิ์ทั้งหมด\n\n" +
            "1. XMAN Studio อนุญาตให้คุณติดตั้งและใช้งานโปรแกรมนี้บนเครื่องคอมพิวเตอร์ของคุณ เพื่อการใช้งานส่วนตัวหรือภายในองค์กร\n" +
            "2. ห้ามจำหน่าย ให้เช่า แจกจ่ายต่อ ดัดแปลง ถอดรหัส (reverse engineer) หรือนำโปรแกรมทั้งหมดหรือบางส่วนไปเผยแพร่ โดยไม่ได้รับอนุญาตเป็นลายลักษณ์อักษรจาก XMAN Studio\n" +
            "3. ชื่อ โลโก้ และเครื่องหมาย XMAN Studio เป็นทรัพย์สินของ XMAN Studio\n" +
            "4. โปรแกรมนี้ให้บริการ \"ตามสภาพ\" (AS IS) โดยไม่มีการรับประกันใด ๆ XMAN Studio ไม่รับผิดชอบต่อความเสียหายที่เกิดจากการใช้งาน\n" +
            "5. การติดตั้งหรือใช้งานโปรแกรมถือว่าคุณยอมรับเงื่อนไขนี้\n\n" +
            "ติดต่อ: " + Url;

        static BitmapImage _logo;

        public static ImageSource LogoSource
        {
            get
            {
                if (_logo == null)
                {
                    _logo = new BitmapImage(new Uri("pack://application:,,,/Assets/xman-logo.png"));
                    _logo.Freeze();
                }
                return _logo;
            }
        }

        public static void OpenUrl(string url = Url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // The XMAN Studio logo with a slowly breathing neon glow; a click opens the website.
        public static FrameworkElement GlowingLogo(double height)
        {
            var glow = new DropShadowEffect { Color = Color.FromRgb(0x5E, 0xF2, 0xFF), ShadowDepth = 0, BlurRadius = 10, Opacity = 0.55 };
            var img = new Image
            {
                Source = LogoSource, Height = height, Stretch = Stretch.Uniform,
                Cursor = Cursors.Hand, ToolTip = "xman4289.com", Effect = glow,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var pulse = new DoubleAnimation(0.35, 0.85, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(pulse, 20); // keeps the layered window cheap to redraw
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);

            img.MouseEnter += (a, b) => { glow.BlurRadius = 18; glow.Color = Color.FromRgb(0xC0, 0x7C, 0xFF); };
            img.MouseLeave += (a, b) => { glow.BlurRadius = 10; glow.Color = Color.FromRgb(0x5E, 0xF2, 0xFF); };
            // Handled so the host window does not start a drag instead.
            img.MouseLeftButtonDown += (a, b) => { b.Handled = true; OpenUrl(); };
            return img;
        }
    }

    // About MacDock: version, publisher and license.
    public class AboutWindow : Window
    {
        static AboutWindow _open;

        public static void ShowSingle()
        {
            if (_open == null)
            {
                _open = new AboutWindow();
                _open.Closed += (a, b) => _open = null;
                _open.Show();
            }
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }

        AboutWindow()
        {
            Title = "เกี่ยวกับ MacDock";
            Width = 460;
            Height = 560;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = new LinearGradientBrush(Color.FromRgb(0x0D, 0x11, 0x1E), Color.FromRgb(0x1C, 0x15, 0x34), 45);

            var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF5));
            var dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xBC, 0xD4));

            var root = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };

            var head = new StackPanel();
            var logo = Brand.GlowingLogo(64);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            head.Children.Add(logo);
            head.Children.Add(new TextBlock
            {
                Text = "MacDock", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0)
            });
            head.Children.Add(new TextBlock
            {
                Text = "รุ่น (Version) " + Brand.Version, FontSize = 14, Foreground = dim,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
            });
            head.Children.Add(new TextBlock
            {
                Text = "Dock สไตล์ macOS สำหรับ Windows  •  พัฒนาโดย " + Brand.Company, FontSize = 12.5, Foreground = dim,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            });
            var link = new TextBlock
            {
                Text = "xman4289.com", FontSize = 13, Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0xF2, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14)
            };
            link.MouseLeftButtonDown += (a, b) => Brand.OpenUrl();
            head.Children.Add(link);
            head.Children.Add(new TextBlock
            {
                Text = "สัญญาอนุญาตให้ใช้งาน (License)", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = fg,
                Margin = new Thickness(0, 0, 0, 6)
            });
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var ok = new Button
            {
                Content = "ตกลง", Width = 96, Height = 30, IsDefault = true, IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0)
            };
            ok.Click += (a, b) => Close();
            DockPanel.SetDock(ok, Dock.Bottom);
            root.Children.Add(ok);

            root.Children.Add(new TextBox
            {
                Text = Brand.License, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12.5, Padding = new Thickness(8),
                Foreground = fg, Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x5E, 0xF2, 0xFF))
            });

            Content = root;
        }
    }
}
