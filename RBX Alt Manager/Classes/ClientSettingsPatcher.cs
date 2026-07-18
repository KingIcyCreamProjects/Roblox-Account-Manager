using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System.IO;

namespace RBX_Alt_Manager.Classes
{
    public static class ClientSettingsPatcher
    {
        public static void PatchSettings()
        {
            DirectoryInfo VersionFolder = null;

            object RegistryValue = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon")?.GetValue("");

            if (RegistryValue != null && RegistryValue is string RobloxPath)
                VersionFolder = Directory.GetParent(RobloxPath);

            if (VersionFolder == null || !VersionFolder.Exists) { Program.Logger.Error("Can't patch ClientAppSettings, folder doesn't exist"); return; }
            if (!VersionFolder.Name.StartsWith("version-")) { Program.Logger.Error("Can't patch ClientAppSettings, folder doesn't start with 'version-'"); return; }
            if (!File.Exists(Path.Combine(VersionFolder.FullName, "RobloxPlayerLauncher.exe"))) { Program.Logger.Error("Can't patch ClientAppSettings, RobloxPlayerBeta.exe not found"); return; }

            DirectoryInfo SettingsFolder = new DirectoryInfo(Path.Combine(VersionFolder.FullName, "ClientSettings"));

            if (!SettingsFolder.Exists) SettingsFolder.Create();

            string CustomFN = AccountManager.General.Exists("CustomClientSettings") ? AccountManager.General.Get<string>("CustomClientSettings") : string.Empty;
            string SettingsFN = Path.Combine(SettingsFolder.FullName, "ClientAppSettings.json");

            if (!string.IsNullOrEmpty(CustomFN) && File.Exists(CustomFN))
            {
                if (File.Exists(SettingsFN)) File.Delete(SettingsFN); // File.Copy throws if the target exists
                File.Copy(CustomFN, SettingsFN);
                return;
            }

            bool UnlockFPS = AccountManager.General.Get<bool>("UnlockFPS");
            bool LowGraphics = AccountManager.General.Exists("PerfLowGraphics") && AccountManager.General.Get<bool>("PerfLowGraphics");

            if (!UnlockFPS && !LowGraphics)
                return;

            // Merge into any existing ClientAppSettings.json rather than clobbering it.
            JObject Settings = File.Exists(SettingsFN) && File.ReadAllText(SettingsFN).TryParseJson(out JObject Existing) ? Existing : new JObject();

            // NOTE: DFIntTaskSchedulerTargetFps is NOT on Roblox's Sept-2025 FastFlag allowlist, so the client
            // now silently ignores it — the FPS-unlock is effectively a no-op post-allowlist (kept for older
            // clients / in case the flag is re-allowed; harmless either way, non-allowlisted flags are ignored).
            if (UnlockFPS)
                Settings["DFIntTaskSchedulerTargetFps"] = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;

            // Low-graphics profile for background alts — the flags that DO survive the allowlist (verified
            // July 2026, see the raw allowlist.json): lower texture quality, kill MSAA, floor the FRM quality
            // level, drop grass geometry, and disable DPI render-scaling. All GPU/VRAM reducers, anti-cheat-safe.
            if (LowGraphics)
            {
                Settings["DFFlagTextureQualityOverrideEnabled"] = true;
                Settings["DFIntTextureQualityOverride"] = 0;
                Settings["FIntDebugForceMSAASamples"] = 0;
                Settings["DFIntDebugFRMQualityLevelOverride"] = 1;
                Settings["FIntFRMMaxGrassDistance"] = 0;
                Settings["FIntFRMMinGrassDistance"] = 0;
                Settings["DFFlagDisableDPIScale"] = true;
            }

            File.WriteAllText(SettingsFN, Settings.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}