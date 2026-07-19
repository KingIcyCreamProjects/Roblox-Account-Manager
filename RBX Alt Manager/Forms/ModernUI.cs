using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Nexus;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    // Modern WebView2-hosted front-end for KingsRAM. Hosts the redesigned HTML/JS UI and bridges it to
    // the existing backend (AccountsList, Account.JoinServer, presence, etc.). It is additive: the classic
    // AccountManager form remains the "engine" (it loads accounts, runs timers) and is hidden while this
    // window is shown. If WebView2 fails to initialize, we fall back to the classic UI so the app never bricks.
    public partial class ModernUI : Form
    {
        public static ModernUI Instance;
        private static bool Launched;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);

        private WebView2 web;
        private Form owner;
        private System.Windows.Forms.Timer pushTimer;
        private System.Windows.Forms.Timer resTimer; // v2: faster resource poll (CPU/RAM + dynamic priority)
        private bool ready;
        private bool switchingToClassic;

        // Latest per-account live process stats (username -> stat), refreshed by the resource poll timer.
        private Dictionary<string, ResourceManager.Stat> lastStats = new Dictionary<string, ResourceManager.Stat>();

        // Unauthenticated client for the public server/games browser (games.roblox.com needs no cookie).
        private static readonly RestClient GamesClient = new RestClient("https://games.roblox.com/");

        // Open the modern UI, hiding the classic window on success. Safe to call more than once (no-ops after first).
        public static void Launch(Form classicOwner)
        {
            if (Launched) return;
            Launched = true;

            try
            {
                var ui = new ModernUI(classicOwner);
                ui.Show();
            }
            catch (Exception ex)
            {
                Launched = false;
                Program.Logger.Error($"[ModernUI] launch failed, staying on classic UI: {ex}");
                try { classicOwner?.Show(); } catch { }
            }
        }

        private ModernUI(Form classicOwner)
        {
            Instance = this;
            owner = classicOwner;

            Text = "KingsRAM";
            try { Icon = Properties.Resources.team_KX4_icon; } catch { }
            BackColor = Color.FromArgb(7, 11, 20);
            StartPosition = FormStartPosition.CenterScreen;
            // Compact "little app" footprint like the original RAM — not a full-window app.
            ClientSize = new Size(920, 560);
            MinimumSize = new Size(800, 460);

            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);

            HandleCreated += (s, e) => ApplyDarkTitleBar();
            Load += async (s, e) => { ApplyDarkTitleBar(); await InitAsync(); };
            FormClosed += (s, e) => { if (!switchingToClassic) { try { Application.Exit(); } catch { } } };
        }

        private async Task InitAsync()
        {
            try
            {
                // Keep the WebView2 user-data out of the app folder (avoids clutter + permission issues).
                string udf = Path.Combine(Path.GetTempPath(), "KingsRAM_WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await web.EnsureCoreWebView2Async(env);

                var settings = web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
#if DEBUG
                settings.AreDevToolsEnabled = true;
#else
                settings.AreDevToolsEnabled = false;
#endif
                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                web.CoreWebView2.NavigateToString(LoadHtml());

                // Push presence-refreshed data on a light interval (the classic engine refreshes Presence for us).
                pushTimer = new System.Windows.Forms.Timer { Interval = 12000 };
                pushTimer.Tick += (s, e) => { if (ready) PushAccounts("accounts"); };
                pushTimer.Start();

                // Faster loop for live CPU/RAM stats + dynamic per-instance priority (needs ~seconds between CPU samples).
                resTimer = new System.Windows.Forms.Timer { Interval = 3500 };
                resTimer.Tick += (s, e) => PollStats();
                resTimer.Start();

                // Success — retire the classic window.
                try { owner?.Hide(); } catch { }
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"[ModernUI] WebView2 init failed: {ex}");
                MessageBox.Show(this,
                    "The modern UI needs the Microsoft Edge WebView2 Runtime, which couldn't start.\n\n" +
                    "KingsRAM will use the classic interface instead. Install the WebView2 Runtime to enable the new UI.",
                    "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Launched = false;
                try { owner?.Show(); } catch { }
                try { pushTimer?.Stop(); } catch { }
                try { resTimer?.Stop(); } catch { }
                // Fall back to classic WITHOUT tearing down the process: FormClosed calls Application.Exit()
                // unless switchingToClassic is set. Without this the just-reshown owner window dies too and the
                // default UseModernUI=true path re-bricks on every relaunch. Mirrors SwitchToClassic().
                switchingToClassic = true;
                Close();
            }
        }

        // Dark native title bar (kills the white Windows title bar). Try attr 19 (older builds) then 20.
        private void ApplyDarkTitleBar()
        {
            try { if (DwmSetWindowAttribute(Handle, 19, new[] { 1 }, 4) != 0) DwmSetWindowAttribute(Handle, 20, new[] { 1 }, 4); }
            catch { }
        }

        private string LoadHtml()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("ui.html", StringComparison.OrdinalIgnoreCase));
                if (name != null)
                    using (var sr = new StreamReader(asm.GetManifestResourceStream(name)))
                        return sr.ReadToEnd();
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] LoadHtml: {ex}"); }
            return "<body style='background:#070B14;color:#E8EDF8;font-family:sans-serif;padding:40px'><h2>UI resource missing.</h2></body>";
        }

        // ---------------- C# -> JS ----------------

        private void Post(object o)
        {
            try { web?.CoreWebView2?.PostWebMessageAsJson(JsonConvert.SerializeObject(o)); } catch { }
        }

        private void PushAccounts(string type = "init")
        {
            try
            {
                var accounts = (AccountManager.AccountsList ?? new List<Account>()).Select(MapAccount).ToList();
                Post(new { type, accounts, settings = MapSettings() });
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] PushAccounts: {ex}"); }
        }

        // Poll live per-instance CPU/RAM and apply dynamic priority/affinity, then push a lightweight stats
        // message the UI patches onto existing rows (no full re-render, so it never disrupts scroll/typing).
        private void PollStats()
        {
            try
            {
                var byTracker = ResourceManager.Poll();
                var byUser = new Dictionary<string, ResourceManager.Stat>();
                foreach (var a in AccountManager.AccountsList ?? new List<Account>())
                    if (a != null && !string.IsNullOrEmpty(a.BrowserTrackerID) && byTracker.TryGetValue(a.BrowserTrackerID, out var st))
                        byUser[a.Username] = st;
                lastStats = byUser;

                if (ready)
                {
                    var stats = new Dictionary<string, object>();
                    foreach (var kv in byUser) stats[kv.Key] = new { cpu = kv.Value.Cpu, ram = kv.Value.RamMB, min = kv.Value.Minimized };
                    Post(new { type = "stats", stats });
                }
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] PollStats: {ex}"); }
        }

        private object MapSettings()
        {
            var g = AccountManager.General; var ac = AccountManager.AccountControl; var dev = AccountManager.Developer;
            string GetS(IniSection f, string k, string dflt) { try { return f != null && f.Exists(k) ? f.Get(k) : dflt; } catch { return dflt; } }
            bool GetB(IniSection f, string k) { try { return f != null && f.Get<bool>(k); } catch { return false; } }
            bool GetBd(IniSection f, string k, bool dflt) { try { return f != null && f.Exists(k) ? f.Get<bool>(k) : dflt; } catch { return dflt; } }
            int GetI(IniSection f, string k, int dflt) { try { return f != null && f.Exists(k) ? f.Get<int>(k) : dflt; } catch { return dflt; } }
            try
            {
                return new
                {
                    version = AppVersion(),
                    placeId = GetS(g, "SavedPlaceId", "5315046213"),
                    jobId = GetS(g, "SavedJobId", ""),
                    accent = GetS(g, "ModernAccent", "cyan"),
                    density = GetS(g, "ModernDensity", "comfortable"),
                    theme = GetS(g, "ModernTheme", "dark"),
                    toggles = new
                    {
                        checkUpdates = GetB(g, "CheckForUpdates"),
                        savePw = GetB(g, "SavePasswords"),
                        disableAging = GetB(g, "DisableAgingAlert"),
                        autoCookie = GetB(g, "AutoCookieRefresh"),
                        async = GetB(g, "AsyncJoin"),
                        shuffleLowest = GetB(g, "ShuffleChoosesLowestServer"),
                        fpsUnlock = GetB(g, "UnlockFPS"),
                        multiRoblox = GetB(g, "EnableMultiRbx"),
                        webApi = GetB(dev, "EnableWebServer"),
                        autoRelaunch = GetB(ac, "StartOnLaunch"),
                    },
                    steppers = new
                    {
                        launchDelay = GetI(g, "AccountJoinDelay", 8),
                        maxRecent = GetI(g, "MaxRecentGames", 8),
                        maxFps = GetI(g, "MaxFPSValue", 120),
                        relaunchDelay = GetI(ac, "RelaunchDelay", 60),
                        connPort = GetI(ac, "NexusPort", 5242),
                    },
                    // v2 multi-Roblox resource manager (see Classes/ResourceManager.cs).
                    perf = new
                    {
                        manage = GetBd(g, "PerfManage", true),
                        dynamic = GetBd(g, "PerfDynamicPriority", true),
                        bgPriority = GetS(g, "PerfBackgroundPriority", "below"),
                        affinity = GetS(g, "PerfAffinityMode", "all"),
                        trimOnMin = GetBd(g, "PerfTrimOnMinimize", false),
                        autoMin = GetBd(g, "PerfAutoMinimizeAlts", false),
                        lowGfx = GetBd(g, "PerfLowGraphics", false),
                        cores = Environment.ProcessorCount,
                    },
                };
            }
            catch { return new { version = AppVersion() }; }
        }

        // Single source of truth for the displayed version = the exe's AssemblyFileVersion (e.g. "1.1.2").
        private static string AppVersion()
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
                return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}";
            }
            catch { return "1.1"; }
        }

        private object MapAccount(Account a)
        {
            string st = "offline";
            string game = "";
            string job = "";
            var p = a.Presence;
            if (p != null)
            {
                switch (p.userPresenceType)
                {
                    case UserPresenceType.Online: st = "web"; break;
                    case UserPresenceType.InGame: st = "ingame"; game = p.lastLocation ?? ""; job = p.gameId ?? ""; break;
                    case UserPresenceType.InStudio: st = "studio"; break;
                    default: st = "offline"; break;
                }
            }

            int days;
            try { days = (int)Math.Max(0, (DateTime.Now - a.LastUse).TotalDays); } catch { days = 0; }

            // Active signals (client / nexus / uptime / disconnected) — best-effort from what the app knows locally.
            object act = null;
            double cpu = 0; long ram = 0; bool minimized = false;
            bool hasProc = lastStats.TryGetValue(a.Username ?? "", out var stat);
            if (hasProc) { cpu = stat.Cpu; ram = stat.RamMB; minimized = stat.Minimized; }
            try
            {
                var ca = AccountControl.Instance?.Accounts?.FirstOrDefault(c => c.Username == a.Username);
                bool nexus = ca != null && ca.Status == AccountStatus.Online;
                bool inGame = p != null && p.userPresenceType == UserPresenceType.InGame;
                bool client = nexus || inGame || hasProc;
                if (nexus && !string.IsNullOrEmpty(ca.InGameJobId)) job = ca.InGameJobId;
                // Show in the Active view whenever the account is Nexus-tracked, in-game, OR has a live Roblox
                // process (mapped by tracker) — so the resource monitor sees every running client, not just Nexus ones.
                if (ca != null || inGame || hasProc) act = new object[] { client ? 1 : 0, nexus ? 1 : 0, 0, 0 };
            }
            catch { }

            int gHue;
            try { gHue = string.IsNullOrEmpty(game) ? 220 : (Math.Abs(game.GetHashCode()) % 360); } catch { gHue = 220; }

            return new
            {
                u = a.Username ?? "",
                alias = a.Alias ?? "",
                grp = string.IsNullOrEmpty(a.Group) ? "Default" : a.Group,
                st,
                gi = -1,
                game,
                gHue,
                days,
                valid = a.Valid,
                rbx = 0,
                prem = false,
                act,
                cpu,
                ram,
                min = minimized,
                uid = a.UserID,
                job,
                notes = a.Description ?? ""
            };
        }

        // ---------------- JS -> C# ----------------

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject m;
            try { m = JObject.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }

            string type = (string)m["type"];
            if (string.IsNullOrEmpty(type)) return;

            try
            {
                switch (type)
                {
                    case "ready": ready = true; PushAccounts("init"); break;
                    case "launch": HandleLaunch(m); break;
                    case "remove": HandleRemove(m); break;
                    case "setAlias": SetField(m, a => a.Alias = (string)m["value"]); break;
                    case "setNotes": SetField(m, a => a.Description = (string)m["value"]); break;
                    case "setGroup": SetField(m, a => a.Group = string.IsNullOrEmpty((string)m["value"]) ? "Default" : (string)m["value"]); break;
                    case "saveTarget": HandleSaveTarget(m); break;
                    case "copy": HandleCopy(m); break;
                    case "ctx": HandleCtx(m); break;
                    case "openBrowser": HandleOpenBrowser(m); break;
                    case "utilities": HandleUtilities(m); break;
                    case "relaunch": RelaunchOne((string)m["user"]); break;
                    case "relaunchDisconnected": RelaunchDisconnected(); break;
                    case "closeClient": CloseClient((string)m["user"]); break;
                    case "closeAll": CloseAllClients(); break;
                    case "minimize": case "minimizeAll": Program.Logger.Info($"[ModernUI] {type} (not yet wired)"); break;
                    case "add": HandleAdd(m); break;
                    case "setSetting": HandleSetSetting(m); break;
                    case "savePlace": SaveGlobalTarget((string)m["placeId"], (string)m["jobId"], true); break;
                    case "switchToClassic": SwitchToClassic(); break;
                    // v2 resource manager + server browser
                    case "optimizeAll": { int n = ResourceManager.OptimizeAll(); Toast(n > 0 ? $"Optimized {n} client" + (n == 1 ? "" : "s") : "No running clients"); break; }
                    case "trimAll": { int n = ResourceManager.TrimAll(); Toast(n > 0 ? $"Trimmed RAM on {n} client" + (n == 1 ? "" : "s") : "No running clients"); PollStats(); break; }
                    case "trimInstance": { var a = Find((string)m["user"]); Toast(ResourceManager.TrimOne(a) ? "Trimmed RAM" : "That client isn't running"); PollStats(); break; }
                    case "fetchServers": HandleFetchServers(m); break;
                    case "fetchGames": HandleFetchGames(); break;
                    case "fetchFavorites": HandleFetchFavorites(); break;
                    case "joinJob": HandleJoinJob(m); break;
                    default: Program.Logger.Info($"[ModernUI] unhandled: {m}"); break;
                }
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] action '{type}': {ex}"); }
        }

        private Account Find(string user) => AccountManager.AccountsList?.FirstOrDefault(a => a.Username == user);

        private void SetField(JObject m, Action<Account> apply)
        {
            var a = Find((string)m["user"]);
            if (a == null) return;
            apply(a);
            SafeSave();
        }

        private void SafeSave() { try { AccountManager.SaveAccounts(true, true); } catch (Exception ex) { Program.Logger.Error($"[ModernUI] save: {ex}"); } }

        private List<Account> Users(JObject m)
        {
            var list = new List<Account>();
            if (m["users"] is JArray arr)
                foreach (var t in arr) { var a = Find((string)t); if (a != null) list.Add(a); }
            if (list.Count == 0 && m["user"] != null) { var a = Find((string)m["user"]); if (a != null) list.Add(a); }
            return list;
        }

        private async void HandleLaunch(JObject m)
        {
            long placeId = 0;
            long.TryParse((string)m["placeId"], out placeId);
            string jobId = (string)m["jobId"] ?? "";
            if (placeId <= 0) { Toast("Enter a valid Place ID"); return; }

            // Remember the Place ID/Job across restarts, same as the classic UI does on launch.
            SaveGlobalTarget(placeId.ToString(), jobId, false);

            var accounts = Users(m);
            if (accounts.Count == 0) { Toast("Select at least one account first"); return; }

            // Most common "nothing happens": a Roblox client is already open and Multi-Roblox is off, so the
            // new instance silently no-ops on Roblox's single-instance lock. Warn instead of leaving them guessing.
            try
            {
                bool multi = AccountManager.General != null && AccountManager.General.Get<bool>("EnableMultiRbx");
                if (!multi && Process.GetProcessesByName("RobloxPlayerBeta").Length > 0)
                    Toast("A Roblox client is already open — enable Multi-Roblox in Settings to launch more than one at once.");
            }
            catch { }

            double delay = 8;
            try { double.TryParse(AccountManager.General?.Get("AccountJoinDelay"), out delay); } catch { }
            if (delay < 1) delay = 1;

            foreach (var a in accounts)
            {
                try
                {
                    string res = await a.JoinServer(placeId, jobId);
                    // JoinServer returns "Success" or an "ERROR: ..." string — surface failures instead of swallowing them.
                    if (!string.IsNullOrEmpty(res) && res != "Success")
                        Toast(res.StartsWith("ERROR:") ? res.Substring(6).Trim() : res);
                }
                catch (Exception ex) { Program.Logger.Error($"[ModernUI] launch {a.Username}: {ex}"); Toast($"Launch failed: {ex.Message}"); }
                if (accounts.Count > 1) await Task.Delay((int)(delay * 1000));
            }
        }

        private void RelaunchOne(string user)
        {
            var a = Find(user);
            if (a == null) return;
            long placeId = 0;
            try { long.TryParse(a.GetField("SavedPlaceId"), out placeId); } catch { }
            if (placeId <= 0) { Program.Logger.Info($"[ModernUI] relaunch {user}: no saved place"); return; }
            string job = ""; try { job = a.GetField("SavedJobId") ?? ""; } catch { }
            _ = a.JoinServer(placeId, job);
        }

        // Right-click context-menu actions (the JS sends {type:"ctx",action,user}). These were previously unhandled,
        // so "Remove Account" / "Quick Log In" from the right-click menu did nothing.
        private void HandleCtx(JObject m)
        {
            string action = (string)m["action"];
            var a = Find((string)m["user"]);
            if (a == null) return;
            switch (action)
            {
                case "remove":
                    AccountManager.AccountsList.Remove(a);
                    SafeSave();
                    PushAccounts("accounts");
                    Toast($"Removed {a.Username}");
                    break;
                case "quicklogin":
                    try { AccountManager.Instance.ModernOpenBrowser(a); } catch (Exception ex) { Program.Logger.Error($"[ModernUI] quicklogin: {ex}"); }
                    break;
                default: Program.Logger.Info($"[ModernUI] ctx: unhandled action {action}"); break;
            }
        }

        private void HandleRemove(JObject m)
        {
            var accounts = Users(m);
            if (accounts.Count == 0) return;
            foreach (var a in accounts) AccountManager.AccountsList.Remove(a);
            SafeSave();
            PushAccounts("accounts");
        }

        private void HandleSaveTarget(JObject m)
        {
            long placeId = 0; long.TryParse((string)m["placeId"], out placeId);
            string jobId = (string)m["jobId"] ?? "";
            if (placeId <= 0) { Toast("Enter a valid Place ID first"); return; }

            var accounts = Users(m);
            if (accounts.Count == 0)
            {
                // Nothing checkbox-selected: save it as the global default target (remembered next launch) instead of silently doing nothing.
                SaveGlobalTarget(placeId.ToString(), jobId, true);
                return;
            }

            foreach (var a in accounts)
                try { a.SetField("SavedPlaceId", placeId.ToString()); a.SetField("SavedJobId", jobId); } catch { }
            SafeSave();
            Toast($"Saved target to {accounts.Count} account" + (accounts.Count == 1 ? "" : "s"));
        }

        // Persist the Place ID / Job as the app-wide default (mirrors the classic UI's [General] SavedPlaceId).
        private void SaveGlobalTarget(string placeId, string jobId, bool toast)
        {
            try
            {
                AccountManager.General.Set("SavedPlaceId", placeId ?? "");
                AccountManager.General.Set("SavedJobId", jobId ?? "");
                AccountManager.Instance.SaveSettings();
                if (toast) Toast("Saved as default Place ID");
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] SaveGlobalTarget: {ex}"); }
        }

        // Small transient message shown by the UI (host -> JS toast).
        private void Toast(string text) { try { Post(new { type = "toast", text }); } catch { } }

        // Persist a modern-UI setting to the right RAMSettings.ini section (keys mirror the classic Settings form).
        private void HandleSetSetting(JObject m)
        {
            string key = (string)m["key"];
            if (string.IsNullOrEmpty(key)) return;
            JToken vt = m["value"];
            string val = vt == null ? "" : (vt.Type == JTokenType.Boolean ? ((bool)vt ? "true" : "false") : vt.ToString());
            try
            {
                switch (key)
                {
                    case "checkUpdates": AccountManager.General.Set("CheckForUpdates", val); break;
                    case "savePw": AccountManager.General.Set("SavePasswords", val); break;
                    case "disableAging": AccountManager.General.Set("DisableAgingAlert", val); break;
                    case "async": AccountManager.General.Set("AsyncJoin", val); break;
                    case "shuffleLowest": AccountManager.General.Set("ShuffleChoosesLowestServer", val); break;
                    case "fpsUnlock": AccountManager.General.Set("UnlockFPS", val); break;
                    case "multiRoblox":
                        AccountManager.General.Set("EnableMultiRbx", val);
                        try { if (!AccountManager.Instance.UpdateMultiRoblox()) Toast("Multi-Roblox saved — close open Roblox windows for it to take effect."); } catch { }
                        break;
                    case "webApi": AccountManager.Developer.Set("EnableWebServer", val); break;
                    case "autoRelaunch": AccountManager.AccountControl.Set("StartOnLaunch", val); break;
                    case "autoCookie":
                        AccountManager.General.Set("AutoCookieRefresh", val);
                        try { if (AccountManager.Instance.AutoCookieRefresh != null) AccountManager.Instance.AutoCookieRefresh.Enabled = (val == "true"); } catch { }
                        break;
                    case "launchDelay": AccountManager.General.Set("AccountJoinDelay", val); break;
                    case "maxRecent": AccountManager.General.Set("MaxRecentGames", val); break;
                    case "maxFps": AccountManager.General.Set("MaxFPSValue", val); break;
                    case "relaunchDelay": AccountManager.AccountControl.Set("RelaunchDelay", val); break;
                    case "connPort": AccountManager.AccountControl.Set("NexusPort", val); break;
                    case "accent": AccountManager.General.Set("ModernAccent", val); break;
                    case "density": AccountManager.General.Set("ModernDensity", val); break;
                    case "theme": AccountManager.General.Set("ModernTheme", val); break;
                    // v2 resource-manager settings (consumed by Classes/ResourceManager.cs + ClientSettingsPatcher).
                    case "perfManage": AccountManager.General.Set("PerfManage", val); break;
                    case "dynamicPriority": AccountManager.General.Set("PerfDynamicPriority", val); break;
                    case "bgPriority": AccountManager.General.Set("PerfBackgroundPriority", val); break;
                    case "affinityMode": AccountManager.General.Set("PerfAffinityMode", val); break;
                    case "trimOnMin": AccountManager.General.Set("PerfTrimOnMinimize", val); break;
                    case "autoMin": AccountManager.General.Set("PerfAutoMinimizeAlts", val); break;
                    case "lowGfx": AccountManager.General.Set("PerfLowGraphics", val); break;
                    default: Program.Logger.Info($"[ModernUI] setSetting: unknown key {key}"); return;
                }
                AccountManager.Instance.SaveSettings();
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] setSetting {key}: {ex}"); }
        }

        // Manually relaunch every Nexus-tracked account that is currently disconnected and has a saved target.
        private void RelaunchDisconnected()
        {
            try
            {
                int n = 0;
                foreach (var a in AccountManager.AccountsList ?? new List<Account>())
                {
                    var ca = AccountControl.Instance?.Accounts?.FirstOrDefault(c => c.Username == a.Username);
                    if (ca == null || ca.Status == AccountStatus.Online) continue;
                    long placeId = 0; long.TryParse(a.GetField("SavedPlaceId"), out placeId);
                    if (placeId <= 0) { long.TryParse(AccountManager.General?.Get("SavedPlaceId"), out placeId); }
                    if (placeId <= 0) continue;
                    string job = a.GetField("SavedJobId") ?? "";
                    _ = a.JoinServer(placeId, job); n++;
                }
                Toast(n > 0 ? $"Relaunching {n} disconnected" : "No disconnected clients to relaunch");
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] relaunchDisconnected: {ex}"); }
        }

        private void HandleCopy(JObject m)
        {
            var a = Find((string)m["user"]);
            if (a == null) return;
            // The UI sends display labels ("Username", "User : Pass combo", "Profile URL", "User ID"); match loosely so they don't silently fail.
            string what = ((string)m["what"] ?? "").ToLowerInvariant();
            string val = null;
            if (what.Contains("combo") || what.Contains(":")) val = $"{a.Username}:{a.Password}";
            else if (what.Contains("user") && what.Contains("id")) val = a.UserID.ToString();
            else if (what.Contains("profile")) val = $"https://www.roblox.com/users/{a.UserID}/profile";
            else if (what.Contains("pass")) val = a.Password;
            else if (what.Contains("user")) val = a.Username;
            if (!string.IsNullOrEmpty(val)) { try { Clipboard.SetText(val); Toast("Copied to clipboard"); } catch { } }
        }

        private void HandleOpenBrowser(JObject m)
        {
            foreach (var a in Users(m)) AccountManager.Instance.ModernOpenBrowser(a);
        }

        // Switch back to the classic UI (safety escape) — persists the choice and doesn't exit the app.
        public void SwitchToClassic()
        {
            try { AccountManager.General.Set("UseModernUI", "false"); AccountManager.Instance.SaveSettings(); } catch { }
            switchingToClassic = true;
            Launched = false; // allow re-opening the modern UI later (e.g. from the classic Settings toggle)
            try { pushTimer?.Stop(); } catch { }
            try { owner?.Show(); owner?.BringToFront(); } catch { }
            Close();
        }

        private void HandleUtilities(JObject m)
        {
            var a = Find((string)m["user"]);
            if (a == null) return;
            try
            {
                AccountManager.SelectedAccount = a;
                var f = new AccountUtils();
                f.Show(this);
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] utilities: {ex}"); }
        }

        private void CloseClient(string user)
        {
            var a = Find(user);
            if (a == null || string.IsNullOrEmpty(a.BrowserTrackerID)) return;
            try
            {
                foreach (var proc in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(proc.GetCommandLine(), @"-b (\d+)");
                        if (match.Success && match.Groups[1].Value == a.BrowserTrackerID) { proc.Kill(); }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] closeClient: {ex}"); }
        }

        private void CloseAllClients()
        {
            try { foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta")) { try { p.Kill(); } catch { } } }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] closeAll: {ex}"); }
        }

        private async void HandleAdd(JObject m)
        {
            string mode = (string)m["mode"] ?? "manual";
            try
            {
                switch (mode)
                {
                    case "cookie": new ImportForm().Show(this); break;
                    case "manual": await AccountManager.Instance.ModernAddAccount(); PushAccounts("accounts"); break;
                    case "userpass": AccountManager.Instance.ModernBulkUserPass(); break;
                    case "custom": AccountManager.Instance.ModernAddCustom(); break;
                    default:
                        MessageBox.Show(this, "That add method isn't in the new UI yet — use Manual Login or Cookie import.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                }
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] add {mode}: {ex}"); }
        }

        // ---------------- Server browser (real data) ----------------

        private long ResolvePlaceId(JObject m)
        {
            long placeId = 0;
            long.TryParse((string)m["placeId"], out placeId);
            if (placeId <= 0) { try { long.TryParse(AccountManager.General?.Get("SavedPlaceId"), out placeId); } catch { } }
            return placeId;
        }

        // Public server list for a place — unauthenticated GET games.roblox.com/v1/games/{id}/servers/Public.
        private async void HandleFetchServers(JObject m)
        {
            long placeId = ResolvePlaceId(m);
            if (placeId <= 0) { Toast("Enter a Place ID first"); Post(new { type = "servers", data = new object[0], placeId = 0 }); return; }
            try
            {
                var resp = await GamesClient.ExecuteAsync(new RestRequest($"v1/games/{placeId}/servers/Public?sortOrder=Asc&limit=100"));
                var data = new List<object>();
                if (resp.IsSuccessful && !string.IsNullOrEmpty(resp.Content) && JObject.Parse(resp.Content)["data"] is JArray arr)
                    foreach (var s in arr)
                        data.Add(new
                        {
                            id = (string)s["id"],
                            playing = (int?)s["playing"] ?? 0,
                            max = (int?)s["maxPlayers"] ?? 0,
                            fps = (int)Math.Round((double?)s["fps"] ?? 0),
                            ping = (int?)s["ping"] ?? 0,
                        });
                Post(new { type = "servers", data, placeId });
                if (data.Count == 0) Toast("No public servers found for that place");
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] fetchServers: {ex}"); Post(new { type = "servers", data = new object[0], placeId }); Toast("Couldn't load servers"); }
        }

        // Popular games — unauthenticated GET games.roblox.com/v1/games/list.
        private async void HandleFetchGames()
        {
            try
            {
                var resp = await GamesClient.ExecuteAsync(new RestRequest("v1/games/list?model.startRows=0&model.maxRows=30"));
                var data = new List<object>();
                if (resp.IsSuccessful && !string.IsNullOrEmpty(resp.Content) && JObject.Parse(resp.Content)["games"] is JArray arr)
                    foreach (var gme in arr)
                        data.Add(new { name = (string)gme["name"], placeId = (long?)gme["placeId"] ?? 0, playing = (int?)gme["playerCount"] ?? 0 });
                Post(new { type = "games", data });
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] fetchGames: {ex}"); Post(new { type = "games", data = new object[0] }); }
        }

        // Favorites — the classic FavoriteGames.json in the working dir (may not exist).
        private void HandleFetchFavorites()
        {
            var data = new List<object>();
            try
            {
                string fn = Path.Combine(Environment.CurrentDirectory, "FavoriteGames.json");
                if (File.Exists(fn) && JsonConvert.DeserializeObject<JArray>(File.ReadAllText(fn)) is JArray arr)
                    foreach (var f in arr)
                        data.Add(new { name = (string)(f["Name"] ?? f["name"]), placeId = (long?)(f["PlaceID"] ?? f["placeId"]) ?? 0, priv = (string)(f["PrivateServer"] ?? "") });
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] fetchFavorites: {ex}"); }
            Post(new { type = "favorites", data });
        }

        // Join a specific server/game. Uses the named account, else the first checkbox-selected/valid account.
        private async void HandleJoinJob(JObject m)
        {
            long placeId = ResolvePlaceId(m);
            if (placeId <= 0) { Toast("No Place ID to join"); return; }
            string jobId = (string)m["jobId"] ?? "";

            var a = Find((string)m["user"])
                    ?? AccountManager.AccountsList?.FirstOrDefault(x => x.Valid)
                    ?? AccountManager.AccountsList?.FirstOrDefault();
            if (a == null) { Toast("Add an account first"); return; }

            try
            {
                string res = await a.JoinServer(placeId, jobId);
                if (!string.IsNullOrEmpty(res) && res != "Success")
                    Toast(res.StartsWith("ERROR:") ? res.Substring(6).Trim() : res);
                else
                    Toast($"Joining as {a.Username}");
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] joinJob: {ex}"); Toast($"Join failed: {ex.Message}"); }
        }
    }
}
