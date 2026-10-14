using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MacDock
{
    public enum DockEdge { Bottom, Left, Right, Top }

    public class Settings
    {
        public DockEdge Edge { get; set; } = DockEdge.Top;
        public double IconSize { get; set; } = 56;
        public double Zoom { get; set; } = 1.7;
        public bool Magnify { get; set; } = true;
        public bool HideDesktopIcons { get; set; } = true;
        public bool HideTaskbar { get; set; } = true;
        public bool ShowRunning { get; set; } = true;
        public bool Imported { get; set; }
        public bool DockEnabled { get; set; }
        public int TaskbarOriginal { get; set; } = -1;
        public List<string> Pinned { get; set; } = new List<string>();
        public List<string> Categories { get; set; } = new List<string>();
        public List<string> Collapsed { get; set; } = new List<string>();
        public Dictionary<string, string> PinCats { get; set; } = new Dictionary<string, string>();
        public bool TopMigrated { get; set; }
        public bool SmartSort { get; set; } = true;
        public bool AutoHide { get; set; } = true;
        public Dictionary<string, double> Usage { get; set; } = new Dictionary<string, double>();
        public string UsageDay { get; set; } = "";
        public ClockStyle Clock { get; set; } = ClockStyle.Off;
        // Nullable rather than NaN: System.Text.Json refuses to serialize NaN and Save() would silently fail.
        public double? ClockX { get; set; }
        public double? ClockY { get; set; }
        public bool ClockOnTop { get; set; }
        public bool AutoUpdate { get; set; } = true;

        static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "settings.json");
        static readonly string PortablePath = Path.Combine(AppContext.BaseDirectory, "MacDock.settings.json");

        static string _filePath;
        public static string FilePath => _filePath ??= ResolvePath();
        public static bool IsPortable => FilePath == PortablePath;

        // Portable: settings live beside the exe so the folder can be carried to another PC.
        // Falls back to %APPDATA% only when the exe folder is read-only (e.g. Program Files).
        static string ResolvePath()
        {
            if (File.Exists(PortablePath)) return PortablePath;
            if (!CanWrite(AppContext.BaseDirectory)) return AppDataPath;
            try { if (File.Exists(AppDataPath)) File.Copy(AppDataPath, PortablePath); } catch { }
            return PortablePath;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".macdock-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static Settings Load()
        {
            Settings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch { }
            s = s ?? new Settings();
            s.Categories = s.Categories ?? new List<string>();
            s.Collapsed = s.Collapsed ?? new List<string>();
            s.PinCats = s.PinCats ?? new Dictionary<string, string>();
            s.Usage = new Dictionary<string, double>(s.Usage ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
            s.DecayUsage();
            if (!s.TopMigrated)
            {
                s.Edge = DockEdge.Top;
                s.TopMigrated = true;
                s.Save();
            }
            return s;
        }

        // Usage score decays ~7% per day so recent habits outweigh old ones.
        void DecayUsage()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (UsageDay == today) return;
            if (DateTime.TryParse(UsageDay, out var last))
            {
                int days = Math.Min(90, Math.Max(0, (int)(DateTime.Now.Date - last.Date).TotalDays));
                if (days > 0)
                {
                    double f = Math.Pow(0.93, days);
                    foreach (var k in Usage.Keys.ToList())
                    {
                        Usage[k] *= f;
                        if (Usage[k] < 1) Usage.Remove(k);
                    }
                }
            }
            UsageDay = today;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
