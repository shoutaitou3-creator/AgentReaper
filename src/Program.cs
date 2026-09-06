using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AgentReaper
{
    internal static class Program
    {
        private const string MutexName = "Global\\AgentReaper.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
            bool noGui = args.Any(a => string.Equals(a.Trim(), "--nogui", StringComparison.OrdinalIgnoreCase));
            bool json = args.Any(a => string.Equals(a.Trim(), "--json", StringComparison.OrdinalIgnoreCase));

            switch (mode)
            {
                case "--diagnose":
                    return RunDiagnose(json, noGui || json);

                case "--approve":
                    return RunApprove(noGui);

                case "--memory-commands":
                    return RunMemoryCommandsMode();

                case "--dry-run":
                    return RunOneShot(true, noGui);

                case "--reap-once":
                    return RunOneShot(false, noGui);

                case "--lighten":
                    return RunLighten(noGui);

                case "--pool-tags":
                    return RunPoolTags(noGui, args.Any(a =>
                        string.Equals(a.Trim(), "--attribute", StringComparison.OrdinalIgnoreCase)));

                case "--help":
                case "-h":
                case "/?":
                    Emit("使い方", HelpText(), noGui);
                    return 0;

                default:
                    return RunTray();
            }
        }

        // ---------------- モード ----------------

        /// <summary>
        /// 環境を測って機械可読で出す。このツールの主インターフェース。
        /// --json なら標準出力へ JSON を流し、常に diagnose.json も残す。
        /// </summary>
        private static int RunDiagnose(bool json, bool noGui)
        {
            var reaper = new Reaper();
            reaper.Signatures = Config.LoadSignatures();
            reaper.Settings = Config.LoadSettings();

            // シグネチャごとの一致数と動的 Guard の判定を埋めるため、実際に 1 回走査する。
            // ドライランなので何も終了しない。
            reaper.Plan();

            JObj report = Diagnose.Build(reaper);

            string jsonText = Json.Write(report);
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Config.BaseDir, "diagnose.json"),
                    jsonText, new UTF8Encoding(false));
            }
            catch
            {
            }

            if (json)
            {
                ConsoleOut.Write(jsonText);
                return 0;
            }

            string text = Json.ToText(report);
            Emit("診断", text, noGui);
            return 0;
        }

        /// <summary>
        /// いまの signatures.conf をドライランで確認したものとして記録する。
        /// これを通すまで、設定が dryRun = false でもプロセスは終了しない（Reaper.Execute の承認ゲート）。
        /// </summary>
        private static int RunApprove(bool noGui)
        {
            var reaper = new Reaper();
            reaper.Signatures = Config.LoadSignatures();
            reaper.Settings = Config.LoadSettings();

            if (reaper.Signatures.Count == 0)
            {
                string none = "signatures.conf に有効なシグネチャがありません。承認するものがありません。";
                if (Config.RejectedSignatures.Count > 0)
                {
                    none += Environment.NewLine + Environment.NewLine + "拒否された行:";
                    foreach (string r in Config.RejectedSignatures)
                        none += Environment.NewLine + "  " + r;
                }
                Emit("承認", none, noGui);
                return 1;
            }

            // 承認の前に必ずドライランを通す。ここで何が対象になるかを見せる。
            int warmups = Math.Max(1, reaper.Settings.IdleScansRequired) + 1;
            for (int i = 0; i < warmups; i++)
            {
                reaper.Plan();
                if (i < warmups - 1) Thread.Sleep(3000);
            }

            ReapPlan plan = reaper.Plan();
            ReapResult dry = reaper.Execute(plan, true);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("ドライラン: {0} ツリー / {1} プロセスが対象",
                plan.Kill.Count, dry.ProcessesKilled));
            foreach (ReapTarget t in plan.Kill)
                sb.AppendLine(string.Format("  [対象] {0} / {1} / pid={2} / {3} プロセス / {4:F1}時間前",
                    t.Sig.Name, t.ClientLabel, t.Root.Pid, t.Count, t.Root.Age.TotalHours));

            if (reaper.DisabledSignatures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("この走査で無効化されたシグネチャ:");
                foreach (string d in reaper.DisabledSignatures) sb.AppendLine("  " + d);
            }

            string summary = sb.ToString();

            try
            {
                Guard.WriteApproval(reaper.Signatures, summary);
            }
            catch (Exception ex)
            {
                string err = "承認の記録に失敗しました: " + ex.Message;
                Emit("承認", err, noGui);
                return 1;
            }

            var outText = new StringBuilder();
            outText.AppendLine("承認しました。この signatures.conf での回収が有効になります。");
            outText.AppendLine("  指紋      : " + Guard.Fingerprint(reaper.Signatures));
            outText.AppendLine("  記録先    : " + Guard.ApprovalPath);
            outText.AppendLine();
            outText.AppendLine("signatures.conf を編集すると指紋が変わり、承認は自動的に無効になります。");
            outText.AppendLine("そのときは --dry-run で確認してから --approve をやり直してください。");
            outText.AppendLine();
            outText.Append(summary);

            string final = outText.ToString();
            Log.Section("承認");
            Log.Write(final);
            Emit("承認", final, noGui);
            return 0;
        }

        private static int RunTray()
        {
            bool isNew;
            using (var mutex = new Mutex(true, MutexName, out isNew))
            {
                if (!isNew)
                {
                    MessageBox.Show("AgentReaper は既に起動しています（タスクトレイを確認してください）。",
                        "AgentReaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                try
                {
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    Log.Write("致命的な例外: " + ex);
                    MessageBox.Show("AgentReaper が異常終了しました。\n\n" + ex.Message,
                        "AgentReaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    GC.KeepAlive(mutex);
                }
            }
            return 0;
        }

        /// <summary>
        /// 昇格して呼ばれるヘルパー。UI は出さずメモリリスト操作だけ行う。
        /// タスクスケジューラ経由で起動されると終了コードを直接受け取れないため、
        /// 結果を memory-commands.result に書き出して呼び出し側が読めるようにする。
        /// </summary>
        private static int RunMemoryCommandsMode()
        {
            Settings s = Config.LoadSettings();
            List<string> messages = Reaper.RunMemoryCommands(s);
            bool failed = messages.Any(m => m.Contains("失敗"));

            Log.Section("メモリリスト操作（昇格）");
            foreach (string m in messages) Log.Write("  " + m);

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(failed ? "NG" : "OK");
                foreach (string m in messages) sb.AppendLine(m);
                System.IO.File.WriteAllText(Elevation.ResultPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }

            return failed ? 1 : 0;
        }

        /// <summary>
        /// トレイの「今すぐ軽くする」と同じことをコマンドラインから実行する。
        /// 昇格経路（タスクスケジューラ → UAC）もトレイと同じコードを通る。
        /// </summary>
        private static int RunLighten(bool noGui)
        {
            var reaper = new Reaper();
            reaper.Signatures = Config.LoadSignatures();
            reaper.Settings = Config.LoadSettings();
            reaper.Settings.DryRun = false;

            int warmups = Math.Max(1, reaper.Settings.IdleScansRequired) + 1;
            for (int i = 0; i < warmups; i++)
            {
                reaper.Plan();
                if (i < warmups - 1) Thread.Sleep(3000);
            }

            Log.Section("今すぐ軽くする（コマンドライン）");
            ReapPlan plan = reaper.Plan();
            ReapResult r = reaper.Execute(plan, false);

            var sb = new StringBuilder();
            if (r.BlockedByApproval)
            {
                sb.AppendLine("承認ゲート: signatures.conf が未承認のためプロセスは終了していません。");
                sb.AppendLine("（メモリリスト操作はシグネチャに関係ないので、下の通り実行しています）");
                sb.AppendLine();
            }
            sb.AppendLine(string.Format("回収: {0} ツリー / {1} プロセスを終了（失敗 {2}）",
                r.TargetsKilled, r.ProcessesKilled, r.Failed));

            foreach (string m in Elevation.RunMemoryCommands(
                         reaper.Settings, Application.ExecutablePath))
                sb.AppendLine(m);

            MemSnapshot after = ProcessScanner.Memory();
            sb.AppendLine();
            sb.AppendLine(string.Format("プロセス    : {0} → {1}", r.Before.ProcessCount, after.ProcessCount));
            sb.AppendLine(string.Format("ハンドル    : {0:N0} → {1:N0}", r.Before.HandleCount, after.HandleCount));
            sb.AppendLine(string.Format("実空き      : {0:F2} GB → {1:F2} GB", r.Before.FreeAndZeroGb, after.FreeAndZeroGb));
            sb.AppendLine(string.Format("スタンバイ  : {0:F2} GB → {1:F2} GB", r.Before.StandbyGb, after.StandbyGb));
            sb.AppendLine(string.Format("コミット    : {0:F2} GB → {1:F2} GB", r.Before.CommittedGb, after.CommittedGb));
            sb.AppendLine(string.Format("圧縮ストア  : {0:F2} GB → {1:F2} GB", r.Before.CompressionGb, after.CompressionGb));

            string text = sb.ToString();
            Log.Write(text);
            Emit("今すぐ軽くする", text, noGui);
            return 0;
        }

        /// <summary>1 回だけ走査して結果を表示する。CPU 履歴は短い間隔で貯める。</summary>
        private static int RunOneShot(bool dryRun, bool noGui)
        {
            var reaper = new Reaper();
            reaper.Signatures = Config.LoadSignatures();
            reaper.Settings = Config.LoadSettings();
            reaper.Settings.DryRun = dryRun;

            if (reaper.Signatures.Count == 0)
            {
                Emit("エラー",
                    "signatures.conf が見つからないか空です。\n\n想定パス: " + Config.SignaturesPath, noGui);
                return 1;
            }

            // CPU 無活動の判定に必要な回数だけ短い間隔で走査する
            int warmups = Math.Max(1, reaper.Settings.IdleScansRequired) + 1;
            for (int i = 0; i < warmups; i++)
            {
                reaper.Plan();
                if (i < warmups - 1) Thread.Sleep(3000);
            }

            ReapPlan plan = reaper.Plan();
            Log.Section(dryRun ? "ワンショット（ドライラン）" : "ワンショット回収");

            ReapResult r = reaper.Execute(plan, dryRun);
            MemSnapshot after = ProcessScanner.Memory();

            var sb = new StringBuilder();

            // 承認ゲートに阻まれたことを黙って「0 プロセス」と表示しない。
            // 「走査したが対象が無かった」と「そもそも回収を許可されていない」は別物。
            if (r.BlockedByApproval)
            {
                sb.AppendLine("承認ゲート: いまの signatures.conf は未承認のため、実際には何も終了していません。");
                sb.AppendLine("下の一覧を確認したうえで AgentReaper.exe --approve を実行してください。");
                sb.AppendLine();
            }

            sb.AppendLine(dryRun || r.BlockedByApproval
                ? string.Format("ドライラン: {0} ツリー / {1} プロセスが対象（実際には終了していません）",
                    plan.Kill.Count, r.ProcessesKilled)
                : string.Format("回収: {0} ツリー / {1} プロセスを終了（失敗 {2}）",
                    r.TargetsKilled, r.ProcessesKilled, r.Failed));
            sb.AppendLine();

            foreach (ReapTarget t in plan.Kill)
            {
                sb.AppendLine(string.Format("[{0}] {1} / {2} / pid={3} / {4} プロセス / {5:F1}時間前",
                    dryRun ? "dry" : "kill", t.Sig.Name, t.ClientLabel, t.Root.Pid, t.Count, t.Root.Age.TotalHours));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("温存 {0} ツリー", plan.Keep.Count));
            foreach (ReapTarget t in plan.Keep.OrderBy(x => x.Sig.Name))
                sb.AppendLine(string.Format("[keep] {0} / {1} / pid={2} / 理由: {3}",
                    t.Sig.Name, t.ClientLabel, t.Root.Pid, t.Reason));

            sb.AppendLine();
            sb.AppendLine(string.Format("プロセス  : {0} → {1}", r.Before.ProcessCount, after.ProcessCount));
            sb.AppendLine(string.Format("ハンドル  : {0:N0} → {1:N0}", r.Before.HandleCount, after.HandleCount));
            sb.AppendLine(string.Format("実空き    : {0:F2} GB → {1:F2} GB", r.Before.FreeAndZeroGb, after.FreeAndZeroGb));
            sb.AppendLine(string.Format("コミット  : {0:F2} GB → {1:F2} GB", r.Before.CommittedGb, after.CommittedGb));

            string text = sb.ToString();
            Log.Write(text);
            Emit(dryRun ? "ドライラン結果" : "回収結果", text, noGui);
            return 0;
        }

        /// <summary>
        /// カーネルプールをタグ単位で表示する診断モード。
        /// プロセス回収では動かないメモリがどこに居るかを突き止めるために使う。
        /// --attribute を付けると .sys を全走査してタグの所有ドライバーを逆引きする。
        /// </summary>
        private static int RunPoolTags(bool noGui, bool attribute)
        {
            string error;
            List<PoolTagEntry> tags = PoolTags.Query(out error);
            if (tags == null)
            {
                Emit("プールタグ取得エラー", error, noGui);
                return 1;
            }

            long npTotal = 0, pgTotal = 0;
            foreach (PoolTagEntry e in tags) { npTotal += e.NonPagedUsed; pgTotal += e.PagedUsed; }

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("プールタグ {0} 件", tags.Count));
            sb.AppendLine(string.Format("タグ合計 NonPaged : {0:F2} GB", npTotal / 1024.0 / 1024 / 1024));
            sb.AppendLine(string.Format("タグ合計 Paged    : {0:F2} GB", pgTotal / 1024.0 / 1024 / 1024));
            sb.AppendLine();

            var topNp = tags.Where(e => e.NonPagedUsed > 0)
                            .OrderByDescending(e => e.NonPagedUsed).Take(25).ToList();

            sb.AppendLine("=== NonPaged 上位 25 ===");
            sb.AppendLine("Tag    NonPaged_MB   未解放確保数        確保回数      解放回数");
            foreach (PoolTagEntry e in topNp)
                sb.AppendLine(string.Format("{0,-4} {1,13:N1} {2,14:N0} {3,15:N0} {4,13:N0}",
                    e.Tag, e.NonPagedUsed / 1024.0 / 1024, e.NonPagedOutstanding, e.NonPagedAllocs, e.NonPagedFrees));

            sb.AppendLine();
            sb.AppendLine("=== Paged 上位 15 ===");
            sb.AppendLine("Tag       Paged_MB   未解放確保数");
            foreach (PoolTagEntry e in tags.Where(e => e.PagedUsed > 0)
                                           .OrderByDescending(e => e.PagedUsed).Take(15))
                sb.AppendLine(string.Format("{0,-4} {1,13:N1} {2,14:N0}",
                    e.Tag, e.PagedUsed / 1024.0 / 1024, e.PagedOutstanding));

            if (attribute)
            {
                sb.AppendLine();
                sb.AppendLine("=== タグ所有ドライバーの逆引き（.sys 内にタグ文字列を持つもの） ===");
                Dictionary<string, List<string>> owners =
                    PoolTags.AttributeToDrivers(topNp.Select(e => e.Tag), 64);

                foreach (PoolTagEntry e in topNp)
                {
                    List<string> list;
                    if (!owners.TryGetValue(e.Tag, out list)) continue;
                    sb.AppendLine(string.Format("{0,-4} {1,10:N1} MB : {2}",
                        e.Tag, e.NonPagedUsed / 1024.0 / 1024,
                        list.Count == 0 ? "(該当なし = OS カーネル本体の可能性)" : string.Join(", ", list.ToArray())));
                }
            }

            string text = sb.ToString();
            Log.Section("プールタグ診断");
            Log.Write(text);
            Emit("カーネルプール診断", text, noGui);
            return 0;
        }

        // ---------------- 補助 ----------------

        /// <summary>
        /// 結果を last-report.txt に必ず書き出し、呼び出し元にコンソールがあればそこへも流し、
        /// --nogui でなければウィンドウにも表示する。
        /// 自動実行やスクリプトから呼ぶときはファイルだけを見ればよい。
        /// </summary>
        private static void Emit(string title, string body, bool noGui)
        {
            try
            {
                string path = System.IO.Path.Combine(Config.BaseDir, "last-report.txt");
                System.IO.File.WriteAllText(path, body, new UTF8Encoding(false));
            }
            catch
            {
            }

            ConsoleOut.Write(body.EndsWith(Environment.NewLine) ? body : body + Environment.NewLine);

            if (!noGui) ReportForm.Show(title, body);
        }

        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static string HelpText()
        {
            return string.Join(Environment.NewLine, new string[]
            {
                "AgentReaper — AI エージェントが残す子プロセスを回収してPCを軽く保つ常駐ツール",
                "",
                "はじめに:",
                "  1. AgentReaper.exe --diagnose --json   環境を測る（何も変更しない）",
                "  2. AGENTS.md を自分の ClaudeCode / Codex に読ませて signatures.conf を書かせる",
                "  3. AgentReaper.exe --dry-run          何が対象になるかを見る",
                "  4. AgentReaper.exe --approve         内容に納得したら承認する",
                "  承認するまで、設定が dryRun = false でもプロセスは終了しない。",
                "",
                "使い方:",
                "  AgentReaper.exe                  タスクトレイに常駐する（既定）",
                "  AgentReaper.exe --diagnose       環境診断を表示する",
                "                    --json         標準出力へ JSON で出す（diagnose.json にも残る）",
                "  AgentReaper.exe --approve        いまの signatures.conf を確認済みとして記録する",
                "  AgentReaper.exe --dry-run        1回だけ走査し、何が回収されるかを表示する",
                "  AgentReaper.exe --reap-once      1回だけ走査して実際に回収する（承認が必要）",
                "  AgentReaper.exe --lighten        トレイの「今すぐ軽くする」と同じ処理",
                "  AgentReaper.exe --pool-tags      カーネルプールをタグ単位で診断する",
                "                    --attribute    タグの所有ドライバーを .sys 走査で逆引きする",
                "  AgentReaper.exe --memory-commands  メモリリスト操作のみ（管理者権限が必要）",
                "  いずれも --nogui でウィンドウを出さず last-report.txt に書く",
                "",
                "設定ファイル（exe と同じフォルダ）:",
                "  signatures.conf   回収対象のシグネチャ定義（自分で書く／エージェントに書かせる）",
                "  settings.conf     動作設定",
                "  approved.conf     ドライラン承認の記録（--approve が書く。手で編集しない）",
                "  diagnose.json     直近の --diagnose の結果",
                "  agent-reaper.log  実行ログ",
                "",
                "安全設計（設定では無効化できない）:",
                "  - ドライランを通していない signatures.conf では回収しない",
                "  - 短すぎるパターン・保護対象に当たるパターンは読み込み時に拒否する",
                "  - 走査ごとに、広すぎるパターン・仕事中のプロセスを掴んだパターンを無効化する",
                "  - クライアントごとに最新 N 個は必ず残す（現用の可能性があるため）",
                "  - 生成から一定時間内のプロセスは残す",
                "  - CPU 時間が増えているプロセスは残す",
                "  - エディタ・端末・OS 中核プロセスは設定に関わらず終了しない"
            });
        }
    }
}
