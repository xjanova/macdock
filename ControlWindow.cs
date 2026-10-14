using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacDock
{
    // Minimal control panel: one switch to turn the dock on / off.
    public class ControlWindow : Window
    {
        public event Action<bool> Toggled;

        readonly Button _btn;
        readonly TextBlock _status;
        bool _on;

        public ControlWindow()
        {
            Title = "MacDock";
            Width = 280;
            Height = 190;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var panel = new StackPanel { Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = "MacDock",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _status = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.Gray
            };
            panel.Children.Add(_status);
            _btn = new Button { Height = 40, FontSize = 16 };
            _btn.Click += (s, e) => { _on = !_on; Refresh(); Toggled?.Invoke(_on); };
            panel.Children.Add(_btn);
            Content = panel;
            Refresh();
            Closing += (s, e) =>
            {
                if (_on) { e.Cancel = true; Hide(); }
            };
        }

        public void SetOn(bool on) { _on = on; Refresh(); }

        public void ShowPanel()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void Refresh()
        {
            _btn.Content = _on ? "ปิด Dock" : "เปิด Dock";
            _status.Text = _on ? "สถานะ: เปิดอยู่" : "สถานะ: ปิดอยู่";
        }
    }
}
