using System;
using System.Collections.Generic;
using System.Management;

namespace AgentReaper
{
    internal sealed class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;          // 拡張子なしの小文字
        public string RawName;       // WMI が返す名前
        public string ExePath;
        public string CommandLine;
        public DateTime Created;
        public double CpuSeconds;
        public long WorkingSet;

        public readonly List<ProcInfo> Children = new List<ProcInfo>();
        public ProcInfo Parent;

        public TimeSpan Age { get { return DateTime.Now - Created; } }
    }

    /// <summary>OS のメモリ状態スナップショット。</summary>
    internal sealed class MemSnapshot
    {
        public double TotalGb;
        public double AvailableGb;
        public double FreeAndZeroGb;
        public double StandbyGb;
        public double ModifiedGb;
        public double CommittedGb;
        public double CommitLimitGb;
        public double CompressionGb;
        public int ProcessCount;
        public long HandleCount;

        public override string ToString()
        {
            return string.Format(
                "proc={0} handles={1:N0} free={2:F2}GB avail={3:F2}GB standby={4:F2}GB commit={5:F2}/{6:F2}GB compress={7:F2}GB",
                ProcessCount, HandleCount, FreeAndZeroGb, AvailableGb, StandbyGb,
                CommittedGb, CommitLimitGb, CompressionGb);
        }
    }

    internal static class ProcessScanner
    {
        private const double Gb = 1024.0 * 1024.0 * 1024.0;

        /// <summary>
        /// 全プロセスを 1 回の WMI クエリで取得し、親子リンクを張って返す。
        /// CPU 時間も同じクエリから取るので追加の列挙は不要。
        /// </summary>
        public static Dictionary<int, ProcInfo> Snapshot()
        {
            var map = new Dictionary<int, ProcInfo>();

            const string query =
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, " +
                "CreationDate, KernelModeTime, UserModeTime, WorkingSetSize FROM Win32_Process";

            using (var searcher = new ManagementObjectSearcher(new ObjectQuery(query)))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    try
                    {
                        var p = new ProcInfo();
                        p.Pid = ToInt(mo["ProcessId"]);
                        p.ParentPid = ToInt(mo["ParentProcessId"]);
                        p.RawName = mo["Name"] as string;
                        if (p.RawName == null) p.RawName = "";
                        p.Name = StripExe(p.RawName);
                        p.ExePath = (mo["ExecutablePath"] as string) ?? "";
                        p.CommandLine = (mo["CommandLine"] as string) ?? "";

                        object cd = mo["CreationDate"];
                        p.Created = cd != null
                            ? ManagementDateTimeConverter.ToDateTime(cd.ToString())
                            : DateTime.Now;

                        double kernel = ToDouble(mo["KernelModeTime"]);
                        double user = ToDouble(mo["UserModeTime"]);
                        p.CpuSeconds = (kernel + user) / 10000000.0;   // 100ns 単位 → 秒

                        p.WorkingSet = (long)ToDouble(mo["WorkingSetSize"]);

                        map[p.Pid] = p;
                    }
                    catch
                    {
                        // 走査中に終了したプロセスは黙って飛ばす
                    }
                    finally
                    {
                        mo.Dispose();
                    }
                }
            }

            // 親子リンク。PID 再利用による誤リンクを防ぐため、親の生成時刻が子より後なら親なし扱い。
            foreach (var p in map.Values)
            {
                ProcInfo parent;
                if (map.TryGetValue(p.ParentPid, out parent) && parent.Pid != p.Pid && parent.Created <= p.Created)
                {
                    p.Parent = parent;
                    parent.Children.Add(p);
                }
            }

            return map;
        }

        /// <summary>指定プロセスの全子孫を返す（自分自身は含まない）。深い順に並べる。</summary>
        public static List<ProcInfo> Descendants(ProcInfo root)
        {
            var result = new List<ProcInfo>();
            var stack = new Stack<ProcInfo>();
            var seen = new HashSet<int>();
            stack.Push(root);
            seen.Add(root.Pid);

            while (stack.Count > 0)
            {
                ProcInfo cur = stack.Pop();
                foreach (var c in cur.Children)
                {
                    if (seen.Contains(c.Pid)) continue;
                    seen.Add(c.Pid);
                    result.Add(c);
                    stack.Push(c);
                }
            }
            return result;
        }

        /// <summary>
        /// グループ化の基準となる MCP クライアント側の祖先を返す。見つからなければ null。
        /// cmd.exe / npx のような使い捨てラッパーは透過させる（起動ごとに別 PID になるため、
        /// これを基準にすると全グループが 1 件ずつになり回収判定が機能しない）。
        /// </summary>
        public static ProcInfo ClientAncestor(ProcInfo p)
        {
            ProcInfo cur = p.Parent;
            int guard = 0;
            while (cur != null && guard++ < 40)
            {
                if (Config.TransparentWrappers.Contains(cur.Name)) { cur = cur.Parent; continue; }
                if (Config.ClientAnchors.Contains(cur.Name)) return cur;
                cur = cur.Parent;
            }
            return null;
        }

        public static MemSnapshot Memory()
        {
            var s = new MemSnapshot();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT AvailableBytes, FreeAndZeroPageListBytes, StandbyCacheCoreBytes, " +
                    "StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, ModifiedPageListBytes, " +
                    "CommittedBytes, CommitLimit FROM Win32_PerfRawData_PerfOS_Memory"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        s.AvailableGb = ToDouble(mo["AvailableBytes"]) / Gb;
                        s.FreeAndZeroGb = ToDouble(mo["FreeAndZeroPageListBytes"]) / Gb;
                        s.StandbyGb = (ToDouble(mo["StandbyCacheCoreBytes"])
                                     + ToDouble(mo["StandbyCacheNormalPriorityBytes"])
                                     + ToDouble(mo["StandbyCacheReserveBytes"])) / Gb;
                        s.ModifiedGb = ToDouble(mo["ModifiedPageListBytes"]) / Gb;
                        s.CommittedGb = ToDouble(mo["CommittedBytes"]) / Gb;
                        s.CommitLimitGb = ToDouble(mo["CommitLimit"]) / Gb;
                        mo.Dispose();
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        s.TotalGb = ToDouble(mo["TotalPhysicalMemory"]) / Gb;
                        mo.Dispose();
                        break;
                    }
                }
            }
            catch
            {
                // 取得できなくても動作は続ける
            }

            try
            {
                var all = System.Diagnostics.Process.GetProcesses();
                s.ProcessCount = all.Length;
                long handles = 0;
                foreach (var p in all)
                {
                    try { handles += p.HandleCount; }
                    catch { }
                    try
                    {
                        if (string.Equals(p.ProcessName, "Memory Compression", StringComparison.OrdinalIgnoreCase))
                            s.CompressionGb = p.WorkingSet64 / Gb;
                    }
                    catch { }
                    p.Dispose();
                }
                s.HandleCount = handles;
            }
            catch
            {
            }

            return s;
        }

        private static string StripExe(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n;
        }

        private static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o); }
            catch { return 0; }
        }

        private static double ToDouble(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o); }
            catch { return 0; }
        }
    }
}
