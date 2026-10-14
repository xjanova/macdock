using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MacDock
{
    // Hides / restores the Windows taskbar and desktop icons.
    // The taskbar's original state is persisted so a crashed session can be repaired on next start.
    internal static class SystemTweaks
    {
        static Settings _s;

        public static void Init(Settings s)
        {
            _s = s;
            if (_s.TaskbarOriginal >= 0) SetTaskbarHidden(false); // previous session died while hidden
            SetDesktopIconsHidden(false);
        }

        public static void RestoreAll()
        {
            try { SetDesktopIconsHidden(false); } catch { }
            try { SetTaskbarHidden(false); } catch { }
        }

        public static void SetTaskbarHidden(bool hide)
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
            if (hide)
            {
                if (_s.TaskbarOriginal < 0)
                {
                    _s.TaskbarOriginal = (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd);
                    _s.Save();
                }
                abd.lParam = (IntPtr)(_s.TaskbarOriginal | Native.ABS_AUTOHIDE);
                Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
            }
            else if (_s.TaskbarOriginal >= 0)
            {
                StopPeek();
                abd.lParam = (IntPtr)_s.TaskbarOriginal;
                Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
                _s.TaskbarOriginal = -1;
                _s.Save();
            }

            ShowTaskbars(!hide);
        }

        static void ShowTaskbars(bool show)
        {
            int cmd = show ? Native.SW_SHOW : Native.SW_HIDE;
            Native.ShowWindow(Native.FindWindow("Shell_TrayWnd", null), cmd);
            IntPtr second = IntPtr.Zero;
            while ((second = Native.FindWindowEx(IntPtr.Zero, second, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                Native.ShowWindow(second, cmd);
        }

        static bool AnyTaskbarVisible()
        {
            if (Native.IsWindowVisible(Native.FindWindow("Shell_TrayWnd", null))) return true;
            IntPtr second = IntPtr.Zero;
            while ((second = Native.FindWindowEx(IntPtr.Zero, second, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (Native.IsWindowVisible(second)) return true;
            return false;
        }

        // Called periodically: Explorer restarts bring the taskbar / icons back.
        public static void Enforce(bool hideTaskbar, bool hideIcons)
        {
            if (hideIcons) SetDesktopIconsHidden(true);
            if (hideTaskbar && _peek == null && Native.IsWindowVisible(Native.FindWindow("Shell_TrayWnd", null)))
                SetTaskbarHidden(true);
        }

        // ---------------------------------------------------------------- tray peek
        // Brings the (possibly hidden) taskbar up so the notification-area icons (volume, network, apps)
        // can be used, then hides it again once the user is done with it.

        static DispatcherTimer _peek;
        static DateTime _peekSince, _peekLastUse;
        static DateTime _settleUntil = DateTime.MinValue; // after a peek: keep slapping the taskbar back down
        static IntPtr _peekPrevFg;
        static bool _peekPinned; // auto-hide was switched off to force the taskbar on screen

        public static void PeekTray()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            if (_settleUntil == DateTime.MinValue && !IsShellWindow(Native.GetForegroundWindow()))
                _peekPrevFg = Native.GetForegroundWindow();
            _settleUntil = DateTime.MinValue;
            Native.ShowWindow(tray, Native.SW_SHOW);
            // Win+B: slides an auto-hide taskbar in and focuses the notification area.
            // Synthetic input is not subject to the foreground lock that blocks SetForegroundWindow.
            Native.keybd_event(Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_B, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_B, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.keybd_event(Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (_s == null || !_s.HideTaskbar) return;

            _peekSince = _peekLastUse = DateTime.Now;
            if (_peek != null) return;
            _peek = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _peek.Tick += (a, b) => PeekTick();
            _peek.Start();
        }

        // Fallback when Win+B did not bring an auto-hidden taskbar on screen: drop auto-hide for the peek.
        static void PinIfOffScreen()
        {
            if (_peekPinned || TaskbarOnScreen()) return;
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
            abd.lParam = (IntPtr)(Math.Max(_s.TaskbarOriginal, 0) & ~Native.ABS_AUTOHIDE);
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
            Native.ShowWindow(Native.FindWindow("Shell_TrayWnd", null), Native.SW_SHOW);
            _peekPinned = true;
        }

        static bool TaskbarOnScreen()
        {
            if (!Native.GetWindowRect(Native.FindWindow("Shell_TrayWnd", null), out var r)) return false;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            int w = Math.Min(r.Right, sw) - Math.Max(r.Left, 0), h = Math.Min(r.Bottom, sh) - Math.Max(r.Top, 0);
            int thick = Math.Min(r.Right - r.Left, r.Bottom - r.Top);
            return w > 0 && h > 0 && Math.Min(w, h) >= thick * 0.6;
        }

        static void PeekTick()
        {
            if (!_s.HideTaskbar) { StopPeek(); return; }
            var now = DateTime.Now;

            // Settle phase: Explorer re-shows the taskbar after ABM_SETSTATE / focus changes; hide it again at once.
            if (_settleUntil != DateTime.MinValue)
            {
                if (AnyTaskbarVisible()) ShowTaskbars(false);
                if (now >= _settleUntil) StopPeek();
                return;
            }

            if ((now - _peekSince).TotalMilliseconds >= 500) PinIfOffScreen();
            if (TrayInUse()) _peekLastUse = now;
            if ((now - _peekSince).TotalSeconds < 1.5 || (now - _peekLastUse).TotalSeconds < 0.8) return;
            EndPeek();
        }

        static void EndPeek()
        {
            ShowTaskbars(false);
            if (_peekPinned)
            {
                var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
                abd.lParam = (IntPtr)(Math.Max(_s.TaskbarOriginal, 0) | Native.ABS_AUTOHIDE);
                Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
                _peekPinned = false;
            }
            // Win+B left keyboard focus on the taskbar; while it owns focus Explorer keeps popping it back up.
            if (IsShellWindow(Native.GetForegroundWindow()) && _peekPrevFg != IntPtr.Zero && Native.IsWindowVisible(_peekPrevFg))
            {
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt tap unlocks SetForegroundWindow
                Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Native.SetForegroundWindow(_peekPrevFg);
            }
            _peekPrevFg = IntPtr.Zero;
            ShowTaskbars(false);
            _settleUntil = DateTime.Now.AddSeconds(2.5);
        }

        static void StopPeek()
        {
            _peek?.Stop();
            _peek = null;
            _peekPinned = false;
            _settleUntil = DateTime.MinValue;
        }

        static bool IsShellWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            var cls = new StringBuilder(64);
            Native.GetClassName(h, cls, cls.Capacity);
            string c = cls.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow"
                || c == "TopLevelWindowForOverflowXamlIsland";
        }

        static readonly string[] ShellHosts = { "explorer", "ShellExperienceHost", "ShellHost", "StartMenuExperienceHost", "SearchHost" };

        // Cursor over the taskbar, a shell flyout (volume / network / overflow) in front, or a tray icon's menu open.
        static bool TrayInUse()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (Native.GetCursorPos(out var p) && Native.GetWindowRect(tray, out var r)
                && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return true;

            IntPtr menu = Native.FindWindow("#32768", null);
            if (menu != IntPtr.Zero && Native.IsWindowVisible(menu)) return true;

            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var cls = new StringBuilder(256);
            Native.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "CabinetWClass" || c == "Progman" || c == "WorkerW") return false; // File Explorer / desktop
            Native.GetWindowThreadProcessId(fg, out uint pid);
            string exe = Native.GetProcessPath(pid);
            if (exe == null) return false;
            string name = Path.GetFileNameWithoutExtension(exe);
            foreach (var h in ShellHosts)
                if (string.Equals(name, h, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void SetDesktopIconsHidden(bool hide)
        {
            IntPtr lv = FindDesktopListView();
            if (lv != IntPtr.Zero) Native.ShowWindow(lv, hide ? Native.SW_HIDE : Native.SW_SHOW);
        }

        static IntPtr FindDesktopListView()
        {
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                // Wallpaper-hosting layouts: the DefView lives under a WorkerW sibling.
                IntPtr worker = IntPtr.Zero;
                while ((worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                {
                    defView = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero) break;
                }
            }
            return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }
    }
}
