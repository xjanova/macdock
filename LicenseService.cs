using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MacDock
{
    // Talks to the xman studio product API (https://xman4289.com/api/v1/product/macdock/*).
    internal static class XmanApi
    {
        public const string Site = "https://xman4289.com";
        public const string Slug = "macdock";
        const string ApiBase = Site + "/api/v1/product/" + Slug;

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            h.DefaultRequestHeaders.UserAgent.ParseAdd("MacDock/" + Brand.Version);
            return h;
        }

        public readonly struct Reply
        {
            public readonly int Status;
            public readonly JsonElement Body;
            public readonly bool HasJson;
            public Reply(int status, JsonElement body, bool hasJson) { Status = status; Body = body; HasJson = hasJson; }

            // Only a JSON answer that is not a server error / rate limit may change the license state:
            // an outage or a captive portal must never take a license away.
            public bool IsDefinitive => HasJson && Status < 500 && Status != 429;
            public bool Success => IsDefinitive && Status < 300 && Bool(Body, "success");
        }

        public static Task<Reply> PostAsync(string path, object payload, CancellationToken ct = default)
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return SendAsync(new HttpRequestMessage(HttpMethod.Post, ApiBase + path) { Content = content }, ct);
        }

        public static Task<Reply> GetAsync(string pathAndQuery, CancellationToken ct = default) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, ApiBase + pathAndQuery), ct);

        static async Task<Reply> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            try
            {
                using (req)
                using (var res = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using (var doc = JsonDocument.Parse(text))
                            return new Reply((int)res.StatusCode, doc.RootElement.Clone(), doc.RootElement.ValueKind == JsonValueKind.Object);
                    }
                    catch (JsonException) { return new Reply((int)res.StatusCode, default, false); }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                if (ct.IsCancellationRequested) throw;
                return new Reply(0, default, false);
            }
        }

        public static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public static long? Long(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : (long?)null;

        public static JsonElement Obj(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    }

    // Stable per-PC ids. MachineGuid alone changes with a Windows reinstall, so the SMBIOS UUID is mixed in.
    internal static class MachineIdentity
    {
        static string _id, _hw;
        static bool _hwRead;

        public static string MachineName => Environment.MachineName;

        public static string MachineId => _id ??= Sha256Hex("macdock-machine-v1|" + (MachineGuid() ?? Environment.MachineName + ":" + Environment.UserName) + "|" + (SmbiosUuid() ?? ""));

        public static string HardwareHash
        {
            get
            {
                if (!_hwRead) { string u = SmbiosUuid(); _hw = u == null ? null : Sha256Hex("macdock-hw|" + u); _hwRead = true; }
                return _hw;
            }
        }

        static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        static string MachineGuid()
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    return k?.GetValue("MachineGuid") as string;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll")]
        static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[] buffer, uint size);

        // SMBIOS type 1 (System Information) carries the board's UUID at offset 8.
        static string SmbiosUuid()
        {
            try
            {
                const uint RSMB = 0x52534D42;
                uint n = GetSystemFirmwareTable(RSMB, 0, null, 0);
                if (n < 8) return null;
                var b = new byte[n];
                if (GetSystemFirmwareTable(RSMB, 0, b, n) != n) return null;
                int end = (int)Math.Min(b.Length, 8L + BitConverter.ToUInt32(b, 4));
                int p = 8;
                while (p + 4 <= end)
                {
                    byte type = b[p], len = b[p + 1];
                    if (len < 4) break;
                    if (type == 1 && len >= 0x19 && p + 0x18 <= end)
                    {
                        var u = new byte[16];
                        Array.Copy(b, p + 8, u, 0, 16);
                        bool allSame = true;
                        for (int i = 1; i < 16; i++) if (u[i] != u[0]) { allSame = false; break; }
                        string s = new Guid(u).ToString();
                        // Blank or the well-known "not set by the OEM" placeholder: not a machine identity.
                        if (allSame || s == "03000200-0400-0500-0006-000700080009") return null;
                        return s;
                    }
                    if (type == 127) break;
                    int q = p + len;
                    while (q + 1 < end && !(b[q] == 0 && b[q + 1] == 0)) q++;
                    p = q + 2;
                }
            }
            catch { }
            return null;
        }
    }

    public enum LicenseCheck { Licensed, NotLicensed, Offline }

    // The free MacDock license: a FREE-… key on xman4289.com bound to this PC and to the customer's account.
    // It unlocks automatic updates. The server is asked every time it matters (check-machine), so the local
    // copy only feeds the UI — editing it unlocks nothing.
    internal static class LicenseService
    {
        public sealed class Info
        {
            public string Key { get; set; }
            public string Type { get; set; }
            public string MachineId { get; set; }
            public DateTime CheckedUtc { get; set; }
        }

        static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock", "license.json");

        static Info _current;
        static bool _loaded;

        public static event Action Changed;

        public static Info Current
        {
            get
            {
                if (!_loaded) { _current = Load(); _loaded = true; }
                return _current;
            }
        }

        public static bool IsLicensed => Current != null;

        public static string MaskedKey
        {
            get
            {
                string k = Current?.Key;
                if (string.IsNullOrEmpty(k)) return "";
                return k.Length <= 12 ? k : k.Substring(0, 9) + "…" + k.Substring(k.Length - 4);
            }
        }

        // Opened in the browser: log in / sign up on xman4289.com, press confirm, and the key is bound to this PC.
        public static string ClaimUrl =>
            XmanApi.Site + "/products/" + XmanApi.Slug + "/free-license?machine_id=" + MachineIdentity.MachineId +
            "&device=" + Uri.EscapeDataString(MachineIdentity.MachineName);

        public const string MyLicensesUrl = XmanApi.Site + "/my-account/licenses";

        static Info Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;
                var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(StorePath));
                // A store copied from another PC is not this PC's license.
                return info != null && !string.IsNullOrEmpty(info.Key) && info.MachineId == MachineIdentity.MachineId ? info : null;
            }
            catch { return null; }
        }

        static void Store(Info info)
        {
            bool changed = (_current?.Key) != (info?.Key);
            _current = info;
            _loaded = true;
            try
            {
                if (info == null) { if (File.Exists(StorePath)) File.Delete(StorePath); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                    File.WriteAllText(StorePath, JsonSerializer.Serialize(info));
                }
            }
            catch { }
            if (changed) Changed?.Invoke();
        }

        // Tells xman this PC exists (needed before the website can bind a free key to it).
        public static async Task<bool> RegisterDeviceAsync(CancellationToken ct = default)
        {
            var r = await XmanApi.PostAsync("/register-device", new
            {
                machine_id = MachineIdentity.MachineId,
                machine_name = MachineIdentity.MachineName,
                os_version = Environment.OSVersion.VersionString,
                app_version = Brand.Version,
                hardware_hash = MachineIdentity.HardwareHash
            }, ct).ConfigureAwait(false);
            return r.Success;
        }

        // Asks xman which key this PC holds. Only a definite answer changes the stored license.
        public static async Task<LicenseCheck> CheckAsync(CancellationToken ct = default)
        {
            var r = await XmanApi.PostAsync("/check-machine", new { machine_id = MachineIdentity.MachineId }, ct).ConfigureAwait(false);
            if (!r.IsDefinitive) return LicenseCheck.Offline;
            // PRODUCT_NOT_FOUND = the site is mid-deploy or not set up yet; not a verdict about this PC.
            if (r.Status == (int)HttpStatusCode.NotFound) return IsLicensed ? LicenseCheck.Licensed : LicenseCheck.NotLicensed;

            var data = XmanApi.Obj(r.Body, "data");
            string key = XmanApi.Str(data, "license_key");
            string status = XmanApi.Str(data, "status");
            if (r.Success && XmanApi.Bool(r.Body, "has_license") && !string.IsNullOrEmpty(key) && (status == null || status == "active"))
            {
                Store(new Info { Key = key, Type = XmanApi.Str(data, "license_type"), MachineId = MachineIdentity.MachineId, CheckedUtc = DateTime.UtcNow });
                return LicenseCheck.Licensed;
            }
            if (r.Success)
            {
                Store(null);
                return LicenseCheck.NotLicensed;
            }
            return IsLicensed ? LicenseCheck.Licensed : LicenseCheck.NotLicensed;
        }

        // After the browser was opened: polls until the customer has pressed confirm on the website.
        public static async Task<bool> WaitForClaimAsync(TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (await CheckAsync(ct).ConfigureAwait(false) == LicenseCheck.Licensed) return true;
                await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            }
            return false;
        }
    }
}
