using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentReaper
{
    /// <summary>回収の判定単位。シグネチャに一致した最上位プロセスとその子孫ツリー。</summary>
    internal sealed class ReapTarget
    {
        public ProcInfo Root;
        public Signature Sig;
        public List<ProcInfo> Tree;        // Root を含む全メンバー
        public ProcInfo Client;            // MCP クライアント側の祖先（null あり）
        public string Reason;              // 残す/殺す理由

        public int Count { get { return Tree.Count; } }
        public long WorkingSet { get { return Tree.Sum(p => p.WorkingSet); } }
        public string ClientLabel
        {
            get { return Client == null ? "(不明)" : string.Format("{0}#{1}", Client.RawName, Client.Pid); }
        }
    }

    internal sealed class ReapPlan
    {
        public readonly List<ReapTarget> Kill = new List<ReapTarget>();
        public readonly List<ReapTarget> Keep = new List<ReapTarget>();

        public int KillProcessCount { get { return Kill.Sum(t => t.Count); } }
        public long KillWorkingSet { get { return Kill.Sum(t => t.WorkingSet); } }
    }

    internal sealed class ReapResult
    {
        public int TargetsKilled;
        public int ProcessesKilled;
        public int Failed;
        public MemSnapshot Before;
        public MemSnapshot After;

        /// <summary>承認ゲートに阻まれてドライランへ落ちた場合 true。</summary>
        public bool BlockedByApproval;
    }

    /// <summary>
    /// リークしたエージェント子プロセスを検出して回収する中核。
    ///
    /// 「生きている」判定は OR で評価し、1 つでも当てはまれば残す:
    ///   - 起動中のクライアントを祖先に持つ（順位・経過時間・CPU に関係なく保護）
    ///   - クライアントごとに最新 N 個
    ///   - 生成から MinAgeMinutes 以内
    ///   - 直近スキャンで CPU 時間が増えている（連続 IdleScansRequired 回の無活動を要求）
    ///   - 除外 PID を含む
    ///   - ツリーに保護対象プロセスを含む
    /// </summary>
    internal sealed class Reaper
    {
        private const double CpuBusyThresholdSeconds = 0.05;

        private readonly Dictionary<int, double> _lastCpu = new Dictionary<int, double>();
        private readonly Dictionary<int, int> _idleScans = new Dictionary<int, int>();

        public List<Signature> Signatures = new List<Signature>();
        public Settings Settings = new Settings();

        /// <summary>直近のスキャン結果（トレイ表示用）。</summary>
        public ReapPlan LastPlan { get; private set; }
        public MemSnapshot LastMemory { get; private set; }

        /// <summary>
        /// 直近の走査で Guard の動的検査に落ちたシグネチャ（名前 — 理由）。
        /// 広すぎるパターンは、その走査では一切使われない。
        /// </summary>
        public readonly List<string> DisabledSignatures = new List<string>();

        /// <summary>同じ拒否をスキャンのたびにログへ書かないための記録。</summary>
        private readonly HashSet<string> _loggedRejects = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>直近の走査でシグネチャごとに一致したプロセス数（診断用）。</summary>
        public readonly Dictionary<string, int> MatchCounts = new Dictionary<string, int>();

        /// <summary>プロセスを走査し、回収計画を立てる。実際の終了はしない。</summary>
        public ReapPlan Plan()
        {
            Dictionary<int, ProcInfo> map = ProcessScanner.Snapshot();
            UpdateCpuHistory(map);

            var plan = new ReapPlan();

            // 1) シグネチャごとに一致プロセスを集める。
            //    1 プロセスは最初に一致したシグネチャにだけ属する（先勝ち）。
            var matchesOf = new List<KeyValuePair<Signature, List<ProcInfo>>>();
            foreach (Signature sig in Signatures)
                matchesOf.Add(new KeyValuePair<Signature, List<ProcInfo>>(sig, new List<ProcInfo>()));

            foreach (var p in map.Values)
            {
                if (Config.Protected.Contains(p.Name)) continue;
                foreach (var entry in matchesOf)
                {
                    if (entry.Key.Matches(p)) { entry.Value.Add(p); break; }
                }
            }

            // 1b) 走査ごとの Guard。広すぎるパターン・仕事中のプロセスを掴んだパターンは
            //     この走査で丸ごと無効にする。設定では無効化できない。
            DisabledSignatures.Clear();
            MatchCounts.Clear();
            var sigOf = new Dictionary<int, Signature>();

            foreach (var entry in matchesOf)
            {
                MatchCounts[entry.Key.Name] = entry.Value.Count;

                string reject = Guard.RejectDynamic(entry.Key, entry.Value, map.Count);
                if (reject != null)
                {
                    string msg = entry.Key.Name + " — " + reject;
                    DisabledSignatures.Add(msg);
                    if (_loggedRejects.Add(msg)) Log.Write("シグネチャを無効化: " + msg);
                    continue;
                }

                foreach (ProcInfo p in entry.Value) sigOf[p.Pid] = entry.Key;
            }

            // 2) 候補のうち、親が候補でないものをツリーの根とする
            var targets = new List<ReapTarget>();
            foreach (var kv in sigOf)
            {
                ProcInfo p = map[kv.Key];
                if (p.Parent != null && sigOf.ContainsKey(p.Parent.Pid)) continue;

                var t = new ReapTarget();
                t.Root = p;
                t.Sig = kv.Value;
                t.Tree = new List<ProcInfo> { p };
                t.Tree.AddRange(ProcessScanner.Descendants(p));
                t.Client = ProcessScanner.ClientAncestor(p);
                targets.Add(t);
            }

            // 3) (クライアント, シグネチャ) ごとに新しい順で並べ、上位 N を温存
            var groups = targets.GroupBy(t => string.Format("{0}|{1}",
                t.Client == null ? 0 : t.Client.Pid, t.Sig.Name));

            foreach (var g in groups)
            {
                var ordered = g.OrderByDescending(t => t.Root.Created).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    ReapTarget t = ordered[i];
                    string keepReason = WhyKeep(t, i);
                    if (keepReason != null)
                    {
                        t.Reason = keepReason;
                        plan.Keep.Add(t);
                    }
                    else
                    {
                        t.Reason = string.Format("放置 {0:F1} 時間 / CPU 無活動 / {1} 個目",
                            t.Root.Age.TotalHours, i + 1);
                        plan.Kill.Add(t);
                    }
                }
            }

            // 4) 古い順に回収し、暴走ガードとして 1 回の上限を設ける
            plan.Kill.Sort((a, b) => a.Root.Created.CompareTo(b.Root.Created));
            if (plan.Kill.Count > Settings.MaxKillPerPass)
                plan.Kill.RemoveRange(Settings.MaxKillPerPass, plan.Kill.Count - Settings.MaxKillPerPass);

            LastPlan = plan;
            return plan;
        }

        /// <summary>残す理由を返す。殺してよい場合は null。</summary>
        private string WhyKeep(ReapTarget t, int rankNewestFirst)
        {
            // Client はスナップショット内の実在する祖先。idle は接続終了の証拠にならない。
            if (t.Client != null)
                return string.Format("起動中クライアント {0} の配下", t.ClientLabel);

            if (rankNewestFirst < t.Sig.KeepNewestPerClient)
                return string.Format("最新 {0} 個以内（現用の可能性）", t.Sig.KeepNewestPerClient);

            if (t.Root.Age.TotalMinutes < t.Sig.MinAgeMinutes)
                return string.Format("生成から {0:F0} 分（猶予 {1} 分）", t.Root.Age.TotalMinutes, t.Sig.MinAgeMinutes);

            foreach (ProcInfo p in t.Tree)
            {
                if (Config.Protected.Contains(p.Name))
                    return string.Format("保護対象 {0} を含む", p.RawName);

                if (Settings.ExcludePids.Contains(p.Pid))
                    return string.Format("除外 PID {0} を含む", p.Pid);

                int idle;
                if (!_idleScans.TryGetValue(p.Pid, out idle) || idle < Settings.IdleScansRequired)
                    return string.Format("CPU 無活動の確認が {0}/{1} 回", idle, Settings.IdleScansRequired);
            }

            return null;
        }

        /// <summary>CPU 時間の増分から、各プロセスの連続無活動スキャン回数を更新する。</summary>
        private void UpdateCpuHistory(Dictionary<int, ProcInfo> map)
        {
            foreach (var p in map.Values)
            {
                double prev;
                if (_lastCpu.TryGetValue(p.Pid, out prev))
                {
                    if (p.CpuSeconds - prev > CpuBusyThresholdSeconds)
                        _idleScans[p.Pid] = 0;
                    else
                        _idleScans[p.Pid] = (_idleScans.ContainsKey(p.Pid) ? _idleScans[p.Pid] : 0) + 1;
                }
                else
                {
                    // 初観測。最低 1 スキャン分の履歴が貯まるまで殺さない。
                    _idleScans[p.Pid] = 0;
                }
                _lastCpu[p.Pid] = p.CpuSeconds;
            }

            // 消えた PID の履歴を破棄（PID 再利用による誤判定を防ぐ）
            var gone = _lastCpu.Keys.Where(pid => !map.ContainsKey(pid)).ToList();
            foreach (int pid in gone) { _lastCpu.Remove(pid); _idleScans.Remove(pid); }
        }

        /// <summary>計画を実行する。dryRun なら計測だけ行い終了はしない。</summary>
        public ReapResult Execute(ReapPlan plan, bool dryRun)
        {
            var r = new ReapResult();
            r.Before = ProcessScanner.Memory();
            LastMemory = r.Before;

            // 承認ゲート。ドライランで確認していない signatures.conf では実回収を行わない。
            // トレイ・コマンドライン・自動走査のどの経路でもここを通るので、
            // 呼び出し側が忘れても回収は起きない。
            if (!dryRun && !Guard.IsApproved(Signatures))
            {
                dryRun = true;
                r.BlockedByApproval = true;
                string msg = "承認ゲート: いまの signatures.conf は --dry-run で確認されていません。"
                    + "ドライランとして実行します（プロセスは終了しません）。"
                    + "内容を確認したうえで AgentReaper.exe --approve を実行してください。";
                // 指紋込みのキーにする。signatures.conf を編集し直したら改めて 1 度だけ記録する。
                if (_loggedRejects.Add("approval-gate:" + Guard.Fingerprint(Signatures))) Log.Write(msg);
            }

            // 古い／誤った計画でも現用ツリーを終了しないよう、実行直前の親子関係で再確認。
            var current = plan.Kill.Count > 0 ? ProcessScanner.Snapshot() : new Dictionary<int, ProcInfo>();
            foreach (ReapTarget t in plan.Kill)
            {
                if (t.Client != null || t.Tree.Any(p =>
                {
                    ProcInfo live;
                    return current.TryGetValue(p.Pid, out live) &&
                        (Config.ClientAnchors.Contains(live.Name) || ProcessScanner.ClientAncestor(live) != null);
                })) continue;

                bool any = false;

                // 子孫を先に終了させ、最後に根を終了させる
                for (int i = t.Tree.Count - 1; i >= 0; i--)
                {
                    ProcInfo p = t.Tree[i];

                    ProcInfo live;
                    if (!current.TryGetValue(p.Pid, out live)) continue;
                    if ((live.Created - p.Created).Duration() > TimeSpan.FromSeconds(2)) continue;
                    if (Config.Protected.Contains(live.Name)) continue;
                    if (Config.Protected.Contains(p.Name)) continue;          // 最終防衛線
                    if (Settings.ExcludePids.Contains(p.Pid)) continue;
                    if (p.Pid == System.Diagnostics.Process.GetCurrentProcess().Id) continue;

                    if (dryRun) { r.ProcessesKilled++; any = true; continue; }

                    try
                    {
                        using (var proc = System.Diagnostics.Process.GetProcessById(p.Pid))
                        {
                            // 走査後に PID が再利用されていないことを確認
                            if ((proc.StartTime - p.Created).Duration() > TimeSpan.FromSeconds(2)) continue;
                            proc.Kill();
                            r.ProcessesKilled++;
                            any = true;
                        }
                    }
                    catch (ArgumentException) { /* 既に終了 */ }
                    catch (Exception ex)
                    {
                        r.Failed++;
                        Log.Write(string.Format("  終了失敗 pid={0} {1}: {2}", p.Pid, p.RawName, ex.Message));
                    }
                }

                if (any) r.TargetsKilled++;
            }

            if (!dryRun && r.ProcessesKilled > 0)
                System.Threading.Thread.Sleep(1200);   // 終了が OS に反映されるのを待つ

            r.After = ProcessScanner.Memory();
            LastMemory = r.After;
            return r;
        }

        /// <summary>設定で有効になっているメモリリスト操作をまとめて実行する。</summary>
        public static List<string> RunMemoryCommands(Settings s)
        {
            var messages = new List<string>();

            if (s.FlushModifiedList)
                messages.Add(Report("変更済みリストのフラッシュ",
                    Native.MemoryListCommand(Native.MemoryFlushModifiedList)));

            if (s.PurgeLowPriorityStandby)
                messages.Add(Report("低優先スタンバイの解放",
                    Native.MemoryListCommand(Native.MemoryPurgeLowPriorityStandbyList)));

            if (s.PurgeAllStandby)
                messages.Add(Report("全スタンバイの削除",
                    Native.MemoryListCommand(Native.MemoryPurgeStandbyList)));

            if (s.EmptyAllWorkingSets)
                messages.Add(Report("全ワーキングセットの切り詰め",
                    Native.MemoryListCommand(Native.MemoryEmptyWorkingSets)));

            return messages;
        }

        private static string Report(string label, string error)
        {
            return error == null
                ? string.Format("{0}: OK", label)
                : string.Format("{0}: 失敗 ({1})", label, error);
        }
    }
}
