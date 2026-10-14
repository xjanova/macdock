using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MacDock
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using (var mutex = new Mutex(true, "MacDock.SingleInstance", out bool created))
            {
                // Started by the updater: the old build is still closing, so wait for it to let go.
                if (!created && Array.IndexOf(args, UpdateService.AfterUpdateArg) >= 0)
                {
                    try { created = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created) return;

                var settings = Settings.Load();
                SystemTweaks.Init(settings); // also repairs state left by a crashed session
                if (args.Length > 0 && args[0] == "--restore") { SystemTweaks.RestoreAll(); return; }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                DockWindow dock = null;
                WinForms.NotifyIcon tray = null;

                void Restore()
                {
                    try { dock?.Cleanup(); } catch { }
                    SystemTweaks.RestoreAll();
                }

                var panel = new ControlWindow();
                void Apply(bool on)
                {
                    settings.DockEnabled = on;
                    settings.Save();
                    if (on)
                    {
                        dock = new DockWindow(settings);
                        if (settings.HideDesktopIcons) SystemTweaks.SetDesktopIconsHidden(true);
                        if (settings.HideTaskbar) SystemTweaks.SetTaskbarHidden(true);
                        dock.Show();
                        if (tray != null) tray.Dispose();
                        tray = CreateTray(dock, panel, settings);
                    }
                    else
                    {
                        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                        try { dock?.Cleanup(); dock?.Close(); } catch { }
                        dock = null;
                        SystemTweaks.RestoreAll();
                    }
                }
                panel.Toggled += Apply;
                panel.Closed += (a, e) => Application.Current.Shutdown();
                app.DispatcherUnhandledException += (a, e) => Restore();
                AppDomain.CurrentDomain.UnhandledException += (a, e) => Restore();
                AppDomain.CurrentDomain.ProcessExit += (a, e) => Restore();
                app.Exit += (a, e) => { if (tray != null) { tray.Visible = false; tray.Dispose(); } Restore(); };

                if (settings.DockEnabled)
                {
                    panel.SetOn(true);
                    Apply(true);
                }
                else panel.Show();
                UpdateService.Start(settings);
                app.Run();
            }
        }

        static WinForms.NotifyIcon CreateTray(DockWindow dock, ControlWindow panel, Settings settings)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(230, 60, 70, 90))) g.FillEllipse(b, 1, 1, 30, 30);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, 6, 20, 20, 5);
            }

            var menu = new WinForms.ContextMenuStrip();
            var icons = new WinForms.ToolStripMenuItem("Hide desktop icons") { CheckOnClick = true };
            var task = new WinForms.ToolStripMenuItem("Hide Windows taskbar") { CheckOnClick = true };
            icons.CheckedChanged += (a, e) => dock.SetHideIcons(icons.Checked);
            task.CheckedChanged += (a, e) => dock.SetHideTaskbar(task.Checked);
            var clock = new WinForms.ToolStripMenuItem("Clock widget");
            foreach (var (label, st) in new[] { ("Off", ClockStyle.Off), ("Analog", ClockStyle.Analog), ("Digital", ClockStyle.Digital) })
            {
                var cs = st;
                clock.DropDownItems.Add(new WinForms.ToolStripMenuItem(label, null, (a, e) => dock.SetClock(cs)) { Tag = cs });
            }
            menu.Opening += (a, e) =>
            {
                icons.Checked = dock.HideIcons; task.Checked = dock.HideTaskbar;
                foreach (WinForms.ToolStripMenuItem it in clock.DropDownItems) it.Checked = (ClockStyle)it.Tag == dock.Clock;
            };
            menu.Items.Add("Open MacDock window", null, (a, e) => panel.ShowPanel());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(icons);
            menu.Items.Add(task);
            menu.Items.Add(clock);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("ไลเซ่น & อัปเดต...", null, (a, e) => LicenseWindow.ShowSingle(settings));
            menu.Items.Add("เกี่ยวกับ MacDock (About)...", null, (a, e) => AboutWindow.ShowSingle());
            menu.Items.Add("Quit MacDock", null, (a, e) => Application.Current.Shutdown());

            return new WinForms.NotifyIcon
            {
                Icon = Icon.FromHandle(bmp.GetHicon()),
                Text = "MacDock",
                Visible = true,
                ContextMenuStrip = menu
            };
        }
    }
}
