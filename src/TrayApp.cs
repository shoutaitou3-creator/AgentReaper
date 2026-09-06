using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AgentReaper
{
    /// <summary>タスクトレイ常駐本体。</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _notify = new NotifyIcon();
        private readonly Timer _timer = new Timer();
        private readonly Reaper _reaper = new Reaper();

        private readonly Icon _iconGreen;
        private readonly Icon _iconAmber;
        private readonly Icon _iconRed;

        private ToolStripMenuItem _miAutoReap;
        private ToolStripMenuItem _miDryRun;
        private ToolStripMenuItem _miStatus;

        private bool _wasRed;
        private bool _busy;

        private DateTime _lastAutoLighten = DateTime.MinValue;
        private DateTime _lastTrendLog = DateTime.MinValue;
        private volatile bool _lightening;

        public TrayApp()
        {
            _iconGreen = MakeIcon(Color.FromArgb(46, 160, 92));
            _iconAmber = MakeIcon(Color.FromArgb(224, 158, 32));
            _iconRed = MakeIcon(Color.FromArgb(206, 62, 54));

            ReloadConfig();

            _notify.Icon = _iconGreen;
            _notify.Text = "AgentReaper";
            _notify.Visible = true;
            _notify.ContextMenuStrip = BuildMenu();
            _notify.DoubleClick += delegate { ShowStatus(); };

            _timer.Tick += delegate { Tick(false); };
            _timer.Start();

            Log.Section("起動");
            Log.Write(string.Format("シグネチャ {0} 件 / 間隔 {1}秒 / 自動回収 {2} / ドライラン {3}",
                _reaper.Signatures.Count, _reaper.Settings.ScanIntervalSeconds,
                _reaper.Settings.AutoReap, _reaper.Settings.DryRun));

            Tick(false);
        }

        // ---------------- メニュー ----------------

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            _miStatus = new ToolStripMenuItem("状態を確認…");
            _miStatus.Click += delegate { ShowStatus(); };
            _miStatus.Font = new Font(_miStatus.Font, FontStyle.Bold);
            menu.Items.Add(_miStatus);

            var miReap = new ToolStripMenuItem("今すぐ軽くする");
            miReap.Click += delegate { RunNow(); };
            menu.Items.Add(miReap);

            var miPreview = new ToolStripMenuItem("何が回収されるか見る（ドライラン）");
            miPreview.Click += delegate { ShowPreview(); };
            menu.Items.Add(miPreview);

            menu.Items.Add(new ToolStripSeparator());

            _miAutoReap = new ToolStripMenuItem("自動回収を有効にする");
            _miAutoReap.CheckOnClick = true;
            _miAutoReap.Checked = _reaper.Settings.AutoReap;
            _miAutoReap.Click += delegate { _reaper.Settings.AutoReap = _miAutoReap.Checked; };
            menu.Items.Add(_miAutoReap);

            _miDryRun = new ToolStripMenuItem("ドライラン（実際には終了しない）");
            _miDryRun.CheckOnClick = true;
            _miDryRun.Checked = _reaper.Settings.DryRun;
            _miDryRun.Click += delegate { _reaper.Settings.DryRun = _miDryRun.Checked; };
            menu.Items.Add(_miDryRun);

            menu.Items.Add(new ToolStripSeparator());

            var miLog = new ToolStripMenuItem("ログを開く");
            miLog.Click += delegate { OpenFile(Config.LogPath); };
            menu.Items.Add(miLog);

            var miSig = new ToolStripMenuItem("シグネチャ定義を編集");
            miSig.Click += delegate { OpenFile(Config.SignaturesPath); };
            menu.Items.Add(miSig);

            var miSet = new ToolStripMenuItem("設定を編集");
            miSet.Click += delegate { OpenFile(Config.SettingsPath); };
            menu.Items.Add(miSet);

            var miReload = new ToolStripMenuItem("設定を再読み込み");
            miReload.Click += delegate
            {
                ReloadConfig();
                _miAutoReap.Checked = _reaper.Settings.AutoReap;
                _miDryRun.Checked = _reaper.Settings.DryRun;
                _notify.ShowBalloonTip(2000, "AgentReaper",
                    string.Format("設定を再読み込みしました（シグネチャ {0} 件）", _reaper.Signatures.Count),
                    ToolTipIcon.Info);
            };
            menu.Items.Add(miReload);

            menu.Items.Add(new ToolStripSeparator());

            var miExit = new ToolStripMenuItem("終了");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            return menu;
        }

        private void ReloadConfig()
        {
            _reaper.Signatures = Config.LoadSignatures();
            _reaper.Settings = Config.LoadSettings();
            _timer.Interval = _reaper.Settings.ScanIntervalSeconds * 1000;
        }

        // ---------------- 定期処理 ----------------

        private void Tick(bool force)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                ReapPlan plan = _reaper.Plan();
                MemSnapshot mem = ProcessScanner.Memory();

                // 判定は実空きページだけ。プロセス数は使わない（2026-09-06 の実測で無相関と確定）。
                // 522 プロセス・実空き 31.5GB という健全そのものの状態で
                // 「PC が重くなっています」を出していた。誤報は本物の警告まで無視させる。
                //
                // さらに、このスキャンで自動解放が直すなら警告しない。判定は MaybeAutoLighten より
                // 前に走るので、そうしないと「追いついていません」と出した直後に直ることになる。
                // アイコンの色は「いま実空きが少ない」という状態表示。自動解放が直す最中でも赤くする。
                bool lowFree = mem.FreeAndZeroGb < _reaper.Settings.WarnFreeGb;
                // トーストを出すのは、自動解放に任せられない時だけ。状態表示とは別条件にする。
                bool warn = lowFree && !AutoLightenWillHandle(mem);

                _notify.Icon = lowFree ? _iconRed : (plan.Kill.Count > 0 ? _iconAmber : _iconGreen);
                _notify.Text = Truncate(string.Format(
                    "AgentReaper — プロセス {0} / 実空き {1:F1}GB{2}",
                    mem.ProcessCount, mem.FreeAndZeroGb,
                    plan.Kill.Count > 0
                        ? string.Format("\n回収候補 {0} 個（{1} プロセス）", plan.Kill.Count, plan.KillProcessCount)
                        : "\n回収候補なし"), 63);

                if (warn && !_wasRed)
                {
                    // 体感を断定せず、検知した事実だけを書く。ここへ来るのは
                    // 自動解放が無効・ドライラン・クールダウン中（＝直前に解放したのにまだ足りない）
                    // のいずれかなので、「追いついていない」は事実として正しい。
                    _notify.ShowBalloonTip(5000, "実空きページが不足しています",
                        string.Format("実空き {0:F1}GB（閾値 {1:F1}GB）。自動解放が追いついていません。プロセス {2} 個 / 回収候補 {3} 個。",
                            mem.FreeAndZeroGb, _reaper.Settings.WarnFreeGb, mem.ProcessCount, plan.Kill.Count),
                        ToolTipIcon.Warning);
                }
                _wasRed = warn;

                if ((_reaper.Settings.AutoReap || force) && plan.Kill.Count > 0)
                {
                    ReapResult r = _reaper.Execute(plan, _reaper.Settings.DryRun);
                    LogResult(plan, r, _reaper.Settings.DryRun);
                }

                LogTrend(mem, plan);
                MaybeAutoLighten(mem);
            }
            catch (Exception ex)
            {
                Log.Write("スキャン中の例外: " + ex);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// 実空きページが閾値を割ったら、昇格タスク経由でメモリリスト操作を自動実行する。
        /// マウス停止＝実空き枯渇なので、枯渇する前に先回りして実空きを回復させるのが狙い。
        ///
        /// - 昇格が要る操作なので登録済みタスク経由でだけ行う（UAC は自動では絶対に出さない）。
        /// - タスクは最大 30 秒ブロックするため、UI タイマーを止めないよう別スレッドで走らせる。
        /// - クールダウンとフラグでスラッシング・多重実行を防ぐ。
        /// - ドライラン中は解放もしない。
        /// </summary>
        /// <summary>
        /// このスキャンで自動解放が実空きを回復させる見込みがあるか。
        /// 警告を出す前に呼ぶ。見込みがあるなら黙って任せる（直後に直るものを警告しない）。
        /// クールダウン中は「直前に解放したのにまだ足りない」＝本当に追いついていないので警告する。
        /// </summary>
        private bool AutoLightenWillHandle(MemSnapshot mem)
        {
            if (!_reaper.Settings.AutoLighten) return false;
            if (_reaper.Settings.DryRun) return false;
            if (_lightening) return true;   // 実行中。結果を待てばよい
            if (mem.FreeAndZeroGb >= _reaper.Settings.AutoLightenFreeGb) return false;
            if ((DateTime.Now - _lastAutoLighten).TotalSeconds < _reaper.Settings.AutoLightenCooldownSeconds) return false;
            return true;                    // このスキャンで発動する
        }

        private void MaybeAutoLighten(MemSnapshot mem)
        {
            if (!_reaper.Settings.AutoLighten) return;
            if (_reaper.Settings.DryRun) return;
            if (_lightening) return;
            if (mem.FreeAndZeroGb >= _reaper.Settings.AutoLightenFreeGb) return;
            if ((DateTime.Now - _lastAutoLighten).TotalSeconds < _reaper.Settings.AutoLightenCooldownSeconds) return;

            _lastAutoLighten = DateTime.Now;
            _lightening = true;
            double beforeFree = mem.FreeAndZeroGb;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Log.Section("自動解放（実空き低下を検知）");
                    Log.Write(string.Format("トリガー: 実空き {0:F2}GB < 閾値 {1:F2}GB",
                        beforeFree, _reaper.Settings.AutoLightenFreeGb));

                    List<string> msgs = Elevation.TryScheduledTask();
                    if (msgs == null)
                    {
                        Log.Write("  メモリリスト操作: 実行できず（昇格タスク未登録の可能性。"
                            + "install-elevated-task.ps1 を実行してください）");
                    }
                    else
                    {
                        foreach (string m in msgs) Log.Write("  " + m);
                    }

                    MemSnapshot after = ProcessScanner.Memory();
                    Log.Write(string.Format("  実空き: {0:F2}GB → {1:F2}GB / スタンバイ {2:F2}GB / 圧縮 {3:F2}GB",
                        beforeFree, after.FreeAndZeroGb, after.StandbyGb, after.CompressionGb));

                    if (after.FreeAndZeroGb > beforeFree + 1.0)
                        _notify.ShowBalloonTip(4000, "AgentReaper",
                            string.Format("自動でメモリを解放しました（実空き {0:F1} → {1:F1} GB）",
                                beforeFree, after.FreeAndZeroGb), ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Log.Write("自動解放の例外: " + ex);
                }
                finally
                {
                    _lightening = false;
                }
            });
        }

        /// <summary>
        /// 実空きの推移をログに残す。閾値 autoLightenFreeGb が妥当かどうかは実データでしか
        /// 決められないため、判断材料が人手を介さず溜まるようにする。
        /// 平常時は 10 分ごと、閾値の 2 倍を割った危険域では 1 分ごとに記録する。
        /// </summary>
        private void LogTrend(MemSnapshot mem, ReapPlan plan)
        {
            double danger = _reaper.Settings.AutoLightenFreeGb * 2.0;
            double intervalMinutes = mem.FreeAndZeroGb < danger ? 1.0 : 10.0;
            if ((DateTime.Now - _lastTrendLog).TotalMinutes < intervalMinutes) return;
            _lastTrendLog = DateTime.Now;

            Log.Write(string.Format(
                "[推移] 実空き {0:F2}GB / スタンバイ {1:F2}GB / 変更済み {2:F2}GB / 圧縮 {3:F2}GB"
                + " / proc {4} / handles {5:N0} / 回収候補 {6}{7}",
                mem.FreeAndZeroGb, mem.StandbyGb, mem.ModifiedGb, mem.CompressionGb,
                mem.ProcessCount, mem.HandleCount,
                plan == null ? 0 : plan.Kill.Count,
                mem.FreeAndZeroGb < danger ? "  ← 危険域" : ""));
        }

        // ---------------- 手動操作 ----------------

        private void RunNow()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                ReapPlan plan = _reaper.Plan();
                Log.Section(_reaper.Settings.DryRun ? "手動実行（ドライラン）" : "手動実行");

                ReapResult r = _reaper.Execute(plan, _reaper.Settings.DryRun);
                LogResult(plan, r, _reaper.Settings.DryRun);

                var lines = new List<string>();
                lines.Add(_reaper.Settings.DryRun
                    ? string.Format("ドライラン: {0} ツリー / {1} プロセスが対象（実際には終了していません）",
                        plan.Kill.Count, r.ProcessesKilled)
                    : string.Format("回収: {0} ツリー / {1} プロセスを終了（失敗 {2}）",
                        r.TargetsKilled, r.ProcessesKilled, r.Failed));

                if (!_reaper.Settings.DryRun)
                {
                    foreach (string m in RunMemoryCommandsWithElevation())
                        lines.Add(m);
                }

                MemSnapshot after = ProcessScanner.Memory();
                lines.Add("");
                lines.Add(string.Format("プロセス    : {0} → {1}", r.Before.ProcessCount, after.ProcessCount));
                lines.Add(string.Format("ハンドル    : {0:N0} → {1:N0}", r.Before.HandleCount, after.HandleCount));
                lines.Add(string.Format("実空き      : {0:F2} GB → {1:F2} GB", r.Before.FreeAndZeroGb, after.FreeAndZeroGb));
                lines.Add(string.Format("コミット    : {0:F2} GB → {1:F2} GB", r.Before.CommittedGb, after.CommittedGb));
                lines.Add(string.Format("圧縮ストア  : {0:F2} GB → {1:F2} GB", r.Before.CompressionGb, after.CompressionGb));

                string text = string.Join(Environment.NewLine, lines.ToArray());
                Log.Write(text);
                _notify.ShowBalloonTip(6000, "AgentReaper", Truncate(lines[0], 250), ToolTipIcon.Info);
                ReportForm.Show("実行結果", text);
            }
            catch (Exception ex)
            {
                Log.Write("手動実行の例外: " + ex);
                MessageBox.Show("実行中にエラーが発生しました。\n\n" + ex.Message,
                    "AgentReaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShowPreview()
        {
            ReapPlan plan = _reaper.Plan();
            var sb = new StringBuilder();

            sb.AppendLine(string.Format("回収候補: {0} ツリー / {1} プロセス / {2:F2} GB",
                plan.Kill.Count, plan.KillProcessCount, plan.KillWorkingSet / 1024.0 / 1024.0 / 1024.0));
            sb.AppendLine();

            foreach (ReapTarget t in plan.Kill)
            {
                sb.AppendLine(string.Format("[回収] {0} / {1} / pid={2} / {3} プロセス / {4:F1}時間前 / {5}",
                    t.Sig.Name, t.ClientLabel, t.Root.Pid, t.Count, t.Root.Age.TotalHours, t.Root.RawName));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("温存: {0} ツリー", plan.Keep.Count));
            foreach (ReapTarget t in plan.Keep.OrderBy(x => x.Sig.Name).ThenBy(x => x.ClientLabel))
            {
                sb.AppendLine(string.Format("[温存] {0} / {1} / pid={2} / {3} プロセス / 理由: {4}",
                    t.Sig.Name, t.ClientLabel, t.Root.Pid, t.Count, t.Reason));
            }

            ReportForm.Show("ドライラン結果", sb.ToString());
        }

        private void ShowStatus()
        {
            MemSnapshot m = ProcessScanner.Memory();
            ReapPlan plan = _reaper.LastPlan;

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("プロセス数    : {0}", m.ProcessCount));
            sb.AppendLine(string.Format("ハンドル数    : {0:N0}", m.HandleCount));
            sb.AppendLine();
            sb.AppendLine(string.Format("物理メモリ    : {0:F2} GB", m.TotalGb));
            sb.AppendLine(string.Format("Available     : {0:F2} GB  ← タスクマネージャの「空き」", m.AvailableGb));
            sb.AppendLine(string.Format("実空き        : {0:F2} GB  ← 即座に使えるのはこれだけ", m.FreeAndZeroGb));
            sb.AppendLine(string.Format("スタンバイ    : {0:F2} GB", m.StandbyGb));
            sb.AppendLine(string.Format("変更済み      : {0:F2} GB", m.ModifiedGb));
            sb.AppendLine(string.Format("コミット      : {0:F2} / {1:F2} GB", m.CommittedGb, m.CommitLimitGb));
            sb.AppendLine(string.Format("圧縮ストア    : {0:F2} GB  ← 再起動でしか戻らない", m.CompressionGb));
            sb.AppendLine();
            sb.AppendLine(plan == null
                ? "回収候補      : 未スキャン"
                : string.Format("回収候補      : {0} ツリー / {1} プロセス（温存 {2} ツリー）",
                    plan.Kill.Count, plan.KillProcessCount, plan.Keep.Count));
            sb.AppendLine();
            sb.AppendLine(string.Format("自動回収      : {0}", _reaper.Settings.AutoReap ? "有効" : "無効"));
            sb.AppendLine(string.Format("自動解放      : {0}（実空き {1:F1}GB 未満で発動）",
                _reaper.Settings.AutoLighten ? "有効" : "無効", _reaper.Settings.AutoLightenFreeGb));
            sb.AppendLine(string.Format("最終自動解放  : {0}",
                _lastAutoLighten == DateTime.MinValue ? "未実行" : _lastAutoLighten.ToString("HH:mm:ss")));
            sb.AppendLine(string.Format("ドライラン    : {0}", _reaper.Settings.DryRun ? "有効（終了しない）" : "無効"));

            // 承認ゲートの状態。これを出さないと、「自動回収 有効・ドライラン 無効」なのに
            // 何も起きない理由が画面のどこにも出ない。
            sb.AppendLine(string.Format("承認          : {0}", _reaper.Signatures.Count == 0
                ? "対象なし（signatures.conf が空なので回収は起きません）"
                : (Guard.IsApproved(_reaper.Signatures)
                    ? "済み（この signatures.conf での回収が有効）"
                    : "未承認 — --dry-run で確認して --approve するまで終了しません")));

            sb.AppendLine(string.Format("スキャン間隔  : {0} 秒", _reaper.Settings.ScanIntervalSeconds));
            sb.AppendLine(string.Format("管理者権限    : {0}", Program.IsAdmin() ? "あり" : "なし"));
            sb.AppendLine(string.Format("昇格タスク    : {0}", Elevation.TaskRegistered()
                ? "登録済み（UAC は出ません）"
                : "未登録（install-elevated-task.ps1 を実行すると UAC が不要になります）"));

            // 拒否・無効化を黙って隠さない。設定が効いていない理由はここで分かるようにする。
            if (Config.RejectedSignatures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("読み込みで拒否されたシグネチャ:");
                foreach (string d in Config.RejectedSignatures) sb.AppendLine("  " + d);
            }
            if (_reaper.DisabledSignatures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("この走査で無効化されたシグネチャ:");
                foreach (string d in _reaper.DisabledSignatures) sb.AppendLine("  " + d);
            }

            ReportForm.Show("AgentReaper の状態", sb.ToString());
        }

        private List<string> RunMemoryCommandsWithElevation()
        {
            return Elevation.RunMemoryCommands(_reaper.Settings, Application.ExecutablePath);
        }

        // ---------------- 補助 ----------------

        private static void LogResult(ReapPlan plan, ReapResult r, bool dryRun)
        {
            if (plan.Kill.Count == 0) return;

            Log.Write(string.Format("{0}: {1} ツリー / {2} プロセス",
                dryRun ? "ドライラン" : "回収", r.TargetsKilled, r.ProcessesKilled));

            foreach (ReapTarget t in plan.Kill)
            {
                Log.Write(string.Format("  {0} sig={1} client={2} pid={3} n={4} age={5:F1}h ws={6:F0}MB",
                    dryRun ? "[dry]" : "[kill]", t.Sig.Name, t.ClientLabel, t.Root.Pid,
                    t.Count, t.Root.Age.TotalHours, t.WorkingSet / 1024.0 / 1024.0));
            }

            Log.Write(string.Format("  前: {0}", r.Before));
            Log.Write(string.Format("  後: {0}", r.After));
        }

        private static void OpenFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    System.IO.File.WriteAllText(path, "");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("開けませんでした: " + path + "\n\n" + ex.Message,
                    "AgentReaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(color))
                        g.FillEllipse(b, 2, 2, 28, 28);
                    using (var p = new Pen(Color.FromArgb(210, 25, 28, 32), 2.5f))
                        g.DrawEllipse(p, 2, 2, 28, 28);
                    using (var w = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                    {
                        g.FillRectangle(w, 9, 8, 4, 13);
                        g.FillRectangle(w, 19, 8, 4, 13);
                        g.FillRectangle(w, 9, 23, 14, 3);
                    }
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    // クローンして自前のハンドルを持たせ、元のハンドルは解放する
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    Native.ReleaseIcon(h);
                }
            }
        }

        private void ExitApp()
        {
            Log.Write("終了");
            _timer.Stop();
            _notify.Visible = false;
            _notify.Dispose();
            ExitThread();
        }
    }
}
