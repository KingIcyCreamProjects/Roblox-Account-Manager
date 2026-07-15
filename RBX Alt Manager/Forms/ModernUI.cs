using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Nexus;
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
        private bool ready;
        private bool switchingToClassic;

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

        private object MapSettings()
        {
            try
            {
                return new
                {
                    autoRelaunch = AccountManager.AccountControl != null && AccountManager.AccountControl.Get<bool>("StartOnLaunch"),
                };
            }
            catch { return new { }; }
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
            try
            {
                var ca = AccountControl.Instance?.Accounts?.FirstOrDefault(c => c.Username == a.Username);
                bool nexus = ca != null && ca.Status == AccountStatus.Online;
                bool inGame = p != null && p.userPresenceType == UserPresenceType.InGame;
                bool client = nexus || inGame;
                if (nexus && !string.IsNullOrEmpty(ca.InGameJobId)) job = ca.InGameJobId;
                if (client) act = new object[] { client ? 1 : 0, nexus ? 1 : 0, 0, (!nexus && inGame) ? 0 : 0 };
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
                    case "openBrowser": HandleOpenBrowser(m); break;
                    case "utilities": HandleUtilities(m); break;
                    case "relaunch": RelaunchOne((string)m["user"]); break;
                    case "relaunchDisconnected": Program.Logger.Info("[ModernUI] relaunchDisconnected (handled by Auto-Relaunch engine)"); break;
                    case "closeClient": CloseClient((string)m["user"]); break;
                    case "closeAll": CloseAllClients(); break;
                    case "minimize": case "minimizeAll": Program.Logger.Info($"[ModernUI] {type} (not yet wired)"); break;
                    case "add": HandleAdd(m); break;
                    case "switchToClassic": SwitchToClassic(); break;
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
            if (placeId <= 0) { Program.Logger.Warn("[ModernUI] launch: invalid Place ID"); return; }

            var accounts = Users(m);
            if (accounts.Count == 0) return;

            double delay = 8;
            try { double.TryParse(AccountManager.General?.Get("AccountJoinDelay"), out delay); } catch { }
            if (delay < 1) delay = 1;

            foreach (var a in accounts)
            {
                try { await a.JoinServer(placeId, jobId); }
                catch (Exception ex) { Program.Logger.Error($"[ModernUI] launch {a.Username}: {ex}"); }
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
            foreach (var a in Users(m))
            {
                try { a.SetField("SavedPlaceId", placeId.ToString()); a.SetField("SavedJobId", jobId); } catch { }
            }
            SafeSave();
        }

        private void HandleCopy(JObject m)
        {
            var a = Find((string)m["user"]);
            if (a == null) return;
            string what = (string)m["what"] ?? "";
            string val = null;
            switch (what)
            {
                case "username": val = a.Username; break;
                case "password": val = a.Password; break;
                case "combo": val = $"{a.Username}:{a.Password}"; break;
                case "profile": val = $"https://www.roblox.com/users/{a.UserID}/profile"; break;
                case "userid": case "userId": val = a.UserID.ToString(); break;
            }
            if (!string.IsNullOrEmpty(val)) { try { Clipboard.SetText(val); } catch { } }
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
                    default:
                        MessageBox.Show(this, "That add method isn't in the new UI yet — use Manual Login or Cookie import.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                }
            }
            catch (Exception ex) { Program.Logger.Error($"[ModernUI] add {mode}: {ex}"); }
        }
    }
}
