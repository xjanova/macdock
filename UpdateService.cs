using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MacDock
{
    // Automatic updates from xman4289.com — only for PCs holding the free MacDock license.
    // Release = MacDock-<version>-win-x64.zip with MacDock.exe inside; update/check announces its sha256.
    internal static class UpdateService
    {
        public sealed class UpdateInfo
        {
            public string Version;
            public string DownloadUrl;
            public string Sha256;
            public long? Size;
            public string Changelog;
        }

        public enum Outcome { NoLicense, UpToDate, Offline, Declined, Failed, Restarting }

        static readonly HttpClient Download = CreateDownloadClient();
        static Settings _s;
        static DispatcherTimer _timer;
        static bool _busy;
        static string _declinedVersion;

        public const string AfterUpdateArg = "--after-update";

        static HttpClient CreateDownloadClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("MacDock/" + Brand.Version);
            return h;
        }

        public static string ExePath => Environment.ProcessPath;

        public static void Start(Settings s)
        {
            _s = s;
            CleanupOldExe();
            // First check shortly after start (the dock settles first), then every 6 hours.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _timer.Tick += async (a, b) =>
            {
                _timer.Interval = TimeSpan.FromHours(6);
                await LicenseService.RegisterDeviceAsync();
                await LicenseService.CheckAsync();
                if (_s.AutoUpdate) await RunAsync(interactive: false);
            };
            _timer.Start();
        }

        static void CleanupOldExe()
        {
            try { string old = ExePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }

        static Version Parse(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().TrimStart('v', 'V');
            int cut = v.IndexOfAny(new[] { '-', '+' });
            if (cut > 0) v = v.Substring(0, cut);
            return System.Version.TryParse(v, out var r) ? r : null;
        }

        public static async Task<(UpdateInfo info, bool offline)> CheckAsync(CancellationToken ct = default)
        {
            var r = await XmanApi.GetAsync("/update/check?current_version=" + Uri.EscapeDataString(Brand.Version), ct);
            if (!r.HasJson || r.Status >= 300) return (null, true);
            var b = r.Body;
            var latest = Parse(XmanApi.Str(b, "latest_version"));
            var current = Parse(Brand.Version);
            // Never "update" to the same or an older build, whatever the server says.
            if (!XmanApi.Bool(b, "has_update") || latest == null || current == null || latest <= current) return (null, false);
            return (new UpdateInfo
            {
                Version = XmanApi.Str(b, "latest_version").Trim().TrimStart('v', 'V'),
                DownloadUrl = XmanApi.Str(b, "download_url"),
                Sha256 = XmanApi.Str(b, "sha256"),
                Size = XmanApi.Long(b, "file_size"),
                Changelog = XmanApi.Str(b, "changelog")
            }, false);
        }

        // interactive = the customer pressed "check now": every outcome gets a message.
        public static async Task<Outcome> RunAsync(bool interactive)
        {
            if (_busy) return Outcome.Failed;
            _busy = true;
            try
            {
                var lic = await LicenseService.CheckAsync();
                if (lic == LicenseCheck.Offline && !LicenseService.IsLicensed) return Say(interactive, Outcome.Offline, "เชื่อมต่อ xman4289.com ไม่ได้ ลองใหม่ภายหลัง");
                if (!LicenseService.IsLicensed)
                    return Say(interactive, Outcome.NoLicense, "การอัปเดตอัตโนมัติต้องใช้ไลเซ่นฟรีของ XMAN Studio\nเปิดเมนู \"ไลเซ่น & อัปเดต\" แล้วกดรับไลเซ่นฟรีก่อน");

                var (info, offline) = await CheckAsync();
                if (offline) return Say(interactive, Outcome.Offline, "เชื่อมต่อ xman4289.com ไม่ได้ ลองใหม่ภายหลัง");
                if (info == null) return Say(interactive, Outcome.UpToDate, "MacDock เป็นเวอร์ชันล่าสุดแล้ว (" + Brand.Version + ")");
                if (!interactive && info.Version == _declinedVersion) return Outcome.Declined;

                string newExe;
                try { newExe = await DownloadAndVerifyAsync(info); }
                catch (Exception ex) { return Say(interactive, Outcome.Failed, "ดาวน์โหลดอัปเดตไม่สำเร็จ: " + ex.Message); }

                string msg = "MacDock เวอร์ชันใหม่ " + info.Version + " พร้อมติดตั้งแล้ว (ตอนนี้ " + Brand.Version + ")";
                if (!string.IsNullOrWhiteSpace(info.Changelog))
                    msg += "\n\nมีอะไรใหม่:\n" + Trim(info.Changelog, 600);
                msg += "\n\nอัปเดตและเปิด MacDock ใหม่เลยไหม?";
                if (MessageBox.Show(msg, "อัปเดต MacDock", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                {
                    _declinedVersion = info.Version;
                    return Outcome.Declined;
                }

                try { SwapAndRestart(newExe); }
                catch (Exception ex) { return Say(true, Outcome.Failed, "ติดตั้งอัปเดตไม่สำเร็จ: " + ex.Message + "\nดาวน์โหลดเองได้ที่ " + XmanApi.Site + "/products/" + XmanApi.Slug); }
                return Outcome.Restarting;
            }
            finally { _busy = false; }
        }

        static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        static Outcome Say(bool show, Outcome o, string text)
        {
            if (show) MessageBox.Show(text, "อัปเดต MacDock", MessageBoxButton.OK, o == Outcome.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return o;
        }

        static async Task<string> DownloadAndVerifyAsync(UpdateInfo info)
        {
            // Owner rule: apps download only from xman4289.com.
            if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(url.Host, new Uri(XmanApi.Site).Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ลิงก์ดาวน์โหลดไม่ใช่ของ xman4289.com");
            if (string.IsNullOrWhiteSpace(info.Sha256) || info.Sha256.Trim().Length != 64)
                throw new InvalidOperationException("เซิร์ฟเวอร์ไม่ได้ส่งค่า sha256 ของไฟล์มา");

            string dir = Path.Combine(Path.GetTempPath(), "MacDock-update-" + info.Version);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "update.zip");

            using (var res = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                string type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (type.StartsWith("text/") || type.Contains("json")) throw new InvalidOperationException("ได้หน้าเว็บแทนไฟล์อัปเดต");
                using (var src = await res.Content.ReadAsStreamAsync())
                using (var dst = File.Create(zip))
                    await src.CopyToAsync(dst);
            }

            if (info.Size.HasValue && new FileInfo(zip).Length != info.Size.Value) throw new InvalidOperationException("ขนาดไฟล์ไม่ตรงกับที่เซิร์ฟเวอร์ประกาศ");
            string hash;
            using (var f = File.OpenRead(zip)) hash = Convert.ToHexString(SHA256.HashData(f));
            if (!string.Equals(hash, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ไฟล์เสียหาย (sha256 ไม่ตรง)");

            string newExe = Path.Combine(dir, "MacDock.exe");
            using (var z = ZipFile.OpenRead(zip))
            {
                var entry = z.Entries.FirstOrDefault(e => string.Equals(e.FullName, "MacDock.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("ในไฟล์อัปเดตไม่มี MacDock.exe");
                entry.ExtractToFile(newExe, true);
            }

            // The exe inside must really be the announced version.
            var fv = FileVersionInfo.GetVersionInfo(newExe);
            var inside = Parse(fv.ProductVersion);
            if (inside == null || inside != Parse(info.Version)) throw new InvalidOperationException("เวอร์ชันในไฟล์ไม่ตรงกับที่ประกาศ");
            return newExe;
        }

        // A running exe can be renamed (not overwritten): move it aside, put the new one in its place, start it.
        static void SwapAndRestart(string newExe)
        {
            string exe = ExePath;
            string old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(newExe, exe); }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }
            Process.Start(new ProcessStartInfo(exe, AfterUpdateArg) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            Application.Current.Shutdown();
        }
    }
}
