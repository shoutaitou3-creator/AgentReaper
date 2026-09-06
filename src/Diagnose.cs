using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AgentReaper
{
    /// <summary>
    /// 環境を測って機械可読で吐き出す。AgentReaper の主インターフェース。
    ///
    /// 想定する使い方は「利用者のコーディングエージェントがこの出力を読んで
    /// signatures.conf と settings.conf を書く」こと。だからここは
    ///   - 判断せず事実を出す（回収候補も「候補」であって指示ではない）
    ///   - コマンドラインは伏字化してから出す（この出力は LLM に渡される）
    /// の 2 点を守る。
    /// </summary>
    internal static class Diagnose
    {
        private const double Gb = 1024.0 * 1024.0 * 1024.0;
        private const double Mb = 1024.0 * 1024.0;

        /// <summary>孤児とみなす経過時間。これ未満は「まだ使われているかもしれない」。</summary>
        private const double OrphanMinutes = 30.0;

        /// <summary>放置された子プロセスの CPU 実測は 0 秒だった。1 秒を仕事の有無の線にする。</summary>
        private const double IdleCpuSeconds = 1.0;

        public static JObj Build(Reaper reaper)
        {
            var report = new JObj();
            var warnings = new JArr();

            report.Set("schema", "agentreaper.diagnose/1");
            report.Set("generatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss K", CultureInfo.InvariantCulture));
            report.Set("baseDir", Config.BaseDir);
            report.Set("isAdmin", Program.IsAdmin());

            report.Set("host", Host());

            MemSnapshot mem = ProcessScanner.Memory();
            Dictionary<int, ProcInfo> map = ProcessScanner.Snapshot();

            // 一番外側の条件から先に出す。遠隔操作されている機体では、人間が感じる速さは
            // この機体の速さではなく回線で決まる。それを知らずに下の数値を読むと必ず読み違える。
            report.Set("remoteAccess", RemoteAccess(map, warnings));

            report.Set("memory", Memory(mem, warnings));
            report.Set("physicalMemory", PhysicalMemory(warnings));
            report.Set("graphics", Graphics(mem, warnings));

            report.Set("processes", Processes(map, mem));
            report.Set("topGroups", TopGroups(map));
            report.Set("reapCandidates", ReapCandidates(map));

            report.Set("signatures", SignatureState(reaper, warnings));
            report.Set("settings", SettingsState(reaper.Settings, warnings));
            report.Set("install", InstallState());

            report.Set("warnings", warnings);
            return report;
        }

        // ---------------- host ----------------

        private static JObj Host()
        {
            var o = new JObj();
            o.Set("machine", Environment.MachineName);
            o.Set("os", Environment.OSVersion.VersionString);
            o.Set("is64BitOs", Environment.Is64BitOperatingSystem);

            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        o.Set("cpu", Str(mo["Name"]));
                        o.Set("cores", ToInt(mo["NumberOfCores"]));
                        o.Set("logicalProcessors", ToInt(mo["NumberOfLogicalProcessors"]));
                        o.Set("maxClockMhz", ToInt(mo["MaxClockSpeed"]));
                        mo.Dispose();
                        break;
                    }
                }

                using (var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        object bt = mo["LastBootUpTime"];
                        if (bt != null)
                        {
                            DateTime boot = ManagementDateTimeConverter.ToDateTime(bt.ToString());
                            o.Set("uptimeHours", (DateTime.Now - boot).TotalHours);
                        }
                        mo.Dispose();
                        break;
                    }
                }
            }
            catch
            {
            }

            return o;
        }

        // ---------------- memory ----------------

        private static JObj Memory(MemSnapshot m, JArr warnings)
        {
            var o = new JObj();
            o.Set("totalGb", m.TotalGb);
            o.Set("availableGb", m.AvailableGb);
            o.Set("freeAndZeroGb", m.FreeAndZeroGb);
            o.Set("standbyGb", m.StandbyGb);
            o.Set("modifiedGb", m.ModifiedGb);
            o.Set("committedGb", m.CommittedGb);
            o.Set("commitLimitGb", m.CommitLimitGb);
            o.Set("compressionGb", m.CompressionGb);
            o.Set("note",
                "availableGb = freeAndZeroGb + standbyGb。タスクマネージャーの「利用可能」は availableGb で、"
                + "これは空きではない。スタンバイは他プロセスが使っていたページで、再利用の前にゼロ化が要る。"
                + "体感の重さと相関するのは freeAndZeroGb（実空き）のほう。");

            if (m.FreeAndZeroGb < 1.0)
                warnings.Add("実空きページ(freeAndZeroGb)が " + F(m.FreeAndZeroGb) + " GB しかありません。"
                    + "利用可能が大きくても、ページを新規に確保するたびにゼロ化待ちが入ります。"
                    + "GPU が同期的にメモリを要求した瞬間に画面が止まる形の「重さ」はここで起きます。");
            else if (m.FreeAndZeroGb < 3.0)
                warnings.Add("実空きページ(freeAndZeroGb)が " + F(m.FreeAndZeroGb) + " GB と少なめです。");

            if (m.CommitLimitGb > 0 && m.CommittedGb / m.CommitLimitGb > 0.9)
                warnings.Add("コミットが上限の 90% を超えています（" + F(m.CommittedGb) + " / " + F(m.CommitLimitGb) + " GB）。");

            return o;
        }

        private static JObj PhysicalMemory(JArr warnings)
        {
            var o = new JObj();
            var modules = new JArr();
            int slots = 0;

            try
            {
                using (var s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results) { slots += ToInt(mo["MemoryDevices"]); mo.Dispose(); }
                }

                using (var s = new ManagementObjectSearcher(
                    "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        var m = new JObj();
                        m.Set("locator", Str(mo["DeviceLocator"]));
                        m.Set("bank", Str(mo["BankLabel"]));
                        m.Set("capacityGb", ToDouble(mo["Capacity"]) / Gb);
                        m.Set("speedMts", ToInt(mo["Speed"]));
                        m.Set("configuredMts", ToInt(mo["ConfiguredClockSpeed"]));
                        modules.Add(m);
                        mo.Dispose();
                    }
                }
            }
            catch
            {
            }

            o.Set("slots", slots);
            o.Set("populated", modules.Count);
            o.Set("modules", modules);

            if (modules.Count == 1 && slots > 1)
            {
                o.Set("singleChannel", true);
                warnings.Add("メモリモジュールが " + slots + " スロット中 1 枚だけです（シングルチャネル）。"
                    + "内蔵GPUは同じメモリを共有するため、帯域が半分になると"
                    + "CPU・GPU・メモリの使用率に余裕があっても描画が詰まって「重い」と感じます。"
                    + "2 枚差しにすると効きます。");
            }
            else
            {
                o.Set("singleChannel", false);
            }

            return o;
        }

        // ---------------- remote access ----------------

        // 画面を遠隔へ配信するツール。動いている間、人間が体験する速さはこの機体の速さではなく、
        // 上り回線の帯域・遅延・ゆらぎで決まる。ローカルの数値がすべて健全でも操作は重く感じる。
        private static readonly Dictionary<string, string> RemoteTools =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "anydesk", "AnyDesk" },
                { "teamviewer", "TeamViewer" },
                { "tv_w32", "TeamViewer" },
                { "tv_x64", "TeamViewer" },
                { "rustdesk", "RustDesk" },
                { "todesk", "ToDesk" },
                { "splashtop", "Splashtop" },
                { "parsec", "Parsec" },
                { "remoting_host", "Chrome Remote Desktop" },
                { "winvnc", "VNC" },
                { "tvnserver", "TightVNC" },
                { "vncserver", "VNC" },
                { "dwagent", "DWService" },
                { "sunloginclient", "SunLogin" },
            };

        private static JObj RemoteAccess(Dictionary<int, ProcInfo> map, JArr warnings)
        {
            var o = new JObj();
            var found = new JArr();
            var seen = new List<string>();

            foreach (ProcInfo p in map.Values)
            {
                if (p.Name == null) continue;
                foreach (var t in RemoteTools)
                {
                    if (p.Name.IndexOf(t.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (seen.Contains(t.Value)) break;
                    seen.Add(t.Value);
                    found.Add(t.Value);
                    break;
                }
            }

            bool rdp = Native.IsRdpSession();
            o.Set("rdpSession", rdp);
            o.Set("tools", found);

            if (seen.Count > 0 || rdp)
            {
                string what = rdp ? "リモートデスクトップ" : string.Join(" / ", seen.ToArray());
                warnings.Add("画面を遠隔へ配信する仕組み（" + what + "）が動いています。"
                    + "この状態で人間が感じる速さは、この機体の速さではなく上り回線の帯域・遅延・ゆらぎで決まります。"
                    + "以下の memory・graphics・processes がすべて健全でも、上りが細ければ操作は重く感じます。"
                    + "画面が広いほど送る量も増えるので graphics.displays と直接効き合います。"
                    + "先に (1) この機体で上り速度と遅延を測る (2) 有線か無線かを確認する "
                    + "(3) 同じ構成で問題の出ていない機体があれば両方で --diagnose を採って差分を見る。"
                    + "ローカルの数値だけで結論を出さないこと。");
            }

            o.Set("note",
                "ここが空でないなら、体感の重さをこの機体の中だけで説明しようとしない。"
                + "上り回線が飽和している遠隔操作は、CPU・メモリ・GPU がすべて余っている状態で重くなる。");
            return o;
        }

        // ---------------- graphics ----------------

        private static JObj Graphics(MemSnapshot mem, JArr warnings)
        {
            var o = new JObj();
            var adapters = new JArr();

            // 表示名とドライバーは WMI から
            var names = new List<KeyValuePair<string, string>>();
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        names.Add(new KeyValuePair<string, string>(Str(mo["Name"]), Str(mo["DriverVersion"])));
                        mo.Dispose();
                    }
                }
            }
            catch
            {
            }

            // 専用VRAM は Win32_VideoController.AdapterRAM が 4GB 超で壊れるので、
            // レジストリの HardwareInformation.qwMemorySize を正とする。
            const string classKey =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            var vram = new List<KeyValuePair<string, double>>();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(classKey))
                {
                    if (root != null)
                    {
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;   // 0000, 0001, ...
                            using (RegistryKey k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                object qw = k.GetValue("HardwareInformation.qwMemorySize");
                                if (qw == null) continue;

                                double bytes = 0;
                                if (qw is long) bytes = (long)qw;
                                else if (qw is int) bytes = (int)qw;
                                else if (qw is byte[]) bytes = BitConverter.ToInt64(Pad8((byte[])qw), 0);
                                if (bytes <= 0) continue;

                                vram.Add(new KeyValuePair<string, double>(
                                    Convert.ToString(k.GetValue("DriverDesc"), CultureInfo.InvariantCulture),
                                    bytes / Mb));
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            foreach (var v in vram)
            {
                var a = new JObj();
                a.Set("name", v.Key);
                a.Set("dedicatedVramMb", v.Value);

                string driver = null;
                foreach (var n in names)
                {
                    if (n.Key != null && v.Key != null &&
                        n.Key.IndexOf(v.Key, StringComparison.OrdinalIgnoreCase) >= 0) { driver = n.Value; break; }
                }
                a.Set("driverVersion", driver);
                adapters.Add(a);

                // 内蔵GPUで専用VRAM が小さいと、あふれた分が共有システムメモリへ退避する。
                // 共有分は実空きと同じページプールから取るので、メモリ全体の余裕を食う。
                if (v.Value > 0 && v.Value <= 2048)
                    warnings.Add("グラフィックス \"" + v.Key + "\" の専用VRAM が "
                        + F(v.Value) + " MB しかありません。"
                        + "あふれた分は共有システムメモリへ退避し、実空きページと同じプールを消費します。"
                        + "UEFI/BIOS または GPU ソフトウェアで専用VRAM を増やせる機種なら、"
                        + "それが効く場合があります（要再起動）。");
            }

            o.Set("adapters", adapters);

            // ---- 表示中の画面 ----
            //
            // 内蔵GPUは画面の合成にシステムメモリ帯域を使う。ドライバーで増やした仮想ディスプレイも
            // 実画面と同じ合成対象なので、物理的に何も繋がっていなくても帯域と VRAM を食う。
            // しかもこの消費は CPU 使用率にも GPU 使用率にも現れない。
            // 「どこも数%なのに重い」の説明がつかない時は、まずここの枚数と画素数を見る。
            var displays = new JArr();
            double totalMinGbPerSec = 0;
            int virtualCount = 0;
            List<Native.DisplayInfo> ds = Native.Displays();
            foreach (Native.DisplayInfo d in ds)
            {
                // 合成に最低限要る帯域。1画素4バイトを、1リフレッシュにつき読みと書きで 2 回。
                // 実際にはウィンドウごとの中間バッファがあるのでこれより増える（下限であって上限ではない）。
                double gbPerSec = d.Pixels * 4.0 * d.RefreshHz * 2 / 1024 / 1024 / 1024;
                totalMinGbPerSec += gbPerSec;

                // 専用VRAM を報告したアダプターに紐づかない画面は、仮想ディスプレイドライバーである
                // ことが多い。推測であって断定ではない。
                bool likelyVirtual = d.IsMirroring;
                if (!likelyVirtual)
                {
                    likelyVirtual = true;
                    foreach (var v in vram)
                    {
                        if (v.Key != null && d.Adapter != null &&
                            (d.Adapter.IndexOf(v.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             v.Key.IndexOf(d.Adapter, StringComparison.OrdinalIgnoreCase) >= 0))
                        { likelyVirtual = false; break; }
                    }
                }
                if (likelyVirtual) virtualCount++;

                var jd = new JObj();
                jd.Set("device", d.Device);
                jd.Set("adapter", d.Adapter);
                jd.Set("resolution", d.Width.ToString(CultureInfo.InvariantCulture) + "x"
                    + d.Height.ToString(CultureInfo.InvariantCulture));
                jd.Set("refreshHz", d.RefreshHz);
                jd.Set("bitsPerPixel", d.BitsPerPixel);
                jd.Set("megapixels", Math.Round(d.Pixels / 1000000.0, 2));
                jd.Set("minCompositeGbPerSec", Math.Round(gbPerSec, 2));
                jd.Set("likelyVirtual", likelyVirtual);
                displays.Add(jd);
            }
            o.Set("displayCount", ds.Count);
            o.Set("likelyVirtualDisplayCount", virtualCount);
            o.Set("minCompositeGbPerSec", Math.Round(totalMinGbPerSec, 2));
            o.Set("displays", displays);

            if (virtualCount > 0)
                warnings.Add("仮想ディスプレイとみられる画面が " + virtualCount + " 枚あります（全 "
                    + ds.Count + " 枚）。仮想画面も実画面と同じように内蔵GPUが合成するので、"
                    + "何も表示していなくても帯域を消費します。CPU/GPU の使用率には出ません。"
                    + "重さの切り分けとして、仮想画面を一時的に外して体感を比べること。"
                    + "（likelyVirtual は「専用VRAM を報告したアダプターに紐づかない画面」という推測。断定ではない）");

            o.Set("note",
                "dedicatedVramMb はレジストリの HardwareInformation.qwMemorySize。"
                + "Win32_VideoController.AdapterRAM は 4GB 超で壊れた値を返すので使っていない。"
                + "内蔵GPUの共有分は実空きと同じページプールから取る。"
                + "minCompositeGbPerSec は画素数×4バイト×リフレッシュ×2 の下限見積り。"
                + "メモリがシングルチャネルだと使える帯域が半分になるので physicalMemory も併せて見ること。");
            return o;
        }

        private static byte[] Pad8(byte[] src)
        {
            var b = new byte[8];
            Array.Copy(src, b, Math.Min(8, src.Length));
            return b;
        }

        // ---------------- processes ----------------

        private static JObj Processes(Dictionary<int, ProcInfo> map, MemSnapshot mem)
        {
            var o = new JObj();
            o.Set("total", map.Count);
            o.Set("handles", mem.HandleCount);
            o.Set("note",
                "プロセス数そのものは体感の重さと相関しない。実測では 726 プロセスでコマ落ち 11%、"
                + "522 プロセスでも 0% だった。数ではなく、下の reapCandidates と memory を見ること。");
            return o;
        }

        private static JArr TopGroups(Dictionary<int, ProcInfo> map)
        {
            var arr = new JArr();
            var groups = map.Values
                .Where(p => !Config.Protected.Contains(p.Name))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(20);

            foreach (var g in groups)
            {
                var o = new JObj();
                o.Set("name", g.Key);
                o.Set("count", g.Count());
                o.Set("workingSetMb", g.Sum(p => p.WorkingSet) / Mb);
                o.Set("orphanCount", g.Count(p => ProcessScanner.ClientAncestor(p) == null));
                o.Set("oldestMinutes", g.Max(p => p.Age.TotalMinutes));
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>
        /// スクリプトランタイム。実行ファイルの置き場所からは何も分からないので、
        /// 下の場所による除外を適用してはいけない。
        /// node の既定インストール先は C:\Program Files\nodejs\node.exe であり、
        /// ここを場所で切ると node 製の MCP サーバーが 1 つも候補に出なくなる。
        /// 何をしているかはコマンドラインの側にある。
        /// </summary>
        private static readonly HashSet<string> ScriptRuntimes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "node_repl", "deno", "bun", "npx", "npm",
            "python", "python3", "pythonw", "uv", "uvx", "pipx",
            "java", "javaw", "dotnet", "php", "ruby", "perl", "wscript", "cscript"
        };

        /// <summary>
        /// インストール済みソフトの常駐部分は回収候補にしない。
        /// Program Files / Windows 配下で動いているものは、エージェントが残した子プロセスではなく
        /// そのソフト自身の設計であり、止めれば壊れる。実行ファイルの場所が読めないものも同様に外す
        /// （見えていないものを候補として提示してはいけない）。
        /// </summary>
        private static bool IsVendorResident(ProcInfo p)
        {
            if (string.IsNullOrEmpty(p.ExePath)) return true;
            if (ScriptRuntimes.Contains(p.Name)) return false;

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };

            foreach (string root in roots)
            {
                if (!string.IsNullOrEmpty(root) &&
                    p.ExePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Windows サービスとして動いている PID。候補から必ず外す。</summary>
        private static HashSet<int> ServicePids()
        {
            var pids = new HashSet<int>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE State='Running'"))
                using (var results = s.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        int pid = ToInt(mo["ProcessId"]);
                        if (pid > 0) pids.Add(pid);
                        mo.Dispose();
                    }
                }
            }
            catch
            {
            }
            return pids;
        }

        /// <summary>
        /// 回収候補。「孤児 + 一定時間放置 + CPU をほぼ使っていない」が 2 個以上あるものだけ。
        ///
        /// これは提案であって指示ではない。実際に何を回収するかを決めるのは利用者側。
        /// 提案である以上、外したときの被害が大きいものは最初から出さない:
        ///   - Windows サービス
        ///   - Program Files / Windows 配下の常駐プロセス
        ///   - 実行ファイルの場所が読めないもの
        /// これで postgres・検索インデクサ・各社のクラッシュハンドラは候補に出なくなる。
        /// 探しているのはエージェントが起動して片付け損ねた子プロセスであって、
        /// インストール済みソフトの常駐部分ではない。
        /// </summary>
        private static JArr ReapCandidates(Dictionary<int, ProcInfo> map)
        {
            var arr = new JArr();
            HashSet<int> services = ServicePids();

            var stale = map.Values.Where(p =>
                !Config.Protected.Contains(p.Name) &&
                !services.Contains(p.Pid) &&
                !IsVendorResident(p) &&
                ProcessScanner.ClientAncestor(p) == null &&
                p.Age.TotalMinutes > OrphanMinutes &&
                p.CpuSeconds < IdleCpuSeconds).ToList();

            var groups = stale
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Sum(p => p.WorkingSet))
                .Take(15);

            foreach (var g in groups)
            {
                var o = new JObj();
                o.Set("name", g.Key);
                o.Set("count", g.Count());
                o.Set("workingSetMb", g.Sum(p => p.WorkingSet) / Mb);
                o.Set("oldestMinutes", g.Max(p => p.Age.TotalMinutes));
                o.Set("totalCpuSeconds", g.Sum(p => p.CpuSeconds));

                var samples = new JArr();
                foreach (ProcInfo p in g.OrderByDescending(x => x.Age).Take(2))
                    samples.Add(Redact(p.CommandLine));
                o.Set("sampleCommandLines", samples);

                o.Set("exePath", g.Select(p => p.ExePath).FirstOrDefault(x => !string.IsNullOrEmpty(x)));
                o.Set("hint",
                    "sampleCommandLines から、このサーバーだけを特定できる文字列を選んで "
                    + "signatures.conf のパターンにする。ランタイム名やフォルダ名は使わない。");
                arr.Add(o);
            }

            return arr;
        }

        // ---------------- 設定・シグネチャ・導入状況 ----------------

        private static JObj SignatureState(Reaper reaper, JArr warnings)
        {
            var o = new JObj();

            // 読み込みを通っただけの一覧。走査で無効化されたものも入るので、
            // 使われているかどうかは enabled で判断する（active という名前だと誤解を招く）。
            var disabledNames = new HashSet<string>(
                reaper.DisabledSignatures.Select(d =>
                {
                    int sep = d.IndexOf(" — ", StringComparison.Ordinal);
                    return sep > 0 ? d.Substring(0, sep) : d;
                }), StringComparer.Ordinal);

            var loaded = new JArr();
            foreach (Signature s in reaper.Signatures)
            {
                var e = new JObj();
                e.Set("name", s.Name);
                e.Set("field", s.Field);
                e.Set("pattern", s.Pattern);
                e.Set("keepNewestPerClient", s.KeepNewestPerClient);
                e.Set("minAgeMinutes", s.MinAgeMinutes);
                int matched;
                e.Set("matchedThisScan", reaper.MatchCounts.TryGetValue(s.Name, out matched) ? matched : 0);
                e.Set("enabled", !disabledNames.Contains(s.Name));
                loaded.Add(e);
            }
            o.Set("loaded", loaded);

            var rejected = new JArr();
            foreach (string r in Config.RejectedSignatures) { rejected.Add(r); warnings.Add("シグネチャ拒否: " + r); }
            o.Set("rejectedAtLoad", rejected);

            var disabled = new JArr();
            foreach (string d in reaper.DisabledSignatures) { disabled.Add(d); warnings.Add("シグネチャ無効化: " + d); }
            o.Set("disabledThisScan", disabled);

            // シグネチャが 1 件も無いときに approved = true と出すと「承認済み」に読めてしまう。
            // 実態は「承認する対象が無い＝回収も起きない」なので、そう書く。
            bool hasSignatures = reaper.Signatures.Count > 0;
            bool approved = hasSignatures && Guard.IsApproved(reaper.Signatures);

            var approval = new JObj();
            approval.Set("approved", approved);
            approval.Set("fingerprint", hasSignatures ? Guard.Fingerprint(reaper.Signatures) : null);
            approval.Set("file", Guard.ApprovalPath);
            approval.Set("reason", hasSignatures
                ? (approved ? "このシグネチャ一式はドライランで確認済み" : "いまの signatures.conf は未承認")
                : "signatures.conf が空なので承認する対象が無い（回収も起きない）");
            approval.Set("note",
                "承認されるまで、設定が dryRun = false でも AgentReaper はプロセスを終了しない。"
                + "--dry-run で結果を確認してから --approve を実行する。");
            o.Set("approval", approval);

            if (reaper.Signatures.Count == 0)
                warnings.Add("signatures.conf に有効なシグネチャがありません。回収は一切行われません（診断だけ動きます）。");
            else if (!approved)
                warnings.Add("いまの signatures.conf は未承認です。--dry-run で確認してから --approve を実行するまで、"
                    + "回収は行われません。");

            var guard = new JObj();
            guard.Set("minPatternLength", Guard.MinPatternLength);
            guard.Set("maxDistinctExeNames", Guard.MaxDistinctExeNames);
            guard.Set("maxMatchShare", Guard.MaxMatchShare);
            guard.Set("workingProcessCpuSeconds", Guard.WorkingProcessCpuSeconds);
            guard.Set("note", "この閾値は設定ファイルから変更できない。");
            o.Set("guard", guard);

            return o;
        }

        private static JObj SettingsState(Settings s, JArr warnings)
        {
            var o = new JObj();
            o.Set("scanIntervalSeconds", s.ScanIntervalSeconds);
            o.Set("autoReap", s.AutoReap);
            o.Set("dryRun", s.DryRun);
            o.Set("idleScansRequired", s.IdleScansRequired);
            o.Set("maxKillPerPass", s.MaxKillPerPass);
            o.Set("autoLighten", s.AutoLighten);
            o.Set("autoLightenFreeGb", s.AutoLightenFreeGb);
            o.Set("autoLightenFreeGbSource", s.AutoLightenFreeGbExplicit
                ? "settings.conf で明示"
                : "搭載メモリ量から自動算出（搭載量の 4%・下限1.0・上限8.0）");
            o.Set("autoLightenCooldownSeconds", s.AutoLightenCooldownSeconds);
            o.Set("flushModifiedList", s.FlushModifiedList);
            o.Set("purgeLowPriorityStandby", s.PurgeLowPriorityStandby);
            o.Set("purgeAllStandby", s.PurgeAllStandby);
            o.Set("emptyAllWorkingSets", s.EmptyAllWorkingSets);
            o.Set("warnFreeGb", s.WarnFreeGb);
            o.Set("warnFreeGbSource", s.WarnFreeGbExplicit
                ? "settings.conf で明示"
                : "搭載メモリ量から自動算出（搭載量の 3%・下限0.75・上限6.0）");
            o.Set("thresholdBasisTotalGb", s.TotalPhysicalGb);
            o.Set("excludePids", string.Join(",", s.ExcludePids.Select(p =>
                p.ToString(CultureInfo.InvariantCulture)).ToArray()));

            if (s.EmptyAllWorkingSets)
                warnings.Add("emptyAllWorkingSets = true です。全プロセスのワーキングセットを切り詰めると、"
                    + "次に触った瞬間にページフォルトで戻るため体感が悪化しやすい設定です。");

            // 明示されたしきい値が搭載量に対して不自然な場合は指摘する。
            // 切り詰めは Config.ClampThresholds が別途行うが、切り詰めに掛からない範囲でも
            // 「平常時から発動し続ける」設定は成立してしまうので、ここで気づけるようにする。
            if (s.TotalPhysicalGb > 0 && s.AutoLightenFreeGbExplicit
                && s.AutoLightenFreeGb > s.TotalPhysicalGb * 0.10)
                warnings.Add("autoLightenFreeGb = " + F(s.AutoLightenFreeGb)
                    + " は搭載 " + F(s.TotalPhysicalGb) + "GB に対して高めです。"
                    + "Windows は空きを遊ばせずスタンバイへ回すため、実空きは平常時から小さく保たれます。"
                    + "高すぎるしきい値は平常時から発動し続け、ファイルキャッシュを壊して逆に遅くなります。"
                    + "この行を消せば搭載量から自動算出されます。");

            return o;
        }

        private static JObj InstallState()
        {
            var o = new JObj();

            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "AgentReaper.lnk");
            o.Set("autostartShortcut", link);
            o.Set("autostartRegistered", File.Exists(link));
            o.Set("elevatedTaskRegistered", Elevation.TaskRegistered());
            o.Set("elevatedTaskName", Elevation.TaskName);
            return o;
        }

        // ---------------- 伏字化 ----------------

        private static readonly Regex NamedSecret = new Regex(
            @"(?i)\b(token|api[_-]?key|apikey|secret|password|passwd|pwd|bearer|auth|credential)s?\b\s*[=:]?\s*(\S+)",
            RegexOptions.Compiled);

        private static readonly Regex LongToken = new Regex(
            @"\b[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled);

        /// <summary>
        /// コマンドラインをそのまま出さない。この JSON は LLM に渡される前提なので、
        /// 引数に入りうる資格情報とユーザー名を落としてから出す。
        /// </summary>
        public static string Redact(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";

            string s = cmd;
            s = NamedSecret.Replace(s, "$1=<redacted>");
            s = LongToken.Replace(s, "<redacted>");

            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile))
                    s = Regex.Replace(s, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
            catch
            {
            }

            if (s.Length > 300) s = s.Substring(0, 300) + " …";
            return s;
        }

        // ---------------- 小物 ----------------

        private static string F(double v)
        {
            return v.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string Str(object o)
        {
            return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        private static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static double ToDouble(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
