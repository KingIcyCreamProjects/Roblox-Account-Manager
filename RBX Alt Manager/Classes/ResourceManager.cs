using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    // Windows-level per-instance resource control for running MANY simultaneous Roblox clients (KingsRAM v2).
    //
    // Why OS-level and not FastFlags: since Roblox's Sept-2025 FastFlag allowlist, the framerate-cap flag
    // (DFIntTaskSchedulerTargetFps) and most others are silently ignored, and Roblox no longer auto-pauses
    // minimized clients (that experiment was reversed Dec 2025). So the only real CPU/RAM levers are the OS:
    //   * SetPriorityClass — BelowNormal/Idle on background clients (never Realtime/High: they starve the OS).
    //   * EmptyWorkingSet   — trim resident pages of idle/minimized clients (they fault back on next use).
    //   * ProcessorAffinity — optional CCD pinning (keep alts off the V-Cache CCD on X3D chips).
    //
    // Everything is opt-in via [General] Perf* keys and safe for single-client users: with dynamic priority the
    // focused client always stays at Normal, so one lone client is never throttled. Best-effort throughout —
    // Roblox's elevated second process and update races just get skipped, never crash the manager.
    public static class ResourceManager
    {
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;

        private static readonly Regex TrackerRegex = new Regex(@"-b (\d+)", RegexOptions.Compiled);

        private class Sample { public TimeSpan Cpu; public DateTime When; }
        private static readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();

        public struct Stat { public double Cpu; public long RamMB; public bool Minimized; public int Pid; }

        // ---- setting accessors (never throw; safe defaults) ----
        private static string S(string k, string d) { try { var g = AccountManager.General; return g != null && g.Exists(k) ? g.Get(k) : d; } catch { return d; } }
        private static bool B(string k, bool d) { try { var g = AccountManager.General; return g != null && g.Exists(k) ? g.Get<bool>(k) : d; } catch { return d; } }

        public static bool Enabled => B("PerfManage", true);

        private static ProcessPriorityClass BgPriority()
        {
            switch (S("PerfBackgroundPriority", "below").ToLowerInvariant())
            {
                case "idle": return ProcessPriorityClass.Idle;
                case "normal": return ProcessPriorityClass.Normal;
                default: return ProcessPriorityClass.BelowNormal;
            }
        }

        // Affinity mask for the chosen mode, or 0 = leave affinity alone. Derived from the logical-processor
        // count so it works on any CPU; the CCD modes assume a symmetric two-CCD layout — true for the user's
        // 9950X3D where the V-Cache CCD is the low half of the logical processors (0..15) and the high-freq
        // CCD is the high half (16..31). Capped at 32 because the manager is a 32-bit process.
        private static long AffinityMask()
        {
            int n = Math.Min(32, Environment.ProcessorCount);
            if (n < 4) return 0;
            long all = (n >= 32) ? 0xFFFFFFFFL : ((1L << n) - 1);
            int half = n / 2;
            long low = (1L << half) - 1;      // V-Cache CCD on X3D
            long high = all & ~low;           // high-frequency CCD
            switch (S("PerfAffinityMode", "all").ToLowerInvariant())
            {
                case "vcache": return low;
                case "nonvcache": return high;
                default: return 0;
            }
        }

        // Map each launched Roblox client to its owning account via the -b <trackerId> launch arg (the same
        // trick the window-mover and close-client use). tracker -> Process.
        private static Dictionary<string, Process> MapProcesses()
        {
            var map = new Dictionary<string, Process>();
            foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    var m = TrackerRegex.Match(p.GetCommandLine() ?? "");
                    if (m.Success && !map.ContainsKey(m.Groups[1].Value)) map[m.Groups[1].Value] = p;
                }
                catch { }
            }
            return map;
        }

        private static void SetAffinity(Process p, long mask)
        {
            // 32-bit manager => ProcessorAffinity is a 32-bit mask (covers the user's 32 logical processors).
            // unchecked cast so bit 31 (0x80000000) doesn't overflow the int.
            try { p.ProcessorAffinity = (IntPtr)unchecked((int)(mask & 0xFFFFFFFF)); } catch { }
        }

        // Fire-and-forget from JoinServer right after a launch. Waits for the client to appear, then applies
        // affinity + an initial priority + optional auto-minimize/trim. The poll loop takes over dynamic priority.
        public static async Task OnLaunched(Account acc)
        {
            if (!Enabled || acc == null || string.IsNullOrEmpty(acc.BrowserTrackerID)) return;
            try
            {
                DateTime ends = DateTime.Now.AddSeconds(40);
                Process proc = null;
                while (DateTime.Now < ends)
                {
                    await Task.Delay(600);
                    if (MapProcesses().TryGetValue(acc.BrowserTrackerID, out proc) && proc != null && !proc.HasExited) break;
                    proc = null;
                }
                if (proc == null) return;

                long a = AffinityMask(); if (a != 0) SetAffinity(proc, a);
                if (!B("PerfDynamicPriority", true)) { try { proc.PriorityClass = BgPriority(); } catch { } }

                if (B("PerfAutoMinimizeAlts", false))
                    try { if (proc.MainWindowHandle != IntPtr.Zero) ShowWindow(proc.MainWindowHandle, SW_MINIMIZE); } catch { }
                if (B("PerfTrimOnMinimize", false))
                    try { await Task.Delay(2000); EmptyWorkingSet(proc.Handle); } catch { }
            }
            catch { }
        }

        // Sample CPU/RAM per client, apply dynamic priority (focused client -> Normal, others -> background
        // class) and affinity, and return per-tracker stats for the UI. Call on a ~3-4s timer.
        public static Dictionary<string, Stat> Poll()
        {
            var outp = new Dictionary<string, Stat>();
            if (!Enabled) return outp;

            int fgPid = 0; try { GetWindowThreadProcessId(GetForegroundWindow(), out fgPid); } catch { }
            bool dyn = B("PerfDynamicPriority", true);
            ProcessPriorityClass bg = BgPriority();
            long affinity = AffinityMask();
            var alive = new HashSet<int>();

            foreach (var kv in MapProcesses())
            {
                Process p = kv.Value;
                try
                {
                    if (p.HasExited) continue;
                    alive.Add(p.Id);

                    double cpu = 0;
                    TimeSpan now = p.TotalProcessorTime; DateTime t = DateTime.UtcNow;
                    if (Samples.TryGetValue(p.Id, out Sample prev))
                    {
                        double wall = (t - prev.When).TotalMilliseconds;
                        if (wall > 0) cpu = Math.Max(0, (now - prev.Cpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100.0);
                    }
                    Samples[p.Id] = new Sample { Cpu = now, When = t };

                    bool min = false; try { min = p.MainWindowHandle != IntPtr.Zero && IsIconic(p.MainWindowHandle); } catch { }

                    if (dyn) { try { p.PriorityClass = (p.Id == fgPid) ? ProcessPriorityClass.Normal : bg; } catch { } }
                    if (affinity != 0) SetAffinity(p, affinity);

                    outp[kv.Key] = new Stat { Cpu = Math.Round(cpu, 0), RamMB = p.WorkingSet64 / 1024 / 1024, Minimized = min, Pid = p.Id };
                }
                catch { }
            }

            foreach (int id in Samples.Keys.Where(k => !alive.Contains(k)).ToList()) Samples.Remove(id);
            return outp;
        }

        // Manual actions for the Active screen.
        public static int TrimAll()
        {
            int n = 0;
            foreach (var kv in MapProcesses()) { try { if (!kv.Value.HasExited) { EmptyWorkingSet(kv.Value.Handle); n++; } } catch { } }
            return n;
        }

        public static bool TrimOne(Account acc)
        {
            if (acc == null || string.IsNullOrEmpty(acc.BrowserTrackerID)) return false;
            try { if (MapProcesses().TryGetValue(acc.BrowserTrackerID, out Process p) && !p.HasExited) { EmptyWorkingSet(p.Handle); return true; } } catch { }
            return false;
        }

        // Apply priority + affinity (+ optional trim) to every running client now.
        public static int OptimizeAll()
        {
            int n = 0; ProcessPriorityClass bg = BgPriority(); long a = AffinityMask();
            int fgPid = 0; try { GetWindowThreadProcessId(GetForegroundWindow(), out fgPid); } catch { }
            bool dyn = B("PerfDynamicPriority", true); bool trim = B("PerfTrimOnMinimize", false);
            foreach (var kv in MapProcesses())
            {
                Process p = kv.Value;
                try
                {
                    if (p.HasExited) continue;
                    p.PriorityClass = (dyn && p.Id == fgPid) ? ProcessPriorityClass.Normal : bg;
                    if (a != 0) SetAffinity(p, a);
                    if (trim) EmptyWorkingSet(p.Handle);
                    n++;
                }
                catch { }
            }
            return n;
        }
    }
}
