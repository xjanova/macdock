using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MacDock
{
    // Guesses a category for a pinned item from its name, target exe, version info and .url target.
    public static class CategoryClassifier
    {
        public const string Other = "อื่นๆ";
        const string Games = "เกม";
        const string Folders = "โฟลเดอร์";
        const string DocFiles = "ไฟล์เอกสาร";
        const string MediaFiles = "ไฟล์สื่อ";
        const string WebLinks = "ลิงก์เว็บ";

        // First matching rule wins, and categories are shown in this order.
        // Dev sits before Games so "Roblox Studio" / "Unity Hub" are not taken by the game keywords.
        static readonly (string Name, string[] Keywords)[] Rules =
        {
            ("AI", new[] { "claude", "cluadex", "chatgpt", "openai", "grok", "gemini", "copilot", "perplexity", "ollama",
                "lm studio", "lmstudio", "brainx", "deepseek", "poe", "midjourney", "comfyui", "stable diffusion", "jan" }),
            ("เบราว์เซอร์", new[] { "chrome", "google chrome", "firefox", "msedge", "microsoft edge", "opera", "brave", "vivaldi",
                "browser", "tor browser", "waterfox", "thorium", "chromium", "maxthon", "yandex", "internet explorer" }),
            ("แชท & ประชุม", new[] { "discord", "line", "telegram", "whatsapp", "messenger", "slack", "teams", "zoom",
                "zoom workplace", "skype", "signal", "wechat", "viber", "kakaotalk", "webex", "google meet" }),
            ("นักพัฒนา", new[] { "visual studio", "devenv", "vs code", "vscode", "code", "rider", "intellij", "pycharm", "webstorm",
                "clion", "goland", "jetbrains", "android studio", "unity", "unity hub", "unreal", "unreal engine", "godot",
                "roblox studio", "git", "git bash", "github", "github desktop", "gitkraken", "sourcetree", "postman", "insomnia",
                "docker", "terminal", "windows terminal", "powershell", "pwsh", "cmd", "command prompt", "nodejs", "python",
                "sublime", "sublime text", "notepad++", "cursor", "windsurf", "wsl", "putty", "winscp", "filezilla", "dbeaver",
                "sql", "ssms", "mysql", "heidisql", "laragon", "xampp", "ida", "ghidra", "x64dbg", "dnspy", "ilspy", "fiddler",
                "wireshark", "cmake", "arduino", "arduino ide", "vmware", "virtualbox" }),
            ("เกม", new[] { "steam", "valve", "epic games", "epicgameslauncher", "gog galaxy", "battle net", "battlenet", "blizzard",
                "riot", "riot games", "riot client", "valorant", "league of legends", "minecraft", "roblox", "ubisoft", "uplay",
                "ea app", "origin", "xbox", "genshin", "hoyoverse", "honkai", "zenless", "wuthering", "kuro", "rockstar", "garena",
                "game", "games", "gamehouse", "playnite", "fortnite", "dota", "counter strike", "pubg", "gta", "cities", "cities2",
                "paradox", "scum", "aion", "ncsoft", "stellar blade", "nox", "noxplayer", "bluestacks", "ldplayer", "memu" }),
            ("งานเอกสาร", new[] { "word", "winword", "excel", "powerpoint", "powerpnt", "outlook", "onenote", "office",
                "libreoffice", "openoffice", "wps", "acrobat", "acrord32", "pdf", "foxit", "notion", "obsidian", "evernote",
                "visio", "thunderbird", "mail", "calendar", "todo", "trello", "erp", "sap", "express", "notepad" }),
            ("กราฟิก & ตัดต่อ", new[] { "photoshop", "illustrator", "premiere", "after effects", "lightroom", "indesign",
                "adobe", "creative cloud", "gimp", "krita", "blender", "figma", "canva", "paint", "mspaint", "inkscape",
                "davinci", "resolve", "capcut", "obs", "obs studio", "obs64", "affinity", "clip studio", "3ds max", "maya",
                "zbrush", "substance", "cinema 4d", "sketchup", "autocad", "fusion 360", "audacity", "fl studio", "ableton",
                "reaper", "camtasia", "filmora", "vegas", "handbrake", "shotcut", "kdenlive", "aseprite", "vroid", "vroidstudio",
                "tiktok", "tiktok live studio", "streamlabs" }),
            ("เพลง & วิดีโอ", new[] { "spotify", "vlc", "potplayer", "media player", "wmplayer", "itunes", "music", "netflix",
                "youtube", "mpc", "foobar2000", "aimp", "kmplayer", "musicbee", "tidal", "deezer", "plex", "kodi", "stremio",
                "movies", "photos", "viu", "joox", "karaplayer", "karaoke", "nck" }),
            ("เครื่องพิมพ์ & สแกน", new[] { "epson", "hp", "hp smart", "hp scan", "canon", "brother", "printer", "print",
                "scan", "scanner", "smart tank", "xerox", "ricoh", "fuji", "kyocera" }),
            ("เครื่องมือ", new[] { "settings", "control panel", "task manager", "taskmgr", "explorer", "file explorer",
                "this pc", "recycle bin", "7 zip", "7zip", "7zfm", "winrar", "bandizip", "peazip", "everything", "ccleaner",
                "nvidia", "geforce", "amd", "radeon", "intel", "logitech", "logi", "g hub", "razer", "corsair", "steelseries",
                "cpu z", "gpu z", "hwinfo", "hwmonitor", "afterburner", "msi afterburner", "anydesk", "teamviewer", "rustdesk",
                "parsec", "onedrive", "dropbox", "google drive", "vpn", "nordvpn", "expressvpn", "warp", "calculator",
                "snipping", "sharex", "lightshot", "greenshot", "powertoys", "uninstall", "revo", "bitwarden", "keepass",
                "torrent", "utorrent", "qbittorrent", "idm", "downloader", "video downloader", "tokkit", "crystaldiskinfo",
                "rufus", "malwarebytes", "defender", "regedit", "autohotkey", "macdock", "microsoft store", "remote desktop",
                "mstsc" }),
        };

        static readonly HashSet<string> DocExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf", ".odt", ".ods", ".odp", ".json", ".xml", ".log", ".lrc" };
        static readonly HashSet<string> MediaExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".psd", ".mp4", ".mkv", ".avi", ".mov", ".mp3", ".wav", ".flac", ".m4a" };
        static readonly HashSet<string> ProgramExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".lnk", ".appref-ms", ".bat", ".cmd", ".msc", ".cpl" };

        public static readonly string[] Order =
            Rules.Select(r => r.Name).Concat(new[] { Folders, DocFiles, MediaFiles, WebLinks, Other }).ToArray();

        public static string Classify(DockEntry e)
        {
            string path = e.Path ?? "";
            string target = e.Resolved ?? "";
            string ext = Path.GetExtension(path);

            if (Directory.Exists(path) || (target.Length > 0 && Directory.Exists(target))) return Folders;

            // The item's own name is the strongest hint: a Chrome web-app shortcut named "Claude" targets chrome.exe.
            string nameText = Tokens(e.Name, Path.GetFileNameWithoutExtension(path));

            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase) || ext.Equals(".website", StringComparison.OrdinalIgnoreCase))
            {
                string url = ReadUrl(path);
                var byName = MatchRules(nameText) ?? MatchRules(Tokens(url));
                if (byName != null) return byName;
                // Non-web schemes (steam://, com.epicgames.launcher://, ...) are game launcher links.
                return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.Length == 0 ? WebLinks : Games;
            }

            string realExt = target.Length > 0 ? Path.GetExtension(target) : ext;
            if (DocExt.Contains(realExt)) return DocFiles;
            if (MediaExt.Contains(realExt)) return MediaFiles;

            var hit = MatchRules(nameText) ?? MatchRules(TargetText(target));
            if (hit != null) return hit;
            return ProgramExt.Contains(ext) || ProgramExt.Contains(realExt) ? Other : DocFiles;
        }

        static string ReadUrl(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
            }
            catch { }
            return "";
        }

        static string TargetText(string target)
        {
            if (target.Length == 0) return " ";
            var parts = new List<string>
            {
                Path.GetFileNameWithoutExtension(target),
                // Parent folder names often carry the vendor/app ("Steam\steamapps", "JetBrains\Rider").
                Path.GetFileName(Path.GetDirectoryName(target) ?? "")
            };
            if (string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(target);
                    parts.Add(vi.FileDescription);
                    parts.Add(vi.ProductName);
                    parts.Add(vi.CompanyName);
                }
                catch { }
            }
            return Tokens(parts.ToArray());
        }

        // Lower-case and split on anything non-alphanumeric so "Code.exe" -> " code exe ", "7-Zip" -> " 7 zip ".
        static string Tokens(params string[] parts)
        {
            string raw = string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))).ToLowerInvariant();
            raw = Regex.Replace(raw, @"(?<!\+)\+(?!\+)", " "); // keep "notepad++", drop the lone "+" in "Downloader+"
            return " " + Regex.Replace(raw, @"[^a-z0-9\u0E00-\u0E7F+]+", " ").Trim() + " ";
        }

        static string MatchRules(string text)
        {
            foreach (var (name, kws) in Rules)
                foreach (var kw in kws)
                    if (text.Contains(" " + kw + " ")) return name;
            return null;
        }
    }
}
