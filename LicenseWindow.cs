using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacDock
{
    // License & updates: get the free XMAN Studio license (sign up / log in on xman4289.com, press confirm)
    // and manage automatic updates.
    public class LicenseWindow : Window
    {
        static LicenseWindow _open;

        public static void ShowSingle(Settings s)
        {
            if (_open == null)
            {
                _open = new LicenseWindow(s);
                _open.Closed += (a, b) => _open = null;
                _open.Show();
            }
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }

        readonly Settings _s;
        readonly TextBlock _state, _detail, _hint;
        readonly Button _primary, _secondary, _checkNow;
        readonly CheckBox _auto;
        CancellationTokenSource _claim;

        static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF5));
        static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xBC, 0xD4));
        static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x6E, 0xF0, 0xA0));
        static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x6E));
        static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x5E, 0xF2, 0xFF));

        LicenseWindow(Settings s)
        {
            _s = s;
            Title = "ไลเซ่น & อัปเดต — MacDock";
            Width = 470;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = new LinearGradientBrush(Color.FromRgb(0x0D, 0x11, 0x1E), Color.FromRgb(0x1C, 0x15, 0x34), 45);

            var root = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
            var logo = Brand.GlowingLogo(48);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(logo);
            root.Children.Add(new TextBlock
            {
                Text = "ไลเซ่นฟรี & อัปเดตอัตโนมัติ", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Fg,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4)
            });
            root.Children.Add(new TextBlock
            {
                Text = "MacDock " + Brand.Version + "  •  " + Brand.Company, FontSize = 12.5, Foreground = Dim,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16)
            });

            _state = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _detail = new TextBlock { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x5E, 0xF2, 0xFF)), BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Child = new StackPanel { Children = { _state, _detail } }
            };
            root.Children.Add(card);

            _primary = Btn("", true);
            _primary.Margin = new Thickness(0, 16, 0, 0);
            _primary.Click += async (a, b) => await PrimaryAsync();
            root.Children.Add(_primary);

            _secondary = Btn("", false);
            _secondary.Margin = new Thickness(0, 8, 0, 0);
            _secondary.Click += (a, b) => Brand.OpenUrl(LicenseService.IsLicensed ? LicenseService.MyLicensesUrl : LicenseService.ClaimUrl);
            root.Children.Add(_secondary);

            _auto = new CheckBox
            {
                Content = new TextBlock { Text = "อัปเดตอัตโนมัติ (ตรวจทุก 6 ชั่วโมง ถามก่อนติดตั้ง)", Foreground = Fg, FontSize = 13 },
                IsChecked = _s.AutoUpdate, Margin = new Thickness(0, 18, 0, 0)
            };
            _auto.Click += (a, b) => { _s.AutoUpdate = _auto.IsChecked == true; _s.Save(); };
            root.Children.Add(_auto);

            _checkNow = Btn("ตรวจหาอัปเดตตอนนี้", false);
            _checkNow.Margin = new Thickness(0, 10, 0, 0);
            _checkNow.Click += async (a, b) =>
            {
                _checkNow.IsEnabled = false;
                try { await UpdateService.RunAsync(interactive: true); }
                finally { _checkNow.IsEnabled = true; Refresh(); }
            };
            root.Children.Add(_checkNow);

            _hint = new TextBlock
            {
                FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0),
                Text = "ไลเซ่นฟรีผูกกับบัญชี xman studio ของคุณและเครื่องนี้ ใช้เปิดการอัปเดตอัตโนมัติ " +
                       "ไฟล์อัปเดตโหลดจาก xman4289.com เท่านั้น และถูกตรวจ sha256 ก่อนติดตั้งทุกครั้ง"
            };
            root.Children.Add(_hint);

            Content = root;
            LicenseService.Changed += OnChanged;
            Closed += (a, b) => { LicenseService.Changed -= OnChanged; _claim?.Cancel(); };
            Refresh();
            Loaded += async (a, b) => { await LicenseService.CheckAsync(); Refresh(); };
        }

        void OnChanged() => Dispatcher.BeginInvoke(new Action(Refresh));

        static Button Btn(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, Height = primary ? 40 : 32, FontSize = primary ? 14.5 : 13, Cursor = Cursors.Hand,
                Foreground = primary ? Brushes.Black : Fg,
                Background = primary ? Cyan : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x5E, 0xF2, 0xFF))
            };
            if (primary) b.FontWeight = FontWeights.SemiBold;
            return b;
        }

        void Refresh()
        {
            bool waiting = _claim != null;
            if (LicenseService.IsLicensed)
            {
                _state.Text = "✓ ได้รับไลเซ่นฟรีแล้ว — อัปเดตอัตโนมัติเปิดใช้งานได้";
                _state.Foreground = Ok;
                _detail.Text = "คีย์: " + LicenseService.MaskedKey + "\nผูกกับเครื่อง: " + MachineIdentity.MachineName;
                _primary.Visibility = Visibility.Collapsed;
                _secondary.Content = "ดูไลเซ่นของฉันบน xman4289.com";
            }
            else if (waiting)
            {
                _state.Text = "รอการยืนยันบนหน้าเว็บ…";
                _state.Foreground = Warn;
                _detail.Text = "เข้าสู่ระบบหรือสมัครสมาชิก xman studio ในเบราว์เซอร์ แล้วกด \"ยืนยันรับไลเซ่นฟรี\" " +
                               "ไลเซ่นจะเข้ามาที่โปรแกรมเองภายในไม่กี่วินาที";
                _primary.Visibility = Visibility.Visible;
                _primary.Content = "ยกเลิกการรอ";
                _secondary.Content = "เปิดหน้ายืนยันอีกครั้ง";
            }
            else
            {
                _state.Text = "ยังไม่ได้รับไลเซ่น";
                _state.Foreground = Warn;
                _detail.Text = "MacDock ใช้ได้ฟรีเหมือนเดิม แต่การอัปเดตอัตโนมัติต้องใช้ไลเซ่นฟรีของ XMAN Studio " +
                               "— สมัครสมาชิกแล้วกดยืนยัน รับได้ทันที ไม่มีค่าใช้จ่าย";
                _primary.Visibility = Visibility.Visible;
                _primary.Content = "สมัครสมาชิก / เข้าสู่ระบบ เพื่อรับไลเซ่นฟรี";
                _secondary.Content = "เปิดหน้าเว็บ xman4289.com";
            }
            _secondary.Visibility = LicenseService.IsLicensed || waiting ? Visibility.Visible : Visibility.Collapsed;
            _auto.IsEnabled = _checkNow.IsEnabled = LicenseService.IsLicensed;
        }

        async Task PrimaryAsync()
        {
            if (_claim != null) { _claim.Cancel(); return; }

            _primary.IsEnabled = false;
            bool registered = await LicenseService.RegisterDeviceAsync();
            _primary.IsEnabled = true;
            if (!registered)
            {
                MessageBox.Show(this, "ลงทะเบียนเครื่องกับ xman4289.com ไม่สำเร็จ\nตรวจการเชื่อมต่ออินเทอร์เน็ตแล้วลองใหม่",
                    "ไลเซ่น MacDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Brand.OpenUrl(LicenseService.ClaimUrl);
            var cts = _claim = new CancellationTokenSource();
            Refresh();
            bool got = false;
            try { got = await LicenseService.WaitForClaimAsync(TimeSpan.FromMinutes(20), cts.Token); }
            catch (OperationCanceledException) { }
            finally { if (_claim == cts) _claim = null; cts.Dispose(); }
            if (!IsLoaded) return;
            Refresh();
            if (got)
                MessageBox.Show(this, "รับไลเซ่นฟรีเรียบร้อย ขอบคุณที่ใช้ MacDock\nโปรแกรมจะอัปเดตตัวเองอัตโนมัติเมื่อมีเวอร์ชันใหม่",
                    "ไลเซ่น MacDock", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
