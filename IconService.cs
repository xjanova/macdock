using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock
{
    internal static class IconService
    {
        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        // High-res icon (256px) via the shell image factory; falls back to the small associated icon.
        public static ImageSource GetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Cache.TryGetValue(path, out var cached)) return cached;
            ImageSource img = null;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
                // SIIGBF_ICONONLY(0x4) | SIIGBF_BIGGERSIZEOK(0x1)
                int hr = factory.GetImage(new Native.SIZE { cx = 256, cy = 256 }, 0x4 | 0x1, out IntPtr hbm);
                if (hr == 0 && hbm != IntPtr.Zero)
                {
                    img = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    Native.DeleteObject(hbm);
                    img.Freeze();
                }
            }
            catch { }
            if (img == null)
            {
                try
                {
                    using (var ic = Icon.ExtractAssociatedIcon(path))
                    {
                        if (ic != null)
                        {
                            img = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            img.Freeze();
                        }
                    }
                }
                catch { }
            }
            if (img != null) Cache[path] = img;
            return img;
        }

        public static ImageSource FromWindow(IntPtr hwnd)
        {
            try
            {
                // ICON_BIG = 1, GCL_HICON = -14
                IntPtr h = Native.SendMessage(hwnd, Native.WM_GETICON, (IntPtr)1, IntPtr.Zero);
                if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, -14);
                if (h == IntPtr.Zero) return null;
                var s = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            catch { return null; }
        }

        // Resolve a .lnk to its target exe path (used to match pinned items with running processes).
        public static string ResolveShortcut(string lnk)
        {
            try
            {
                if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return lnk;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(lnk);
                string target = sc.TargetPath;
                return string.IsNullOrEmpty(target) ? lnk : target;
            }
            catch { return lnk; }
        }

        public static string DisplayName(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
        }
    }
}
