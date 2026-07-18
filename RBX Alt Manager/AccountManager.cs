using BrightIdeasSoftware;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using RBX_Alt_Manager.Properties;
using RestSharp;
using Sodium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;

#pragma warning disable CS0618 // parameter warnings

namespace RBX_Alt_Manager
{
    public partial class AccountManager : Form
    {
        public static AccountManager Instance;
        public static List<Account> AccountsList;
        public static List<Account> SelectedAccounts;
        public static List<Game> RecentGames;
        public static Account SelectedAccount;
        public static Account LastValidAccount; // this is used for the Batch class since getting place details requires authorization, auto updates whenever an account is used
        public static RestClient MainClient;
        public static RestClient AvatarClient;
        public static RestClient FriendsClient;
        public static RestClient UsersClient;
        public static RestClient AuthClient;
        public static RestClient EconClient;
        public static RestClient AccountClient;
        public static RestClient GameJoinClient;
        public static RestClient Web13Client;
        public static string CurrentPlaceId { get => Instance.PlaceID.Text; }
        public static string CurrentJobId { get => Instance.JobID.Text; }
        private ArgumentsForm afform;
        private ServerList ServerListForm;
        private AccountUtils UtilsForm;
        private ImportForm ImportAccountsForm;
        private AccountFields FieldsForm;
        private ThemeEditor ThemeForm;
        private AccountControl ControlForm;
        private SettingsForm SettingsForm;
        private RecentGamesForm RGForm;
        private readonly static DateTime startTime = DateTime.Now;
        public static bool IsTeleport = false;
        public static bool UseOldJoin = false;
        public static bool ShuffleJobID = false;
        private static bool PuppeteerSupported;
        public static string CurrentVersion;
        public OLVListItem SelectedAccountItem { get; private set; }
        private WebServer AltManagerWS;
        private string WSPassword { get; set; }
        public System.Timers.Timer AutoCookieRefresh { get; private set; }

        public static IniFile IniSettings;
        public static IniSection General;
        public static IniSection Developer;
        public static IniSection WebServer;
        public static IniSection AccountControl;
        public static IniSection Watcher;
        public static IniSection Prompts;

        private static Mutex rbxMultiMutex;
        private readonly static object saveLock = new object();
        private readonly static object rgSaveLock = new object();
        // Guards every STRUCTURAL mutation of AccountsList (Add/Remove/Insert/reassign) against the
        // snapshot taken in SaveAccounts. Without it, a background serialize can enumerate the list while
        // the UI thread adds/removes → "Collection was modified" and the save is aborted (lost cookie writes).
        private readonly static object accountsLock = new object();
        // Coalesces the high-frequency save paths (alias/description keystrokes, 4×/tick window positions)
        // into one write ~1s after the last change, killing SSD write-amplification and UI-thread jank.
        private static System.Threading.Timer SaveDebounceTimer;
        private readonly static object debounceLock = new object();
        public event EventHandler<GameArgs> RecentGameAdded;

        private bool IsResettingPassword;
        private bool IsDownloadingChromium;
        private bool LaunchNext;
        private CancellationTokenSource LauncherToken;

        // OLD hardcoded PUBLIC DPAPI entropy ("ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BUY ic3w0lf").
        // It is NOT a secret — anyone with the binary can reproduce it. Kept ONLY so stores written by older
        // builds still decrypt on first load; every new save uses the per-install random Entropy (see below),
        // so an old store silently re-protects itself with the real key the next time it's saved.
        private static readonly byte[] LegacyEntropy = new byte[] { 0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66 };

        [DllImport("DwmApi")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);

        public static void SetDarkBar(IntPtr Handle)
        {
            if (ThemeEditor.UseDarkTopBar && DwmSetWindowAttribute(Handle, 19, new[] { 1 }, 4) != 0)
                DwmSetWindowAttribute(Handle, 20, new[] { 1 }, 4);
        }

        public AccountManager()
        {
            Instance = this;

            ThemeEditor.LoadTheme();

            SetDarkBar(Handle);

            IniSettings = File.Exists(Path.Combine(Environment.CurrentDirectory, "RAMSettings.ini")) ? new IniFile("RAMSettings.ini") : new IniFile();

            General = IniSettings.Section("General");
            Developer = IniSettings.Section("Developer");
            WebServer = IniSettings.Section("WebServer");
            AccountControl = IniSettings.Section("AccountControl");
            Watcher = IniSettings.Section("Watcher");
            Prompts = IniSettings.Section("Prompts");

            if (!General.Exists("CheckForUpdates")) General.Set("CheckForUpdates", "true");
            if (!General.Exists("AccountJoinDelay")) General.Set("AccountJoinDelay", "8");
            if (!General.Exists("AsyncJoin")) General.Set("AsyncJoin", "false");
            if (!General.Exists("DisableAgingAlert")) General.Set("DisableAgingAlert", "false");
            if (!General.Exists("SavePasswords")) General.Set("SavePasswords", "true");
            if (!General.Exists("ServerRegionFormat")) General.Set("ServerRegionFormat", "<city>, <countryCode>", "Visit http://ip-api.com/json/1.1.1.1 to see available format options");
            if (!General.Exists("MaxRecentGames")) General.Set("MaxRecentGames", "8");
            if (!General.Exists("ShuffleChoosesLowestServer")) General.Set("ShuffleChoosesLowestServer", "false");
            if (!General.Exists("ShufflePageCount")) General.Set("ShufflePageCount", "5");
            if (!General.Exists("IPApiLink")) General.Set("IPApiLink", "http://ip-api.com/json/<ip>");
            if (!General.Exists("WindowScale"))
            {
                General.Set("WindowScale", Screen.PrimaryScreen.Bounds.Height <= Screen.PrimaryScreen.Bounds.Width /*scuffed*/ ? Math.Max(Math.Min(Screen.PrimaryScreen.Bounds.Height / 1080f, 2f), 1f).ToString(".0#", CultureInfo.InvariantCulture) : "1.0");

                if (Program.Scale > 1)
                    if (!Utilities.YesNoPrompt("KingsRAM", "RAM has detected you have a monitor larger than average", $"Would you like to keep the WindowScale setting of {Program.Scale:F2}?", false))
                        General.Set("WindowScale", "1.0");
                    else
                        MessageBox.Show("In case the font scaling is incorrect, open RAMSettings.ini and change \"ScaleFonts=true\" to \"ScaleFonts=false\" without the quotes.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (!General.Exists("ScaleFonts")) General.Set("ScaleFonts", "true");
            if (!General.Exists("AutoCookieRefresh")) General.Set("AutoCookieRefresh", "true");
            if (!General.Exists("AutoCloseLastProcess")) General.Set("AutoCloseLastProcess", "true");
            if (!General.Exists("ShowPresence")) General.Set("ShowPresence", "true");
            if (!General.Exists("PresenceUpdateRate")) General.Set("PresenceUpdateRate", "5");
            if (!General.Exists("UnlockFPS")) General.Set("UnlockFPS", "false");
            if (!General.Exists("MaxFPSValue")) General.Set("MaxFPSValue", "120");
            if (!General.Exists("UseCefSharpBrowser")) General.Set("UseCefSharpBrowser", "false");

            if (!Developer.Exists("DevMode")) Developer.Set("DevMode", "false");
            if (!Developer.Exists("EnableWebServer")) Developer.Set("EnableWebServer", "false");

            if (!WebServer.Exists("WebServerPort")) WebServer.Set("WebServerPort", "7963");
            if (!WebServer.Exists("AllowGetCookie")) WebServer.Set("AllowGetCookie", "false");
            if (!WebServer.Exists("AllowGetAccounts")) WebServer.Set("AllowGetAccounts", "false");
            if (!WebServer.Exists("AllowLaunchAccount")) WebServer.Set("AllowLaunchAccount", "false");
            if (!WebServer.Exists("AllowAccountEditing")) WebServer.Set("AllowAccountEditing", "false");
            if (!WebServer.Exists("Password")) WebServer.Set("Password", "");
            WSPassword = WebServer.Get("Password") ?? "";
            if (!WebServer.Exists("EveryRequestRequiresPassword")) WebServer.Set("EveryRequestRequiresPassword", "false");
            if (!WebServer.Exists("AllowExternalConnections")) WebServer.Set("AllowExternalConnections", "false");

            if (!AccountControl.Exists("AllowExternalConnections")) AccountControl.Set("AllowExternalConnections", "false");
            if (!AccountControl.Exists("RelaunchDelay")) AccountControl.Set("RelaunchDelay", "60");
            if (!AccountControl.Exists("LauncherDelayNumber")) AccountControl.Set("LauncherDelayNumber", "9");
            if (!AccountControl.Exists("NexusPort")) AccountControl.Set("NexusPort", "5242");

            if (!General.Exists("UseModernUI")) General.Set("UseModernUI", "true"); // WebView2 redesign; classic UI is the fallback
            if (!General.Exists("DiscordLink")) General.Set("DiscordLink", "https://discord.com/channels/1526775420966670476"); // KingsRAM Discord (paste a discord.gg invite here)

            InitializeComponent();
            this.Rescale();

            Shown += (s, e) => MaybeLaunchModern();
            // Catch-all: whenever the password/encryption overlay closes (first-run setup, unlock, etc.),
            // the main UI is ready — open the modern UI. Idempotent (guarded by the Launched flag).
            PasswordPanel.VisibleChanged += (s, e) => { if (!PasswordPanel.Visible) MaybeLaunchModern(); };

            DonateButton.Visible = false; // was ic3w0lf22's donate page; no fork donate page

            // Visible "switch to the new UI" button in the top strip (so it's not buried in Settings).
            try
            {
                var NewUIButton = new Button
                {
                    Text = "✦ New UI",
                    Size = new System.Drawing.Size(74, 22),
                    Location = new System.Drawing.Point(610, 5),
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    BackColor = System.Drawing.Color.FromArgb(18, 28, 48),
                    ForeColor = System.Drawing.Color.FromArgb(53, 200, 245),
                    Font = new System.Drawing.Font("Segoe UI", 8.25f, System.Drawing.FontStyle.Bold),
                    TabStop = false,
                    Cursor = Cursors.Hand
                };
                NewUIButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(53, 200, 245);
                NewUIButton.Click += (s, e) =>
                {
                    General.Set("UseModernUI", "true");
                    SaveSettings();
                    RBX_Alt_Manager.Forms.ModernUI.Launch(this);
                };
                Controls.Add(NewUIButton);
                NewUIButton.BringToFront();
            }
            catch (Exception ex) { Program.Logger.Error($"NewUIButton: {ex}"); }

            AccountsList = new List<Account>();
            SelectedAccounts = new List<Account>();

            AccountsView.SetObjects(AccountsList);

            if (ThemeEditor.UseDarkTopBar) Icon = Properties.Resources.team_KX4_icon_white; // this has to go after or icon wont actually change

            // Derive the unfocused-selection highlight from the active theme instead of a fixed blue, so it stays
            // visible and on-theme on dark and light themes alike (theme colors are already loaded by here).
            AccountsView.UnfocusedHighlightBackgroundColor = ThemeEditor.AccountBackground.DarkenOrBrighten(0.35f);
            AccountsView.UnfocusedHighlightForegroundColor = ThemeEditor.AccountForeground;

            SimpleDropSink sink = AccountsView.DropSink as SimpleDropSink;
            sink.CanDropBetween = true;
            sink.CanDropOnItem = true;
            sink.CanDropOnBackground = false;
            sink.CanDropOnSubItem = false;
            sink.CanDrop += Sink_CanDrop;
            sink.Dropped += Sink_Dropped;
            sink.FeedbackColor = Color.FromArgb(33, 33, 33);

            AccountsView.AlwaysGroupByColumn = Group;

            Group.GroupKeyGetter = delegate (object account)
            {
                return ((Account)account).Group;
            };

            Group.GroupKeyToTitleConverter = delegate (object Key)
            {
                string GroupName = Key as string;
                Match match = Regex.Match(GroupName, @"\d{1,3}\s?");

                if (match.Success)
                    return GroupName.Substring(match.Length);
                else
                    return GroupName;
            };

            var VCKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");

            if (!Prompts.Exists("VCPrompted") && (VCKey == null || (VCKey is RegistryKey && VCKey.GetValue("Bld") is int VCVersion && VCVersion < 32532)))
                Task.Run(async () => // Make sure the user has the latest 2015-2022 vcredist installed
                {
                    using HttpClient Client = new HttpClient();
                    byte[] bs = await Client.GetByteArrayAsync("https://aka.ms/vs/17/release/vc_redist.x86.exe");
                    string FN = Path.Combine(Path.GetTempPath(), "vcredist.tmp");

                    File.WriteAllBytes(FN, bs);

                    Process.Start(new ProcessStartInfo(FN) { UseShellExecute = false, Arguments = "/q /norestart" }).WaitForExit();

                    Prompts.Set("VCPrompted", "1");
                });
        }

        private void Sink_CanDrop(object sender, OlvDropEventArgs e)
        {
            if (e.DataObject.GetType() != typeof(OLVDataObject) && e.DragEventArgs.Data.GetDataPresent(DataFormats.Text))
                e.Effect = DragDropEffects.Copy;
        }

        private void Sink_Dropped(object sender, OlvDropEventArgs e)
        {
            if (e.Effect == DragDropEffects.Copy)
            {
                string Text = (string)e.DragEventArgs.Data.GetData(DataFormats.Text);
                Regex RSecRegex = new Regex(@"(_\|WARNING:-DO-NOT-SHARE-THIS\.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items\.\|\w+)");
                MatchCollection RSecMatches = RSecRegex.Matches(Text);

                foreach (Match match in RSecMatches)
                    AddAccount(match.Value);
            }
        }

        // S2: the account store lives in a locked per-user directory (%LOCALAPPDATA%\KingsRAM) instead of the
        // working directory, so full-takeover cookies can't fan out into OneDrive/Downloads/a network share.
        // Legacy CWD files are migrated in on first run. Falls back to CWD if LocalAppData is unavailable.
        private readonly static string DataDirectory = ResolveDataDirectory();

        // S1: per-install random DPAPI entropy, generated once and stored DPAPI-wrapped, replacing the public
        // constant so same-user code can no longer decrypt the store just by knowing a value baked into the binary.
        private static readonly byte[] Entropy = LoadOrCreateEntropy();

        private readonly static string SaveFilePath = Path.Combine(DataDirectory, "AccountData.json");
        private readonly static string RecentGamesFilePath = Path.Combine(DataDirectory, "RecentGames.json"); // i shouldve combined everything that isnt accountdata into one file but oh well im too lazy : |

        private static string ResolveDataDirectory()
        {
            try
            {
                string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KingsRAM");
                Directory.CreateDirectory(Dir); // LocalAppData is already ACL'd to the current user — that's the point

                // One-time migration: move the legacy CWD store (+ rolling backups + RecentGames) into the locked
                // dir. Move, not copy, so the sensitive file doesn't linger in the working directory. Non-fatal.
                string LegacyAccount = Path.Combine(Environment.CurrentDirectory, "AccountData.json");
                string TargetAccount = Path.Combine(Dir, "AccountData.json");

                if (!File.Exists(TargetAccount) && File.Exists(LegacyAccount))
                {
                    foreach (string Suffix in new[] { "", ".backup", ".bak", ".old" })
                    {
                        string Src = LegacyAccount + Suffix, Dst = TargetAccount + Suffix;
                        if (File.Exists(Src) && !File.Exists(Dst)) try { File.Move(Src, Dst); } catch (Exception ex) { Program.Logger.Warn($"Could not migrate {Src}: {ex.Message}"); }
                    }
                }

                string LegacyRG = Path.Combine(Environment.CurrentDirectory, "RecentGames.json");
                string TargetRG = Path.Combine(Dir, "RecentGames.json");
                if (File.Exists(LegacyRG) && !File.Exists(TargetRG)) try { File.Move(LegacyRG, TargetRG); } catch { }

                return Dir;
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Could not use %LOCALAPPDATA%\\KingsRAM, falling back to the working directory: {ex}");
                return Environment.CurrentDirectory;
            }
        }

        private static byte[] LoadOrCreateEntropy()
        {
            try
            {
                string Path_ = Path.Combine(DataDirectory, "entropy.bin");

                if (File.Exists(Path_))
                    return ProtectedData.Unprotect(File.ReadAllBytes(Path_), null, DataProtectionScope.CurrentUser);

                byte[] Key = new byte[32];
                using (var Rng = RandomNumberGenerator.Create()) Rng.GetBytes(Key);

                File.WriteAllBytes(Path_, ProtectedData.Protect(Key, null, DataProtectionScope.CurrentUser));

                return Key;
            }
            catch (Exception ex)
            {
                // If we can't persist a per-install key, fall back to the legacy entropy so the app still works
                // (no worse than before). Logged so it's diagnosable.
                Program.Logger.Error($"Could not create/load per-install entropy, using legacy entropy: {ex}");
                return LegacyEntropy;
            }
        }

        // Try to DPAPI-decrypt with the current per-install entropy first, then the legacy public entropy so a store
        // written by an older build still loads (it will re-protect with the new key on the next save).
        private static bool TryUnprotect(byte[] Data, out byte[] Plain)
        {
            foreach (byte[] Ent in new[] { Entropy, LegacyEntropy })
                try { Plain = ProtectedData.Unprotect(Data, Ent, DataProtectionScope.CurrentUser); return true; }
                catch (CryptographicException) { }

            Plain = null;
            return false;
        }

        private void RefreshView(object obj = null)
        {
            AccountsView.InvokeIfRequired(() =>
            {
                AccountsView.BuildList();
                if (AccountsView.ShowGroups) AccountsView.BuildGroups();

                if (obj != null)
                {
                    AccountsView.RefreshObject(obj);
                    AccountsView.EnsureModelVisible(obj);
                }
            });
        }

        private static ReadOnlyMemory<byte> PasswordHash; // Store the hash after the data is successfully decrypted so we can encrypt again.

        private void LoadAccounts(byte[] Hash = null)
        {
            bool EnteredPassword = false;
            byte[] Data = File.Exists(SaveFilePath) ? File.ReadAllBytes(SaveFilePath) : Array.Empty<byte>();

            if (Data.Length > 0)
            {
                // Guard the length before slicing: a truncated/partial store (Data.Length < header) would throw
                // ArgumentException from the ReadOnlySpan ctor and crash the load with no recovery message.
                bool HasHeader = Data.Length >= Cryptography.RAMHeader.Length &&
                    new ReadOnlySpan<byte>(Data, 0, Cryptography.RAMHeader.Length).SequenceEqual(Cryptography.RAMHeader);

                if (HasHeader)
                {
                    if (Hash == null)
                    {
                        EncryptionSelectionPanel.Visible = false;
                        PasswordSelectionPanel.Visible = false;
                        PasswordLayoutPanel.Visible = true;
                        PasswordPanel.Visible = true;
                        PasswordPanel.BringToFront();
                        PasswordTextBox.Focus();

                        return;
                    }

                    Data = Cryptography.Decrypt(Data, Hash);
                    AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Data));
                    PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

                    PasswordPanel.Visible = false;
                    EnteredPassword = true;
                }
                else if (TryUnprotect(Data, out byte[] Plain))
                    AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Plain));
                else
                {
                    // Neither the per-install nor the legacy DPAPI entropy could decrypt this. Only accept it as a
                    // raw (unencrypted) JSON store if the user explicitly opted out via the NoEncryption sentinel;
                    // otherwise treat it as corrupt/foreign and never silently trust it as plaintext.
                    bool NoEncryptionOptOut = File.Exists(Path.Combine(Environment.CurrentDirectory, "NoEncryption.IUnderstandTheRisks.iautamor"));

                    try
                    {
                        if (!NoEncryptionOptOut) throw new CryptographicException("Encrypted store failed to decrypt and no NoEncryption opt-out file is present.");

                        AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Data));
                    }
                    catch (Exception e)
                    {
                        File.WriteAllBytes(SaveFilePath + ".bak", Data);

                        MessageBox.Show($"Failed to load accounts!\nA backup file was created in case the data can be recovered.\n\n{e.Message}", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }

            AccountsList ??= new List<Account>();

            if (!EnteredPassword && AccountsList.Count == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
            {
                bool BackupHasHeader = BackupData.Length >= Cryptography.RAMHeader.Length &&
                    new ReadOnlySpan<byte>(BackupData, 0, Cryptography.RAMHeader.Length).SequenceEqual(Cryptography.RAMHeader);

                if (BackupHasHeader && MessageBox.Show("The existing backup file is password-locked, would you like to attempt to load it?", "KingsRAM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    if (File.Exists(SaveFilePath))
                    {
                        if (File.Exists($"{SaveFilePath}.old")) File.Delete($"{SaveFilePath}.old");

                        File.Move(SaveFilePath, $"{SaveFilePath}.old");
                    }

                    File.Move($"{SaveFilePath}.backup", SaveFilePath);

                    LoadAccounts();

                    return;
                }

                if (MessageBox.Show("No accounts were loaded but there is a backup file, would you like to load the backup file?", "KingsRAM", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
                {
                    try
                    {
                        if (!TryUnprotect(BackupData, out byte[] Decoded)) throw new CryptographicException("Backup failed to decrypt");

                        AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Decoded));
                    }
                    catch
                    {
                        try { AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(BackupData)); }
                        catch { MessageBox.Show("Failed to load backup file!", "KingsRAM", MessageBoxButtons.OKCancel, MessageBoxIcon.Error); }
                    }
                }
            }

            AccountsView.SetObjects(AccountsList);
            RefreshView();

            if (AccountsList.Count > 0)
            {
                LastValidAccount = AccountsList[0];

                foreach (Account account in AccountsList)
                    if (account.LastUse > LastValidAccount.LastUse)
                        LastValidAccount = account;
            }

            MaybeLaunchModern(); // accounts are loaded now (covers the post-unlock path; no-op if the form isn't shown yet)
        }

        // Launch the modern WebView2 UI once accounts are loaded (not while the password/encryption panel is up).
        // Gated by the UseModernUI setting; ModernUI.Launch falls back to this classic window if WebView2 fails.
        private void MaybeLaunchModern()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (PasswordPanel != null && PasswordPanel.Visible) return;
                if (General == null || !General.Get<bool>("UseModernUI")) return;
                RBX_Alt_Manager.Forms.ModernUI.Launch(this);
            }
            catch (Exception ex) { Program.Logger.Error($"MaybeLaunchModern: {ex}"); }
        }

        // Coalesced save for hot, non-critical paths (property setters, window-position SetField). Restarts a
        // ~1s timer on each call so a burst of edits collapses to a single write. Critical paths (add/remove/
        // import/cookie rotation/password change) call SaveAccounts directly so they persist immediately.
        public static void SaveAccountsDebounced()
        {
            lock (debounceLock)
            {
                if (SaveDebounceTimer == null)
                    SaveDebounceTimer = new System.Threading.Timer(_ => { try { SaveAccounts(); } catch (Exception ex) { Program.Logger.Error($"Debounced save failed: {ex}"); } }, null, 1000, System.Threading.Timeout.Infinite);
                else
                    SaveDebounceTimer.Change(1000, System.Threading.Timeout.Infinite);
            }
        }

        public static void SaveAccounts(bool BypassRateLimit = false, bool BypassCountCheck = false)
        {
            if ((!BypassRateLimit && (DateTime.Now - startTime).TotalSeconds < 2) || (!BypassCountCheck && AccountsList.Count == 0)) return;

            lock (saveLock)
            {
                byte[] OldInfo = File.Exists(SaveFilePath) ? File.ReadAllBytes(SaveFilePath) : Array.Empty<byte>();
                // Snapshot under accountsLock so a concurrent Add/Remove can't tear the enumeration mid-serialize.
                List<Account> Snapshot;
                lock (accountsLock) Snapshot = new List<Account>(AccountsList);
                string SaveData = JsonConvert.SerializeObject(Snapshot);

                FileInfo Backup = new FileInfo($"{SaveFilePath}.backup");

                if (OldInfo.Length > 0 && (!Backup.Exists || (DateTime.Now - Backup.LastWriteTime).TotalMinutes > 60 * 8))
                    File.WriteAllBytes(Backup.FullName, OldInfo);

                byte[] FinalData;

                if (!PasswordHash.IsEmpty)
                    FinalData = Cryptography.Encrypt(SaveData, ProtectedData.Unprotect(PasswordHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser));
                else if (File.Exists(Path.Combine(Environment.CurrentDirectory, "NoEncryption.IUnderstandTheRisks.iautamor")))
                    FinalData = Encoding.UTF8.GetBytes(SaveData);
                else
                    FinalData = ProtectedData.Protect(Encoding.UTF8.GetBytes(SaveData), Entropy, DataProtectionScope.CurrentUser);

                // Atomic write: a crash or power loss mid-write must never leave a half-written
                // AccountData.json. Write to a temp file, then swap it in with File.Replace.
                string Temp = SaveFilePath + ".tmp";
                File.WriteAllBytes(Temp, FinalData);

                if (File.Exists(SaveFilePath))
                    File.Replace(Temp, SaveFilePath, null);
                else
                    File.Move(Temp, SaveFilePath);
            }
        }

        public void ResetEncryption(bool ManualReset = false)
        {
            foreach (var Form in Application.OpenForms.OfType<Form>())
                if (Form != this)
                    Form.Hide();

            IsResettingPassword = true;

            PasswordLayoutPanel.Visible = !PasswordHash.IsEmpty && ManualReset;
            PasswordSelectionPanel.Visible = false;
            EncryptionSelectionPanel.Visible = PasswordHash.IsEmpty || !ManualReset;

            PasswordPanel.Visible = true;
            PasswordPanel.BringToFront();
        }

        private void PasswordTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Return)
            {
                UnlockButton.PerformClick();

                e.Handled = true;
            }
        }

        private void Error(string Message)
        {
            Program.Logger.Error(Message);

            throw new Exception(Message);
        }

        private void UnlockButton_Click(object sender, EventArgs e)
        {
            try
            {
                byte[] Hash = CryptoHash.Hash(PasswordTextBox.Text);

                if (PasswordTextBox.Text.Length < 4)
                    Error("Invalid password, your password must contain 4 or more characters");

                if (IsResettingPassword)
                {
                    byte[] Data = File.Exists(SaveFilePath) ? File.ReadAllBytes(SaveFilePath) : Array.Empty<byte>();

                    if (Data.Length > 0)
                    {
                        var Header = new ReadOnlySpan<byte>(Data, 0, Cryptography.RAMHeader.Length);

                        if (Header.SequenceEqual(Cryptography.RAMHeader))
                        {
                            if (Hash == null)
                            {
                                EncryptionSelectionPanel.Visible = false;
                                PasswordSelectionPanel.Visible = false;
                                PasswordLayoutPanel.Visible = true;
                                PasswordPanel.Visible = true;
                                PasswordPanel.BringToFront();
                                PasswordTextBox.Focus();

                                return;
                            }

                            Cryptography.Decrypt(Data, Hash);

                            PasswordLayoutPanel.Visible = false;
                            EncryptionSelectionPanel.Visible = true;
                            IsResettingPassword = false;
                        }
                    }
                }
                else
                    LoadAccounts(Hash);
            }
            catch (Exception exception)
            {
                MessageBox.Show($"Incorrect Password!\n\n{exception.Message}", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { PasswordTextBox.Text = string.Empty; PasswordTextBox.Focus(); }
        }

        private void DefaultEncryptionButton_Click(object sender, EventArgs e)
        {
            PasswordHash = Array.Empty<byte>();
            SaveAccounts(true, true);

            PasswordPanel.Visible = false;

            MaybeLaunchModern(); // first-run just finished — open the modern UI now (this path skips LoadAccounts)
        }

        private void PasswordEncryptionButton_Click(object sender, EventArgs e)
        {
            EncryptionSelectionPanel.Visible = false;
            PasswordLayoutPanel.Visible = false;
            PasswordSelectionPanel.Visible = true;
        }

        private ReadOnlyMemory<byte> LastHash = null;

        private void SetPasswordButton_Click(object sender, EventArgs e)
        {
            if (PasswordSelectionTB.Text.Length < 8)
            {
                MessageBox.Show("Your master password must be at least 8 characters.\nThis password protects every account's login cookie — pick something strong.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            byte[] Hash = CryptoHash.Hash(PasswordSelectionTB.Text);

            PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

            if (LastHash.IsEmpty)
            {
                LastHash = new ReadOnlyMemory<byte>(PasswordHash.ToArray());
                PasswordSelectionTB.Text = string.Empty;
                MessageBox.Show("Please confirm your password.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            }
            else
            {
                if (ProtectedData.Unprotect(LastHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser).SequenceEqual(Hash.ToArray()))
                {
                    SaveAccounts(true, true);

                    PasswordSelectionTB.Text = string.Empty;
                    PasswordPanel.Visible = false;

                    LastHash = null;

                    MaybeLaunchModern(); // first-run (password) just finished — open the modern UI now
                }
                else
                {
                    // Reset so the next attempt starts a fresh set/confirm pair. Without this the flow kept
                    // comparing the new confirmation against the stale first-attempt hash and could never succeed.
                    LastHash = null;
                    MessageBox.Show("Those passwords didn't match. Please re-enter your new password.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        CancellationTokenSource PasswordSelectionCancellationToken;

        private void PasswordSelectionTB_TextChanged(object sender, EventArgs e)
        {
            PasswordSelectionCancellationToken?.Cancel();

            SetPasswordButton.Enabled = false;

            PasswordSelectionCancellationToken = new CancellationTokenSource();
            var Token = PasswordSelectionCancellationToken.Token;

            Task.Run(async () =>
            {
                await Task.Delay(500); // Wait until the user has stopped typing to enable the continue button

                if (Token.IsCancellationRequested)
                    return;

                AccountsView.InvokeIfRequired(() => SetPasswordButton.Enabled = true);
            }, PasswordSelectionCancellationToken.Token);
        }

        private void PasswordPanel_VisibleChanged(object sender, EventArgs e)
        {
            foreach (Control Control in Controls)
                if (Control != PasswordPanel)
                    Control.Enabled = !PasswordPanel.Visible;
        }

        public static bool GetUserID(string Username, out long UserId, out RestResponse response)
        {
            RestRequest request = LastValidAccount?.MakeRequest("v1/usernames/users", Method.Post) ?? new RestRequest("v1/usernames/users", Method.Post);
            request.AddJsonBody(new { usernames = new string[] { Username } });

            response = UsersClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK && response.Content.TryParseJson(out JObject UserData) && UserData.ContainsKey("data") && UserData["data"].Count() >= 1)
            {
                UserId = UserData["data"]?[0]?["id"].Value<long>() ?? -1;

                return true;
            }

            UserId = -1;

            return false;
        }

        public void UpdateAccountView(Account account) =>
            AccountsView.InvokeIfRequired(() => AccountsView.UpdateObject(account));

        public static Account AddAccount(string SecurityToken, string Password = "", string AccountJSON = null)
        {
            Account account = new Account(SecurityToken, AccountJSON);

            if (account.Valid)
            {
                account.Password = Password;

                Account exists = AccountsList.AsReadOnly().FirstOrDefault(acc => acc.UserID == account.UserID);

                if (exists != null)
                {
                    account = exists;

                    exists.SecurityToken = SecurityToken;
                    exists.Password = Password;
                    exists.LastUse = DateTime.Now;

                    Instance.RefreshView(exists);
                }
                else
                {
                    lock (accountsLock) AccountsList.Add(account);

                    Instance.RefreshView(account);
                }

                SaveAccounts(true);

                return account;
            }

            return null;
        }

        public static string ShowDialog(string text, string caption, string defaultText = "", bool big = false) // tbh pasted from stackoverflow
        {
            using Form prompt = new Form()
            {
                Width = 340,
                Height = big ? 420 : 125,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                Text = caption,
                StartPosition = FormStartPosition.CenterScreen
            };

            Label textLabel = new Label() { Left = 15, Top = 10, Text = text, AutoSize = true };
            Control textBox;
            Button confirmation = new Button() { Text = "OK", Left = 15, Width = 100, Top = big ? 350 : 50, DialogResult = DialogResult.OK };

            if (big) textBox = new RichTextBox() { Left = 15, Top = 15 + textLabel.Size.Height, Width = 295, Height = 330 - textLabel.Size.Height, Text = defaultText };
            else textBox = new TextBox() { Left = 15, Top = 25, Width = 295, Text = defaultText };

            confirmation.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(textBox);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(textLabel);
            if (!big) prompt.AcceptButton = confirmation;

            prompt.Rescale();

            return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "/UC";
        }

        private void AccountManager_Load(object sender, EventArgs e)
        {
            PasswordPanel.Dock = DockStyle.Fill;

            string AFN = Path.Combine(Directory.GetCurrentDirectory(), "Auto Update.exe");
            string AU2FN = Path.Combine(Directory.GetCurrentDirectory(), "AU.exe");

            if (File.Exists(AFN)) File.Delete(AFN);
            if (File.Exists(AU2FN)) File.Delete(AU2FN);

            DirectoryInfo UpdateDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "Update"));

            if (UpdateDir.Exists)
                UpdateDir.RecursiveDelete();

            afform = new ArgumentsForm();
            ServerListForm = new ServerList();
            UtilsForm = new AccountUtils();
            ImportAccountsForm = new ImportForm();
            FieldsForm = new AccountFields();
            ThemeForm = new ThemeEditor();
            RGForm = new RecentGamesForm();

            MainClient = new RestClient("https://www.roblox.com/");
            AvatarClient = new RestClient("https://avatar.roblox.com/");
            AuthClient = new RestClient("https://auth.roblox.com/");
            EconClient = new RestClient("https://economy.roblox.com/");
            AccountClient = new RestClient("https://accountsettings.roblox.com/");
            GameJoinClient = new RestClient(new RestClientOptions("https://gamejoin.roblox.com/") { UserAgent = "Roblox/WinInet" });
            UsersClient = new RestClient("https://users.roblox.com");
            FriendsClient = new RestClient("https://friends.roblox.com");
            Web13Client = new RestClient("https://web.roblox.com/");

            if (File.Exists(SaveFilePath))
                LoadAccounts();
            else
                ResetEncryption();

            ApplyTheme();

            RGForm.RecentGameSelected += (sender, e) => { PlaceID.Text = e.Game.Details?.placeId.ToString(); };

            PlaceID.Text = General.Exists("SavedPlaceId") ? General.Get("SavedPlaceId") : "5315046213";
            UserID.Text = General.Exists("SavedFollowUser") ? General.Get("SavedFollowUser") : string.Empty;

            if (!Developer.Get<bool>("DevMode"))
            {
                AccountsStrip.Items.Remove(viewFieldsToolStripMenuItem);
                AccountsStrip.Items.Remove(getAuthenticationTicketToolStripMenuItem);
                AccountsStrip.Items.Remove(copyRbxplayerLinkToolStripMenuItem);
                AccountsStrip.Items.Remove(copySecurityTokenToolStripMenuItem);
                AccountsStrip.Items.Remove(copyAppLinkToolStripMenuItem);
            }
            else
                ArgumentsB.Visible = true;

            if (General.Get<bool>("HideUsernames"))
                HideUsernamesCheckbox.Checked = true;

            if (General.Get<bool>("CheckForUpdates"))
            {
                Task.Run(() =>
                {
                    try
                    {
                        ServicePointManager.Expect100Continue = true;
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                        WebClient WC = new WebClient();
                        Assembly assembly = Assembly.GetExecutingAssembly();
                        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                        WC.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/95.0.4638.54 Safari/537.36";
                        string Releases = WC.DownloadString("https://api.github.com/repos/KingIcyCreamProjects/Roblox-Account-Manager/releases/latest");
                        Match match = Regex.Match(Releases, @"""tag_name"":\s*""?([^""]+)");

                        // Compare as real version numbers. The old string method (TrimEnd('.','0') + Replace + double)
                        // collapsed any component ending in 0, so 1.1.10 parsed LOWER than 1.1.9 and double-digit
                        // minor/patch bumps silently never prompted an update. Version handles component order correctly.
                        if (match.Success && Version.TryParse(fvi.FileVersion, out Version CurrentV) && Version.TryParse(match.Groups[1].Value.TrimStart('v', 'V'), out Version NewV))
                        {
                            if (NewV > CurrentV)
                            {
                                bool ShouldUpdate = Utilities.YesNoPrompt("KingsRAM", "An update is available", "Would you like to update now?");

                                if (ShouldUpdate)
                                {
                                    File.WriteAllBytes(AFN, File.ReadAllBytes(Application.ExecutablePath));
                                    Process.Start(AFN, "-update");
                                    Environment.Exit(1);
                                    //if (File.Exists(AFN))
                                    //{
                                    //    Process.Start(AFN, "skip");
                                    //    Environment.Exit(1);
                                    //}
                                    //else
                                    //{
                                    //    MessageBox.Show("You do not have the auto updater downloaded, go to the github page and download the latest release.");
                                    //    Process.Start("https://github.com/ic3w0lf22/Roblox-Account-Manager/releases");
                                    //}
                                }
                            }
                        }
                    }
                    catch (Exception ux) { Program.Logger.Warn($"Update check failed: {ux.Message}"); }
                });
            }

            if (!General.Get<bool>("DisableAgingAlert"))
                Username.Renderer = new AccountRenderer();

            try
            {
                if (Developer.Get<bool>("EnableWebServer"))
                {
                    string Port = WebServer.Exists("WebServerPort") ? WebServer.Get("WebServerPort") : "7963";

                    List<string> Prefixes = new List<string>() { $"http://localhost:{Port}/" };

                    if (WebServer.Get<bool>("AllowExternalConnections"))
                        if (Program.Elevated)
                            Prefixes.Add($"http://*:{Port}/");
                        else
                            using (Process proc = new Process() { StartInfo = new ProcessStartInfo(AppDomain.CurrentDomain.FriendlyName, "-adminRequested") { Verb = "runas" } })
                                try
                                {
                                    proc.Start();
                                    Environment.Exit(1);
                                }
                                catch (Exception ex)
                                {
                                    // User declined the UAC prompt (or elevation failed). We can't bind external
                                    // interfaces without admin, so fall back to loopback-only and say why instead
                                    // of silently ignoring the AllowExternalConnections setting.
                                    Program.Logger.Warn($"External web-server bind needs elevation; continuing loopback-only: {ex.Message}");
                                    MessageBox.Show("External connections require running KingsRAM as administrator.\nThe web server will only accept local (loopback) connections this session.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }


                    AltManagerWS = new WebServer(SendResponse, Prefixes.ToArray());
                    AltManagerWS.Run();
                }
            }
            catch (Exception x) { MessageBox.Show($"Failed to start webserver!\n\n{x}", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Error); }

            Task.Run(() =>
            {
                WebClient WC = new WebClient();
                string VersionJSON = WC.DownloadString("https://clientsettings.roblox.com/v1/client-version/WindowsPlayer");

                if (JObject.Parse(VersionJSON).TryGetValue("clientVersionUpload", out JToken token))
                    CurrentVersion = token.Value<string>();
            });

            IniSettings.Save("RAMSettings.ini");

            PlaceID.AutoCompleteCustomSource = new AutoCompleteStringCollection();
            PlaceID.AutoCompleteMode = AutoCompleteMode.Suggest;
            PlaceID.AutoCompleteSource = AutoCompleteSource.CustomSource;

            // async Task now, so wrapping it observes post-first-await faults instead of losing them (async void).
            Task.Run(LoadRecentGames).ContinueWith(t => Program.Logger.Error($"LoadRecentGames faulted: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
            Task.Run(RobloxProcess.UpdateMatches);

            if (General.Get<bool>("ShuffleJobId"))
                ShuffleIcon_Click(null, EventArgs.Empty);

            if (General.Get<bool>("AutoCookieRefresh"))
            {
                AutoCookieRefresh = new System.Timers.Timer(60000 * 5) { Enabled = true };
                AutoCookieRefresh.Elapsed += async (s, e) =>
                {
                    int Count = 0;

                    // Enumerate a snapshot: this runs on a timer thread and holds the enumerator open for seconds
                    // (5s delay per account), so iterating the live list would throw if the user adds/removes meanwhile.
                    List<Account> Snapshot;
                    lock (accountsLock) Snapshot = new List<Account>(AccountsList);

                    foreach (var Account in Snapshot)
                    {
                        if (Account.GetField("NoCookieRefresh") != "true" && (DateTime.Now - Account.LastUse).TotalDays > 20 && (DateTime.Now - Account.LastAttemptedRefresh).TotalDays >= 7)
                        {
                            Program.Logger.Info($"Attempting to refresh {Account.Username} | Last Use: {Account.LastUse}");

                            Account.LastAttemptedRefresh = DateTime.Now;

                            if (Account.LogOutOfOtherSessions(true)) Count++;

                            await Task.Delay(5000);
                        }
                    };
                };
            }

            var PresenceTimer = new System.Timers.Timer(60000 * 2) { Enabled = true };
            PresenceTimer.Elapsed += (s, e) => AccountsView.InvokeIfRequired(async () => await UpdatePresence());
        }

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            if (AccountsView.BackColor != ThemeEditor.AccountBackground || AccountsView.ForeColor != ThemeEditor.AccountForeground)
            {
                AccountsView.BackColor = ThemeEditor.AccountBackground;
                AccountsView.ForeColor = ThemeEditor.AccountForeground;

                RefreshView();
            }

            AccountsView.HeaderStyle = ThemeEditor.ShowHeaders ? (AccountsView.ShowGroups ? ColumnHeaderStyle.Nonclickable : ColumnHeaderStyle.Clickable) : ColumnHeaderStyle.None;
            AccountsView.CellEditActivation = ObjectListView.CellEditActivateMode.DoubleClick;

            Controls.ApplyTheme();

            afform.ApplyTheme();
            ServerListForm.ApplyTheme();
            UtilsForm.ApplyTheme();
            ImportAccountsForm.ApplyTheme();
            FieldsForm.ApplyTheme();
            ThemeForm.ApplyTheme();
            RGForm.ApplyTheme();

            ControlForm?.ApplyTheme();
            SettingsForm?.ApplyTheme();
        }

        private async Task LoadRecentGames()
        {
            RecentGames = new List<Game>();

            if (File.Exists(RecentGamesFilePath))
            {
                List<Game> Games = JsonConvert.DeserializeObject<List<Game>>(File.ReadAllText(RecentGamesFilePath));

                RGForm.LoadGames(Games);

                foreach (Game RG in Games)
                    await AddRecentGame(RG, true);
            }
        }

        private async Task AddRecentGame(Game RG, bool Loading = false)
        {
            await RG.WaitForDetails();

            RecentGames.RemoveAll(g => g?.Details?.placeId == RG.Details?.placeId);

            while (RecentGames.Count > General.Get<int>("MaxRecentGames"))
            {
                this.InvokeIfRequired(() => PlaceID.AutoCompleteCustomSource.Remove(RecentGames[0].Details?.filteredName));
                RecentGames.RemoveAt(0);
            }

            RecentGames.Add(RG);

            this.InvokeIfRequired(() => PlaceID.AutoCompleteCustomSource.Add(RG.Details.filteredName));

            if (!Loading)
            {
                this.InvokeIfRequired(() => RecentGameAdded?.Invoke(this, new GameArgs(RG)));

                lock (rgSaveLock)
                    File.WriteAllText(RecentGamesFilePath, JsonConvert.SerializeObject(RecentGames));
            }
        }

        private readonly List<ServerData> AttemptedJoins = new List<ServerData>();

        private string WebServerResponse(object Message, bool Success) => JsonConvert.SerializeObject(new { Success, Message });

        // Deny-by-default authorization for the web API. Reads the password live (it can change in settings),
        // requires a configured password of >= 6 chars, and compares in constant time. A missing/short password
        // or a missing/mismatched supplied value is never authorized.
        private static bool PasswordOK(string provided)
        {
            string current = WebServer.Get("Password") ?? "";

            if (current.Length < 6) return false;

            return ConstantTimeEquals(provided ?? "", current);
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);

            int diff = x.Length ^ y.Length;

            for (int i = 0; i < y.Length; i++)
                diff |= (i < x.Length ? x[i] : (byte)0) ^ y[i];

            return diff == 0;
        }

        private string SendResponse(HttpListenerContext Context)
        {
            HttpListenerRequest request = Context.Request;

            bool V2 = request.Url.AbsolutePath.StartsWith("/v2/");
            string AbsolutePath = V2 ? request.Url.AbsolutePath.Substring(3) : request.Url.AbsolutePath;

            string Reply(string Response, bool Success = false, int Code = -1, string Raw = null)
            {
                Context.Response.StatusCode = Code > 0 ? Code : (Success ? 200 : 400);

                return V2 ? WebServerResponse(Response, Success) : (Raw ?? Response);
            }

            if (!request.IsLocal && !WebServer.Get<bool>("AllowExternalConnections")) return Reply("External connections are not allowed", false, 401, string.Empty);

            // A cross-site page in a browser can reach the loopback listener; block browser-driven (CSRF / DNS-rebinding)
            // calls, which always carry an Origin header. Legitimate script/CLI clients send none.
            if (!string.IsNullOrEmpty(request.Headers["Origin"])) return Reply("Cross-origin requests are not allowed", false, 403, string.Empty);

            if (!WebServer.Get<bool>("AllowExternalConnections"))
            {
                string Host = request.Headers["Host"] ?? "";
                if (Host.Length > 0 && !Host.StartsWith("localhost") && !Host.StartsWith("127.0.0.1"))
                    return Reply("Invalid Host", false, 403, string.Empty); // defeats DNS-rebinding: attacker hostname != loopback
            }

            if (AbsolutePath == "/favicon.ico") return ""; // always return nothing

            if (AbsolutePath == "/Running") return Reply("KingsRAM is running", true, Raw: "true");

            string Body = new StreamReader(request.InputStream).ReadToEnd();
            string Method = AbsolutePath.Substring(1);
            string Account = request.QueryString["Account"];
            string Password = request.QueryString["Password"];

            // Deny-by-default for anything reaching us from off-box: a remote caller must ALWAYS supply the
            // password, whatever endpoint they hit. Previously a whole class of state-changing/metadata endpoints
            // (GetCSRFToken, SetServer, Block/Unblock, GetAlias, ...) had no password gate at all once external
            // connections were enabled. Loopback callers keep the existing lighter per-method gating.
            if (!request.IsLocal && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            if (WebServer.Get<bool>("EveryRequestRequiresPassword") && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            if ((Method == "GetCookie" || Method == "GetAccounts" || Method == "GetAccountsJson" || Method == "LaunchAccount" || Method == "FollowUser") && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            if (Method == "GetAccounts")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string GroupFilter = request.QueryString["Group"];

                // Snapshot under the lock (this runs on a web-server thread that must not tear the list mid-mutation),
                // then join in one pass instead of O(n^2) string concatenation.
                List<Account> Snapshot; lock (accountsLock) Snapshot = new List<Account>(AccountsList);
                string Names = string.Join(",", Snapshot.Where(acc => string.IsNullOrEmpty(GroupFilter) || acc.Group == GroupFilter).Select(acc => acc.Username));

                return Reply(Names, true, Raw: Names);
            }

            if (Method == "GetAccountsJson")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccountsJson` not allowed", false, 401, "Method not allowed");

                string GroupFilter = request.QueryString["Group"];
                // Never hand a raw takeover cookie back over a non-loopback connection, even with the password/flag —
                // the transport is cleartext HTTP and would expose it to anyone sniffing the segment.
                bool ShowCookies = request.IsLocal && PasswordOK(Password) && request.QueryString["IncludeCookies"] == "true" && WebServer.Get<bool>("AllowGetCookie");

                List<object> Objects = new List<object>();

                List<Account> Snapshot; lock (accountsLock) Snapshot = new List<Account>(AccountsList);

                foreach (Account acc in Snapshot)
                {
                    if (!string.IsNullOrEmpty(GroupFilter) && acc.Group != GroupFilter) continue;

                    object AccountObject = new
                    {
                        acc.Username,
                        acc.UserID,
                        acc.Alias,
                        acc.Description,
                        acc.Group,
                        acc.CSRFToken,
                        LastUsed = acc.LastUse.ToRobloxTick(),
                        Cookie = ShowCookies ? acc.SecurityToken : null,
                        acc.Fields,
                    };

                    Objects.Add(AccountObject);
                }

                return Reply(JsonConvert.SerializeObject(Objects), true);
            }

            if (Method == "ImportCookie")
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `ImportCookie` not allowed", false, 401, "Method not allowed");

                Account New = AddAccount(request.QueryString["Cookie"]);

                bool Success = New != null;

                return Reply(Success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", Success, Raw: Success ? "true" : "false");
            }

            if (string.IsNullOrEmpty(Account)) return Reply("Empty Account", false);

            // Snapshot under the lock so this web-thread lookup can't tear the list during a mutation.
            // ponytail: still an O(n) scan — a Username/UserID dictionary index would be O(1), but n is a
            // handful of accounts on a loopback API, so the index isn't worth the add/remove bookkeeping.
            Account account;
            lock (accountsLock) account = AccountsList.FirstOrDefault(x => x.Username == Account || x.UserID.ToString() == Account);

            if (account == null || !account.GetCSRFToken(out string Token)) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, Raw: "Invalid Account");

            if (Method == "GetCookie")
            {
                if (!WebServer.Get<bool>("AllowGetCookie")) return Reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");
                if (!request.IsLocal) return Reply("Cookies can only be retrieved over a local (loopback) connection", false, 403, "Cookies are loopback-only");

                return Reply(account.SecurityToken, true);
            }

            if (Method == "LaunchAccount")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                bool ValidPlaceId = long.TryParse(request.QueryString["PlaceId"], out long PlaceId); if (!ValidPlaceId) return Reply("Invalid PlaceId provided", false, Raw: "Invalid PlaceId");

                string JobID = !string.IsNullOrEmpty(request.QueryString["JobId"]) ? request.QueryString["JobId"] : "";
                string FollowUser = request.QueryString["FollowUser"];
                string JoinVIP = request.QueryString["JoinVIP"];

                account.JoinServer(PlaceId, JobID, FollowUser == "true", JoinVIP == "true");

                return Reply($"Launched {Account} to {PlaceId}", true);
            }

            if (Method == "FollowUser") // https://github.com/ic3w0lf22/Roblox-Account-Manager/pull/52
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `FollowUser` not allowed", false, 401, "Method not allowed");

                string User = request.QueryString["Username"]; if (string.IsNullOrEmpty(User)) return Reply("Invalid Username Parameter", false);

                if (!GetUserID(User, out long UserId, out var Response))
                    return Reply($"[{Response.StatusCode} {Response.StatusDescription}] Failed to get UserId: {Response.Content}", false);

                account.JoinServer(UserId, "", true);

                return Reply($"Joining {User}'s game on {Account}", true);
            }

            if (Method == "GetCSRFToken") return Reply(Token, true);
            if (Method == "GetAlias") return Reply(account.Alias, true);
            if (Method == "GetDescription") return Reply(account.Description, true);

            if (Method == "BlockUser" && !string.IsNullOrEmpty(request.QueryString["UserId"]))
                try
                {
                    var Res = account.BlockUserId(request.QueryString["UserId"], Context: Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockUser" && !string.IsNullOrEmpty(request.QueryString["UserId"]))
                try
                {
                    var Res = account.UnblockUserId(request.QueryString["UserId"], Context: Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "GetBlockedList") try
                {
                    var Res = account.GetBlockedList(Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockEveryone" && account.UnblockEveryone(out string UbRes) is bool UbSuccess) return Reply(UbRes, UbSuccess);

            if (Method == "SetServer" && !string.IsNullOrEmpty(request.QueryString["PlaceId"]) && !string.IsNullOrEmpty(request.QueryString["JobId"]))
            {
                if (!long.TryParse(request.QueryString["PlaceId"], out long SetPlaceId)) return Reply("Invalid PlaceId provided", false, Raw: "Invalid PlaceId");

                string RSP = account.SetServer(SetPlaceId, request.QueryString["JobId"], out bool Success);

                return Reply(RSP, Success);
            }

            if (Method == "SetRecommendedServer")
            {
                long RecPlaceId = RBX_Alt_Manager.ServerList.CurrentPlaceID;
                if (!string.IsNullOrEmpty(request.QueryString["PlaceId"]) && !long.TryParse(request.QueryString["PlaceId"], out RecPlaceId)) return Reply("Invalid PlaceId provided", false, Raw: "Invalid PlaceId");

                int attempts = 0;
                string res = "-1";

                for (int i = RBX_Alt_Manager.ServerList.servers.Count - 1; i > 0; i--)
                {
                    if (attempts > 10)
                        return Reply("Too many failed attempts", false);

                    ServerData server = RBX_Alt_Manager.ServerList.servers[i];

                    if (AttemptedJoins.FirstOrDefault(x => x.id == server.id) != null) continue;
                    if (AttemptedJoins.Count > 100) AttemptedJoins.Clear();

                    AttemptedJoins.Add(server);

                    attempts++;

                    res = account.SetServer(RecPlaceId, server.id, out bool iSuccess);

                    if (iSuccess)
                        return Reply(res, iSuccess);
                }

                bool Success = !string.IsNullOrEmpty(res);

                return Reply(Success ? "Failed" : res, Success);
            }

            if (Method == "GetField" && !string.IsNullOrEmpty(request.QueryString["Field"])) return Reply(account.GetField(request.QueryString["Field"]), true);

            if (Method == "SetField" && !string.IsNullOrEmpty(request.QueryString["Field"]) && !string.IsNullOrEmpty(request.QueryString["Value"]))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetField` not allowed", false, 401, "Method not allowed");

                account.SetField(request.QueryString["Field"], request.QueryString["Value"]);

                return Reply($"Set Field {request.QueryString["Field"]} to {request.QueryString["Value"]} for {account.Username}", true);
            }
            if (Method == "RemoveField" && !string.IsNullOrEmpty(request.QueryString["Field"]))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `RemoveField` not allowed", false, 401, "Method not allowed");

                account.RemoveField(request.QueryString["Field"]);

                return Reply($"Removed Field {request.QueryString["Field"]} from {account.Username}", true);
            }

            if (Method == "SetAvatar" && Body.TryParseJson(out object _))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetAvatar` not allowed", false, 401, "Method not allowed");

                account.SetAvatar(Body);

                return Reply($"Attempting to set avatar of {account.Username} to {Body}", true);
            }

            if (Method == "SetAlias" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetAlias` not allowed", false, Raw: "Method not allowed");

                account.Alias = Body;
                UpdateAccountView(account);

                return Reply($"Set Alias of {account.Username} to {Body}", true);
            }
            if (Method == "SetDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetDescription` not allowed", false, 401, "Method not allowed");

                account.Description = Body;
                UpdateAccountView(account);

                return Reply($"Set Description of {account.Username} to {Body}", true);
            }
            if (Method == "AppendDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return V2 ? WebServerResponse("Method `AppendDescription` not allowed", false) : "Method not allowed";

                account.Description += Body;
                UpdateAccountView(account);

                return Reply($"Appended Description of {account.Username} with {Body}", true);
            }

            return Reply("404 not found", false, 404);
        }

        private void AccountManager_Shown(object sender, EventArgs e)
        {
            if (!UpdateMultiRoblox() && !General.Get<bool>("HideRbxAlert"))
                MessageBox.Show("WARNING: Roblox is currently running, multi roblox will not work until you restart the account manager with roblox closed.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            int Major = Environment.OSVersion.Version.Major, Minor = Environment.OSVersion.Version.Minor;

            PuppeteerSupported = !(Major < 6 || (Major == 6 && Minor <= 1));

            if (General.Get<bool>("UseCefSharpBrowser")) PuppeteerSupported = false;

            if (!PuppeteerSupported)
            {
                AddAccountsStrip.Items.Remove(bulkUserPassToolStripMenuItem);
                AddAccountsStrip.Items.Remove(customURLJSToolStripMenuItem);
                OpenBrowserStrip.Items.Remove(URLJSToolStripMenuItem);
                OpenBrowserStrip.Items.Remove(joinGroupToolStripMenuItem);
            }

            if (PuppeteerSupported && (!Directory.Exists(AccountBrowser.Fetcher.DownloadsFolder) || Directory.GetDirectories(AccountBrowser.Fetcher.DownloadsFolder).Length == 0))
            {
                Add.Visible = false;
                Remove.Visible = false;
                DownloadProgressBar.Visible = true;
                DLChromiumLabel.Visible = true;

                Task.Run(async () =>
                {
                    IsDownloadingChromium = true;

                    void DownloadProgressChanged(object s, DownloadProgressChangedEventArgs e) => DownloadProgressBar.InvokeIfRequired(() => { DownloadProgressBar.Value = e.ProgressPercentage; });

                    AccountBrowser.Fetcher.DownloadProgressChanged += DownloadProgressChanged;
                    await AccountBrowser.Fetcher.DownloadAsync(BrowserFetcher.DefaultChromiumRevision);
                    AccountBrowser.Fetcher.DownloadProgressChanged -= DownloadProgressChanged;

                    IsDownloadingChromium = false;

                    this.InvokeIfRequired(() =>
                    {
                        Add.Visible = true;
                        Remove.Visible = true;
                        DownloadProgressBar.Visible = false;
                        DLChromiumLabel.Visible = false;
                    });
                });
            }
            else if (!PuppeteerSupported)
            {
                FileInfo Cef = new FileInfo(Path.Combine(Environment.CurrentDirectory, "x86", "CefSharp.dll"));

                if (Cef.Exists)
                {
                    FileVersionInfo Info = FileVersionInfo.GetVersionInfo(Cef.FullName);

                    if (Info.ProductMajorPart != 109)
                        try { Directory.GetParent(Cef.FullName).RecursiveDelete(); } catch { }
                }

                if (!Directory.Exists(Path.Combine(Environment.CurrentDirectory, "x86")))
                {
                    var Existing = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "x86"));

                    DLChromiumLabel.Text = "Downloading CefSharp...";

                    Add.Visible = false;
                    Remove.Visible = false;
                    DownloadProgressBar.Visible = true;
                    DLChromiumLabel.Visible = true;

                    Task.Run(async () =>
                    {
                        IsDownloadingChromium = true;

                        using HttpClient client = new HttpClient();

                        string FileName = Path.GetTempFileName(), DownloadUrl = Resources.CefSharpDownload;

                        var TotalDownloadSize = (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, DownloadUrl))).Content.Headers.ContentLength.Value;
                        Progress<float> progress = new Progress<float>(progress => DownloadProgressBar.InvokeIfRequired(() => DownloadProgressBar.Value = (int)(progress * 100)));

                        using (var file = new FileStream(FileName, FileMode.Create, FileAccess.Write, FileShare.None))
                            await client.DownloadAsync(DownloadUrl, file, progress);

                        if (Existing.Exists) Existing.RecursiveDelete();

                        System.IO.Compression.ZipFile.ExtractToDirectory(FileName, Environment.CurrentDirectory);

                        IsDownloadingChromium = false;

                        this.InvokeIfRequired(() =>
                        {
                            Add.Visible = true;
                            Remove.Visible = true;
                            DownloadProgressBar.Visible = false;
                            DLChromiumLabel.Visible = false;
                        });
                    });
                }
            }

            if (AccountControl.Get<bool>("StartOnLaunch"))
                LaunchNexus.PerformClick();
        }

        public bool UpdateMultiRoblox()
        {
            bool Enabled = General.Get<bool>("EnableMultiRbx");

            if (Enabled && rbxMultiMutex == null)
                try
                {
                    rbxMultiMutex = new Mutex(true, "ROBLOX_singletonMutex");

                    if (!rbxMultiMutex.WaitOne(TimeSpan.Zero, true))
                        return false;
                }
                catch { return false; }
            else if (!Enabled && rbxMultiMutex != null)
            {
                rbxMultiMutex.Close();
                rbxMultiMutex = null;
            }

            return true;
        }

        // Single implementation for both the Remove button and the right-click "Remove Account" item.
        // (They used to be duplicated with a subtle Remove-vs-RemoveAll divergence on the single-select path.)
        private void RemoveSelectedAccounts()
        {
            var Selected = AccountsView.SelectedObjects.Cast<Account>().ToList();

            if (Selected.Count > 1)
            {
                if (MessageBox.Show($"Are you sure you want to remove {Selected.Count} accounts?", "Remove Accounts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                lock (accountsLock) foreach (Account acc in Selected) AccountsList.Remove(acc);
            }
            else if (SelectedAccount != null)
            {
                if (MessageBox.Show($"Are you sure you want to remove {SelectedAccount.Username}?", "Remove Account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                lock (accountsLock) AccountsList.Remove(SelectedAccount);
            }
            else return;

            RefreshView();
            SaveAccounts();
        }

        private void Remove_Click(object sender, EventArgs e) => RemoveSelectedAccounts();

        private async void Add_Click(object sender, EventArgs e)
        {
            if (PuppeteerSupported)
            {
                Add.Enabled = false;

                try { await new AccountBrowser().Login(); }
                catch (Exception x)
                {
                    Program.Logger.Error($"[Add_Click] An error was encountered attempting to login: {x}");

                    if (Utilities.YesNoPrompt($"An error was encountered attempting to login", "You may have a corrupted chromium installation", "Would you like to re-install chromium?", false))
                    {
                        MessageBox.Show("KingsRAM will now close since it can't delete the folder while it's in use.", "", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        if (Directory.GetFiles(AccountBrowser.Fetcher.DownloadsFolder).Length <= 1 && Directory.GetDirectories(AccountBrowser.Fetcher.DownloadsFolder).Length <= 1)
                            Process.Start("cmd.exe", $"/c rmdir /s /q \"{AccountBrowser.Fetcher.DownloadsFolder}\"");
                        else
                            Process.Start("explorer.exe", "/select, " + AccountBrowser.Fetcher.DownloadsFolder);

                        Environment.Exit(0);
                    }
                }

                Add.Enabled = true;
            }
            else
                CefBrowser.Instance.Login();
        }

        // Entry points reused by the modern (WebView2) UI — they drive the exact same proven flows.
        public async Task ModernAddAccount()
        {
            if (PuppeteerSupported) await new AccountBrowser().Login();
            else CefBrowser.Instance.Login();
        }

        public void ModernOpenBrowser(Account acc)
        {
            if (acc == null) return;
            if (PuppeteerSupported) new AccountBrowser(acc);
            else CefBrowser.Instance.EnterBrowserMode(acc);
        }

        // Modern-UI bridges to the proven classic add flows (Puppeteer-based) so the new UI doesn't duplicate them.
        public void ModernBulkUserPass() => this.InvokeIfRequired(() => { try { bulkUserPassToolStripMenuItem_Click(null, EventArgs.Empty); } catch (Exception ex) { Program.Logger.Error($"ModernBulkUserPass: {ex}"); } });
        public void ModernAddCustom() => this.InvokeIfRequired(() => { try { customURLJSToolStripMenuItem_Click(null, EventArgs.Empty); } catch (Exception ex) { Program.Logger.Error($"ModernAddCustom: {ex}"); } });

        public void SaveSettings() { try { IniSettings.Save("RAMSettings.ini"); } catch (Exception ex) { Program.Logger.Error($"SaveSettings: {ex}"); } }

        private void DownloadProgressBar_Click(object sender, EventArgs e)
        {
            static void ShowManualInstallInstructions()
            {
                string Temp = Path.Combine(Path.GetTempPath(), "manual install instructions.html");

                string DownloadLink = PuppeteerSupported ? (string)typeof(BrowserFetcher).GetMethod("GetDownloadURL", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { AccountBrowser.Fetcher.Product, AccountBrowser.Fetcher.Platform, AccountBrowser.Fetcher.DownloadHost, BrowserFetcher.DefaultChromiumRevision }) : Resources.CefSharpDownload;
                string Directory = PuppeteerSupported ? Path.Combine(AccountBrowser.Fetcher.DownloadsFolder, $"{AccountBrowser.Fetcher.Platform}-{BrowserFetcher.DefaultChromiumRevision}") : Path.Combine(Environment.CurrentDirectory);

                File.WriteAllText(Temp, string.Format(Resources.ManualInstallHTML, PuppeteerSupported ? "Chromium" : "CefSharp", DownloadLink, PuppeteerSupported ? "chrome-win" : "x86", Directory));

                Process.Start(new ProcessStartInfo(Temp) { UseShellExecute = true });
                Process.Start(new ProcessStartInfo("cmd") { Arguments = $"/c mkdir \"{Directory}\"", CreateNoWindow = true });
            }

            if (TaskDialog.IsPlatformSupported)
            {
                TaskDialog Dialog = new TaskDialog()
                {
                    Caption = "Add Account",
                    InstructionText = $"{(PuppeteerSupported ? "Chromium" : "CefSharp")} is still being downloaded",
                    Text = "If this is not working for you, you can choose to manually install",
                    Icon = TaskDialogStandardIcon.Information
                };

                TaskDialogButton Manual = new TaskDialogButton("Manual", "Download Manually");
                TaskDialogButton Wait = new TaskDialogButton("Wait", "Wait");

                Wait.Click += (s, e) => Dialog.Close();
                Manual.Click += (s, e) =>
                {
                    Dialog.Close();

                    ShowManualInstallInstructions();
                };

                Dialog.Controls.Add(Manual);
                Dialog.Controls.Add(Wait);
                Wait.Default = true;

                Dialog.Show();
            }
            else if (MessageBox.Show($"{(PuppeteerSupported ? "Chromium" : "CefSharp")} is still downloading, you may have to wait a while before adding an account.\n\nNot working? You can choose to manually install by pressing \"Yes\"", "KingsRAM", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information) == DialogResult.Yes)
                ShowManualInstallInstructions();
        }

        private void DLChromiumLabel_Click(object sender, EventArgs e) => DownloadProgressBar_Click(sender, e);

        private void manualToolStripMenuItem_Click(object sender, EventArgs e) => Add.PerformClick();

        private void addAccountsToolStripMenuItem_Click(object sender, EventArgs e) => Add.PerformClick();

        private void byCookieToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ImportAccountsForm.Show();
            ImportAccountsForm.WindowState = FormWindowState.Normal;
            ImportAccountsForm.BringToFront();
        }

        private async void bulkUserPassToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string Combos = ShowDialog("Separate the accounts with new lines\nMust be in user:pass form", "Import by User:Pass", big: true);

            if (Combos == "/UC") return;

            List<string> ComboList = new List<string>(Combos.Split('\n'));

            var Size = new System.Numerics.Vector2(455, 485);
            AccountBrowser.CreateGrid(Size);

            for (int i = 0; i < ComboList.Count; i++)
            {
                string Combo = ComboList[i];

                if (!Combo.Contains(':')) continue;

                var LoginTask = new AccountBrowser() { Index = i, Size = Size }.Login(Combo.Substring(0, Combo.IndexOf(':')), Combo.Substring(Combo.IndexOf(":") + 1));

                if ((i + 1) % 2 == 0) await LoginTask;
            }
        }

        private void AccountsView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (AccountsView.SelectedItems.Count != 1)
            {
                SelectedAccount = null;
                SelectedAccountItem = null;

                if (AccountsView.SelectedObjects.Count > 1)
                    SelectedAccounts = AccountsView.SelectedObjects.Cast<Account>().ToList();

                return;
            }

            SelectedAccount = AccountsView.SelectedObject as Account;
            SelectedAccountItem = AccountsView.SelectedItem;

            if (SelectedAccount == null) return;

            AccountsView.HideSelection = false;

            Alias.Text = SelectedAccount.Alias;
            DescriptionBox.Text = SelectedAccount.Description;

            if (!string.IsNullOrEmpty(SelectedAccount.GetField("SavedPlaceId"))) PlaceID.Text = SelectedAccount.GetField("SavedPlaceId");
            if (!string.IsNullOrEmpty(SelectedAccount.GetField("SavedJobId"))) JobID.Text = SelectedAccount.GetField("SavedJobId");
        }

        private void SetAlias_Click(object sender, EventArgs e)
        {
            foreach (Account account in AccountsView.SelectedObjects)
                account.Alias = Alias.Text;

            RefreshView();
        }

        private void SetDescription_Click(object sender, EventArgs e)
        {
            foreach (Account account in AccountsView.SelectedObjects)
                account.Description = DescriptionBox.Text;

            RefreshView();
        }

        private void JoinServer_Click(object sender, EventArgs e)
        {
            Match IDMatch = Regex.Match(PlaceID.Text, @"\/games\/(\d+)[\/|\?]?"); // idiotproofing

            if (PlaceID.Text.Contains("privateServerLinkCode") && IDMatch.Success)
                JobID.Text = PlaceID.Text;

            Game G = RecentGames.FirstOrDefault(RG => RG.Details.filteredName == PlaceID.Text);

            if (G != null)
                PlaceID.Text = G.Details.placeId.ToString();

            PlaceID.Text = IDMatch.Success ? IDMatch.Groups[1].Value : Regex.Replace(PlaceID.Text, "[^0-9]", "");

            bool VIPServer = JobID.TextLength > 4 && JobID.Text.Substring(0, 4) == "VIP:";

            if (!long.TryParse(PlaceID.Text, out long PlaceId)) return;

            if (!PlaceTimer.Enabled)
                _ = Task.Run(() => AddRecentGame(new Game(PlaceId)));

            CancelLaunching();

            bool LaunchMultiple = AccountsView.SelectedObjects.Count > 1;

            _ = Task.Run(async () => // was new Thread(async …).Start() which detached the async state machine from the thread
            {
                if (LaunchMultiple)
                {
                    LauncherToken = new CancellationTokenSource();

                    await LaunchAccounts(SelectedAccounts, PlaceId, VIPServer ? JobID.Text.Substring(4) : JobID.Text, false, VIPServer);
                }
                else if (SelectedAccount != null)
                {
                    string res = await SelectedAccount.JoinServer(PlaceId, VIPServer ? JobID.Text.Substring(4) : JobID.Text, false, VIPServer);

                    if (!res.Contains("Success"))
                        MessageBox.Show(res, "Join Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private async void Follow_Click(object sender, EventArgs e)
        {
            if (!GetUserID(UserID.Text, out long UserId, out var Response))
            {
                MessageBox.Show($"[{Response.StatusCode} {Response.StatusDescription}] Failed to get UserId: {Response.Content}");
                return;
            }
    
            if (!(await Presence.GetPresenceSingular(UserId) is UserPresence Status && Status.userPresenceType == UserPresenceType.InGame && Status.placeId is long FollowPlaceID && FollowPlaceID > 0) &&
                !Utilities.YesNoPrompt("Warning", "The user you are trying to follow is not in game or has their joins off", "Do you want to attempt to join anyways?")) return;

            CancelLaunching();

            if (AccountsView.SelectedObjects.Count > 1)
            {
                LauncherToken = new CancellationTokenSource();

                await LaunchAccounts(SelectedAccounts, UserId, "", true);
            }
            else if (SelectedAccount != null)
            {
                string res = await SelectedAccount.JoinServer(UserId, "", true);

                if (!res.Contains("Success"))
                    MessageBox.Show(res);
            }
        }

        private void ServerList_Click(object sender, EventArgs e)
        {
            if (AccountsList.Count == 0 || LastValidAccount == null)
                MessageBox.Show("Some features may not work unless there is a valid account", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (ServerListForm.Visible)
            {
                ServerListForm.WindowState = FormWindowState.Normal;
                ServerListForm.BringToFront();
            }
            else
                ServerListForm.Show();

            ServerListForm.Busy = false; // incase it somehow bugs out

            ServerListForm.StartPosition = FormStartPosition.Manual;
            ServerListForm.Top = Top;
            ServerListForm.Left = Right;
        }

        private void HideUsernamesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            General.Set("HideUsernames", HideUsernamesCheckbox.Checked ? "true" : "false");

            AccountsView.BeginUpdate();

            Username.Width = HideUsernamesCheckbox.Checked ? 0 : (int)(120 * Program.Scale);

            AccountsView.EndUpdate();
        }

        private void removeAccountToolStripMenuItem_Click(object sender, EventArgs e) => RemoveSelectedAccounts();

        private void AccountManager_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsDownloadingChromium && !Utilities.YesNoPrompt("KingsRAM", $"{(PuppeteerSupported ? "Chromium" : "CefSharp")} is still being downloaded, exiting may corrupt your chromium installation and prevent account manager from working", "Exit anyways?", false))
            {
                e.Cancel = true;

                return;
            }

            AltManagerWS?.Stop();

            // Flush any change still sitting in the debounce timer (alias/description/window positions) so it
            // isn't lost when the process exits before the ~1s timer fires.
            try { SaveAccounts(); } catch (Exception ex) { Program.Logger.Error($"Save-on-close failed: {ex}"); }

            if (PlaceID == null || string.IsNullOrEmpty(PlaceID.Text)) return;

            General.Set("SavedPlaceId", PlaceID.Text);
            General.Set("SavedFollowUser", UserID.Text);
            IniSettings.Save("RAMSettings.ini");
        }

        private void BrowserButton_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null)
            {
                MessageBox.Show("No Account Selected!");
                return;
            }

            UtilsForm.Show();
            UtilsForm.WindowState = FormWindowState.Normal;
            UtilsForm.BringToFront();
        }

        private static System.Windows.Forms.Timer ClipboardClearTimer;

        // Copy secrets (auth tickets, cookies, passwords, launch links that embed a ticket) to the clipboard,
        // then auto-clear after a delay so they don't linger in the clipboard/clipboard-history for any process
        // to read. Only clears if the clipboard still holds exactly what we put there (never nukes a later copy).
        private static void CopySensitive(string Text)
        {
            if (string.IsNullOrEmpty(Text)) return;

            try { Clipboard.SetText(Text); } catch { return; }

            ClipboardClearTimer?.Stop();
            ClipboardClearTimer?.Dispose();
            ClipboardClearTimer = new System.Windows.Forms.Timer { Interval = 45000 };
            ClipboardClearTimer.Tick += (s, e) =>
            {
                ClipboardClearTimer.Stop();
                try { if (Clipboard.ContainsText() && Clipboard.GetText() == Text) Clipboard.Clear(); } catch { }
            };
            ClipboardClearTimer.Start();
        }

        private void getAuthenticationTicketToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount != null)
            {
                if (SelectedAccount.GetAuthTicket(out string STicket))
                    CopySensitive(STicket);

                return;
            }

            if (SelectedAccounts.Count < 1) return;

            List<string> Tickets = new List<string>();

            foreach (Account acc in SelectedAccounts)
            {
                if (acc.GetAuthTicket(out string Ticket))
                    Tickets.Add($"{acc.Username}:{Ticket}");
            }

            if (Tickets.Count > 0)
                CopySensitive(string.Join("\n", Tickets));
        }

        private void copyRbxplayerLinkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (SelectedAccount.GetAuthTicket(out string Ticket))
            {
                bool HasJobId = string.IsNullOrEmpty(JobID.Text);
                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                Random r = new Random();
                CopySensitive(string.Format("<roblox-player://1/1+launchmode:play+gameinfo:{0}+launchtime:{4}+browsertrackerid:{5}+placelauncherurl:https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{3}&placeId={1}{2}+robloxLocale:en_us+gameLocale:en_us>", Ticket, PlaceID.Text, HasJobId ? "" : ("&gameId=" + JobID.Text), HasJobId ? "" : "Job", LaunchTime, r.Next(100000, 130000).ToString() + r.Next(100000, 900000).ToString()));
            }
        }

        private void ArgumentsB_Click(object sender, EventArgs e)
        {
            if (afform != null)
                if (afform.Visible)
                    afform.HideForm();
                else
                    afform.ShowForm();
        }

        private void copySecurityTokenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Tokens = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Tokens.Add(account.SecurityToken);

            CopySensitive(string.Join("\n", Tokens));
        }

        private void copyUsernameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Usernames = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Usernames.Add(account.Username);

            Clipboard.SetText(string.Join("\n", Usernames));
        }

        private void copyPasswordToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Passwords = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Passwords.Add($"{account.Password}");

            CopySensitive(string.Join("\n", Passwords));
        }

        private void copyUserPassComboToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Combos = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Combos.Add($"{account.Username}:{account.Password}");

            CopySensitive(string.Join("\n", Combos));
        }

        private void copyUserIdToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> UserIds = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                UserIds.Add(account.UserID.ToString());

            Clipboard.SetText(string.Join("\n", UserIds));
        }

        private void PlaceID_TextChanged(object sender, EventArgs e)
        {
            if (PlaceTimer.Enabled) PlaceTimer.Stop();

            PlaceTimer.Start();
        }

        private async void PlaceTimer_Tick(object sender, EventArgs e)
        {
            if (EconClient == null) return;

            PlaceTimer.Stop();

            RestRequest request = new RestRequest($"v2/assets/{PlaceID.Text}/details", Method.Get);
            request.AddHeader("Accept", "application/json");
            RestResponse response = await EconClient.ExecuteAsync(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK && response.Content.StartsWith("{") && response.Content.EndsWith("}"))
            {
                ProductInfo placeInfo = JsonConvert.DeserializeObject<ProductInfo>(response.Content);

                Utilities.InvokeIfRequired(this, () => CurrentPlace.Text = placeInfo.Name);
            }
        }

        private void moveToToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (AccountsView.SelectedObjects.Count == 0) return;

            string GroupName = ShowDialog("Group Name", "Move Account to Group", SelectedAccount != null ? SelectedAccount.Group : string.Empty);

            if (GroupName == "/UC") return; // User Cancelled
            if (string.IsNullOrEmpty(GroupName)) GroupName = "Default";

            foreach (Account acc in AccountsView.SelectedObjects)
                acc.Group = GroupName;

            RefreshView();
            SaveAccounts();
        }

        private void copyGroupToolStripMenuItem_Click(object sender, EventArgs e) => Clipboard.SetText(SelectedAccount?.Group ?? "No Account Selected");

        private void copyAppLinkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (SelectedAccount.GetAuthTicket(out string Ticket))
            {
                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                Random r = new Random();
                CopySensitive(string.Format("<roblox-player://1/1+launchmode:app+gameinfo:{0}+launchtime:{1}+browsertrackerid:{2}+robloxLocale:en_us+gameLocale:en_us>", Ticket, LaunchTime, r.Next(500000, 600000).ToString() + r.Next(10000, 90000).ToString()));
            }
        }

        // KingsRAM's own Discord (was ic3w0lf22's). Reads from settings so you can paste a real
        // discord.gg invite into RAMSettings.ini [General] DiscordLink= without a rebuild.
        private void JoinDiscord_Click(object sender, EventArgs e)
            => Process.Start(General.Exists("DiscordLink") && !string.IsNullOrWhiteSpace(General.Get("DiscordLink"))
                ? General.Get("DiscordLink")
                : "https://discord.com/channels/1526775420966670476");

        private void OpenBrowser_Click(object sender, EventArgs e)
        {
            if (PuppeteerSupported)
                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account);
            else if (!PuppeteerSupported && SelectedAccount != null)
                CefBrowser.Instance.EnterBrowserMode(SelectedAccount);
        }

        private void customURLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Uri.TryCreate(ShowDialog("URL", "Open Browser"), UriKind.Absolute, out Uri Link))
                if (PuppeteerSupported)
                    foreach (Account account in AccountsView.SelectedObjects)
                        new AccountBrowser(account, Link.ToString(), string.Empty);
                else if (!PuppeteerSupported && SelectedAccount != null)
                    CefBrowser.Instance.EnterBrowserMode(SelectedAccount, Link.ToString());
        }

        private void URLJSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Utilities.YesNoPrompt("Warning", "Your accounts may be at risk using this feature", "Do not paste in javascript unless you know what it does, your account's cookies can easily be logged through javascript.\n\nPress Yes to continue", true)) return;

            if (Uri.TryCreate(ShowDialog("URL", "Open Browser"), UriKind.Absolute, out Uri Link))
            {
                string Script = ShowDialog("Javascript", "Open Browser", big: true);

                if (Script == "/UC") return; // dialog cancelled — don't inject the literal sentinel as JS

                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account, Link.ToString(), Script);
            }
        }

        private void joinGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Uri.TryCreate(ShowDialog("Group Link", "Open Browser"), UriKind.Absolute, out Uri Link))
            {
                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account, Link.ToString(), PostNavigation: async (page) =>
                    {
                        await (await page.WaitForSelectorAsync("#group-join-button", new WaitForSelectorOptions() { Timeout = 12000 })).ClickAsync();
                    });
            }
        }

        private void customURLJSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int Count = 1;

            if (ModifierKeys == Keys.Shift)
                int.TryParse(ShowDialog("Amount (Limited to 15)", "Launch Browser", "1"), out Count);

            if (Uri.TryCreate(ShowDialog("URL", "Launch Browser", "https://roblox.com/"), UriKind.Absolute, out Uri Link))
            {
                string Script = ShowDialog("Javascript", "Launch Browser", big: true);

                if (Script == "/UC") return; // dialog cancelled — don't inject the literal sentinel as JS

                var Size = new System.Numerics.Vector2(550, 440);
                AccountBrowser.CreateGrid(Size);

                for (int i = 0; i < Math.Min(Count, 15); i++) {
                    var Browser = new AccountBrowser() { Size = Size, Index = i };

                    _ = Browser.LaunchBrowser(Url: Link.ToString(), Script: Script, PostNavigation: async (p) => await Browser.LoginTask(p));
                }
            }
        }

        private void copyProfileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Profiles = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Profiles.Add($"https://www.roblox.com/users/{account.UserID}/profile");

            Clipboard.SetText(string.Join("\n", Profiles));
        }

        private void viewFieldsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            FieldsForm.View(SelectedAccount);
        }

        private void SaveToAccount_Click(object sender, EventArgs e)
        {
            if (ModifierKeys == Keys.Shift)
            {
                List<Account> HasSaved = new List<Account>();

                foreach (Account account in AccountsList)
                    if (account.Fields.ContainsKey("SavedPlaceId") || account.Fields.ContainsKey("SavedJobId"))
                        HasSaved.Add(account);

                if (HasSaved.Count > 0 && MessageBox.Show($"Are you sure you want to remove {HasSaved.Count} saved Place Ids?", "KingsRAM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.OK)
                    foreach (Account account in HasSaved)
                    {
                        account.RemoveField("SavedPlaceId");
                        account.RemoveField("SavedJobId");
                    }
            }

            foreach (Account account in AccountsView.SelectedObjects)
            {
                if (string.IsNullOrEmpty(PlaceID.Text) && string.IsNullOrEmpty(JobID.Text))
                {
                    account.RemoveField("SavedPlaceId");
                    account.RemoveField("SavedJobId");

                    return;
                }

                string PlaceId = CurrentPlaceId;

                if (JobID.Text.Contains("privateServerLinkCode") && Regex.IsMatch(JobID.Text, @"\/games\/(\d+)\/"))
                    PlaceId = Regex.Match(CurrentJobId, @"\/games\/(\d+)\/").Groups[1].Value;

                account.SetField("SavedPlaceId", PlaceId);
                account.SetField("SavedJobId", JobID.Text);
            }
        }

        private void AccountsView_ModelCanDrop(object sender, ModelDropEventArgs e)
        {
            if (e.SourceModels[0] != null && e.SourceModels[0] is Account) e.Effect = DragDropEffects.Move;
        }

        private void AccountsView_ModelDropped(object sender, ModelDropEventArgs e)
        {
            if (e.TargetModel == null || e.SourceModels.Count == 0) return;

            Account droppedOn = e.TargetModel as Account;

            int Index = e.DropTargetIndex;

            for (int i = e.SourceModels.Count; i > 0; i--)
            {
                if (!(e.SourceModels[i - 1] is Account dragged)) continue;

                dragged.Group = droppedOn.Group;

                lock (accountsLock) { AccountsList.Remove(dragged); AccountsList.Insert(Math.Min(Index, AccountsList.Count), dragged); }
            }

            RefreshView(e.SourceModels[e.SourceModels.Count - 1]);
            SaveAccounts();
        }

        private void sortAlphabeticallyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show($"Are you sure you want to sort every account alphabetically?", "KingsRAM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                lock (accountsLock) AccountsList = AccountsList.OrderByDescending(x => x.Username.All(char.IsDigit)).ThenByDescending(x => x.Username.Any(char.IsLetter)).ThenBy(x => x.Username).ToList();

                AccountsView.SetObjects(AccountsList);
                AccountsView.BuildGroups();
            }
        }

        private async void quickLogInToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (!Utilities.YesNoPrompt("Quick Log In", "Only enter codes that you requested\nNever enter another user's code", $"Do you understand?", SaveIfNo: false))
                return;

            if (Clipboard.ContainsText() && Clipboard.GetText() is string ClipCode && ClipCode.Length == 6 && await SelectedAccount.QuickLogIn(ClipCode))
                return;

            string Code = ShowDialog("Code", "Quick Log In");

            if (Code.Length != 6) { MessageBox.Show("Quick Log In codes requires 6 characters", "Quick Log In", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            await SelectedAccount.QuickLogIn(Code);
        }

        private void toggleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AccountsView.ShowGroups = !AccountsView.ShowGroups;

            if (AccountsView.HeaderStyle != ColumnHeaderStyle.None) AccountsView.HeaderStyle = AccountsView.ShowGroups ? ColumnHeaderStyle.Nonclickable : ColumnHeaderStyle.Clickable;

            AccountsView.BuildGroups();
        }

        private void EditTheme_Click(object sender, EventArgs e)
        {
            if (ThemeForm != null && ThemeForm.Visible)
            {
                ThemeForm.Hide();
                return;
            }

            ThemeForm.Show();
        }

        private void LaunchNexus_Click(object sender, EventArgs e)
        {
            if (ControlForm != null)
            {
                ControlForm.Top = Bottom;
                ControlForm.Left = Left;
                ControlForm.Show();
                ControlForm.BringToFront();
            }
            else
            {
                ControlForm = new AccountControl
                {
                    StartPosition = FormStartPosition.Manual,
                    Top = Bottom,
                    Left = Left
                };
                ControlForm.Show();
                ControlForm.ApplyTheme();
            }
        }

        private async Task LaunchAccounts(List<Account> Accounts, long PlaceID, string JobID, bool FollowUser = false, bool VIPServer = false)
        {
            int Delay = General.Exists("AccountJoinDelay") ? General.Get<int>("AccountJoinDelay") : 8;

            bool AsyncJoin = General.Get<bool>("AsyncJoin");
            CancellationTokenSource Token = LauncherToken;

            foreach (Account account in Accounts)
            {
                if (Token.IsCancellationRequested) break;

                long PlaceId = PlaceID;
                string JobId = JobID;

                if (!FollowUser)
                {
                    if (!string.IsNullOrEmpty(account.GetField("SavedPlaceId")) && long.TryParse(account.GetField("SavedPlaceId"), out long PID)) PlaceId = PID;
                    if (!string.IsNullOrEmpty(account.GetField("SavedJobId"))) JobId = account.GetField("SavedJobId");
                }

                await account.JoinServer(PlaceId, JobId, FollowUser, VIPServer);

                if (AsyncJoin)
                {
                    while (!LaunchNext)
                        await Task.Delay(50);
                }
                else
                    await Task.Delay(Delay * 1000);

                LaunchNext = false;
            }

            LaunchNext = false;

            Token.Cancel();
            Token.Dispose();
        }

        public void NextAccount() => LaunchNext = true;
        public void CancelLaunching()
        {
            if (LauncherToken != null && !LauncherToken.IsCancellationRequested)
                LauncherToken.Cancel();
        }

        private void infoToolStripMenuItem1_Click(object sender, EventArgs e) =>
            MessageBox.Show("KingsRAM by KingIcyCreamProjects.\n\nA fork of Roblox Account Manager (created by ic3w0lf22), licensed under the GNU GPLv3.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private void groupsToolStripMenuItem_Click(object sender, EventArgs e) =>
            MessageBox.Show("Groups can be sorted by naming them a number then whatever you want.\nFor example: You can put Group Apple on top by naming it '001 Apple' or '1Apple'.\nThe numbers will be hidden from the name but will be correctly sorted depending on the number.\nAccounts can also be dragged into groups.", "KingsRAM", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private void DonateButton_Click(object sender, EventArgs e) =>
            Process.Start("https://github.com/KingIcyCreamProjects/Roblox-Account-Manager"); // donate page removed; button hidden

        private void ConfigButton_Click(object sender, EventArgs e)
        {
            SettingsForm ??= new SettingsForm();

            if (SettingsForm.Visible)
            {
                SettingsForm.WindowState = FormWindowState.Normal;
                SettingsForm.BringToFront();
            }
            else
                SettingsForm.Show();

            SettingsForm.StartPosition = FormStartPosition.Manual;
            SettingsForm.Top = Top;
            SettingsForm.Left = Right;
        }

        private void HistoryIcon_MouseHover(object sender, EventArgs e) => RGForm.ShowForm();

        private void ShuffleIcon_Click(object sender, EventArgs e)
        {
            ShuffleJobID = !ShuffleJobID;

            if (sender != null)
            {
                General.Set("ShuffleJobId", ShuffleJobID ? "true" : "false");
                IniSettings.Save("RAMSettings.ini");
            }

            if (ShuffleJobID)
                if (ThemeEditor.LightImages)
                    ShuffleIcon.ColorImage(87, 245, 102);
                else
                    ShuffleIcon.ColorImage(57, 152, 22);
            else
            {
                if (BackColor.GetBrightness() < 0.5)
                    ShuffleIcon.ColorImage(255, 255, 255);
                else
                    ShuffleIcon.ColorImage(0, 0, 0);
            }
        }

        private void ShowDetailsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Directory.Exists(Path.Combine(Environment.CurrentDirectory, "AccountDumps")))
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "AccountDumps"));

            foreach (Account Account in AccountsView.SelectedObjects)
            {
                Task.Run(async () =>
                {
                    var UserInfo = await Account.GetUserInfo();
                    double AccountAge = -1;

                    if (DateTime.TryParse(UserInfo["created"].Value<string>(), out DateTime CreationTime))
                        AccountAge = (DateTime.UtcNow - CreationTime).TotalDays;

                    StringBuilder builder = new StringBuilder();

                    builder.AppendLine($"Username: {Account.Username}");
                    builder.AppendLine($"UserId: {Account.UserID}");
                    builder.AppendLine($"Robux: {await Account.GetRobux()}");
                    builder.AppendLine($"Account Age: {(AccountAge >= 0 ? $"{AccountAge:F1}" : "UNKNOWN")}");
                    builder.AppendLine($"Email Status: {await Account.GetEmailJSON()}");
                    builder.AppendLine($"User Info: {UserInfo}");
                    builder.AppendLine($"Other: {await Account.GetMobileInfo()}");
                    builder.AppendLine($"Fields: {JsonConvert.SerializeObject(Account.Fields)}");

                    string FileName = Path.Combine(Environment.CurrentDirectory, "AccountDumps", Account.Username + ".txt");

                    File.WriteAllText(FileName, builder.ToString());

                    Process.Start(FileName);
                });
            }
        }

        CancellationTokenSource PresenceCancellationToken;

        private void AccountsView_Scroll(object sender, ScrollEventArgs e)
        {
            // Cancel any in-flight presence debounce, then bail if presence display is off. The old
            // `token != null || !ShowPresence` guard dereferenced a null token on the first scroll with
            // ShowPresence off (null != null is false, !false is true → entered the block → NRE).
            PresenceCancellationToken?.Cancel();

            if (!General.Get<bool>("ShowPresence")) return;

            PresenceCancellationToken = new CancellationTokenSource();
            var Token = PresenceCancellationToken.Token;

            Task.Run(async () =>
            {
                await Task.Delay(3500); // Wait until the user has stopped scrolling before updating account presence

                if (Token.IsCancellationRequested)
                    return;

                AccountsView.InvokeIfRequired(async () => await UpdatePresence());
            }, PresenceCancellationToken.Token);
        }

        private async Task UpdatePresence()
        {
            if (!General.Get<bool>("ShowPresence")) return;

            List<Account> VisibleAccounts = new List<Account>();

            var Bounds = AccountsView.ClientRectangle;
            int Padding = (int)(AccountsView.HeaderStyle == ColumnHeaderStyle.None ? 4f * Program.Scale : 20f * Program.Scale);

            for (int Y = Padding; Y < Bounds.Height - (Padding / 2); Y += (int)(6f * Program.Scale))
            {
                var Item = AccountsView.GetItemAt(4, Y);

                if (Item != null && AccountsView.GetModelObject(Item.Index) is Account account && !VisibleAccounts.Contains(account))
                    VisibleAccounts.Add(account);
            }

            try { await Presence.UpdatePresence(VisibleAccounts.Select(account => account.UserID).ToArray()); } catch { }
        }

        private void JobID_Click( object sender, EventArgs e )
        {
            JobID.SelectAll(); // Allows quick replacing of the JobID with a click and ctrl-v.
        }

        private void PlaceID_Click( object sender, EventArgs e )
        {
            PlaceID.SelectAll(); // Allows quick replacing of the PlaceID with a click and ctrl-v.
        }
    }
}