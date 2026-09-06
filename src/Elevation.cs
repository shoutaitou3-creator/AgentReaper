using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AgentReaper
{
    /// <summary>
    /// メモリリスト操作の昇格実行。トレイの「今すぐ軽くする」と --lighten が共有する。
    ///
    /// 優先順位:
    ///   1. 既に管理者ならそのまま実行
    ///   2. 「最高の特権で実行」で登録済みのタスクスケジューラ経由（UAC が出ない）
    ///   3. 自分自身を runas で昇格起動（UAC が出る）
    /// </summary>
    internal static class Elevation
    {
        public const string TaskName = "AgentReaper-MemoryCommands";

        public static string ResultPath
        {
            get { return Path.Combine(Config.BaseDir, "memory-commands.result"); }
        }

        public static List<string> RunMemoryCommands(Settings s, string exePath)
        {
            if (!s.FlushModifiedList && !s.PurgeLowPriorityStandby
                && !s.PurgeAllStandby && !s.EmptyAllWorkingSets)
                return new List<string>();

            if (Program.IsAdmin())
                return Reaper.RunMemoryCommands(s);

            List<string> viaTask = TryScheduledTask();
            if (viaTask != null) return viaTask;

            return TryRunAs(exePath);
        }

        /// <summary>
        /// 登録済みタスクを起動する。schtasks /run は完了を待たないため、
        /// ヘルパーが残す結果ファイルの出現をもって完了とみなす。
        /// 未登録・起動失敗・時間内に結果が出ない場合は null を返し、呼び出し側が UAC へ退避する。
        /// </summary>
        public static List<string> TryScheduledTask()
        {
            string result = ResultPath;
            try
            {
                if (File.Exists(result)) File.Delete(result);
            }
            catch
            {
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + TaskName + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) return null;
                    if (p.ExitCode != 0) return null;
                }
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < 60; i++)   // 最大 30 秒待つ
            {
                System.Threading.Thread.Sleep(500);
                try
                {
                    if (!File.Exists(result)) continue;

                    string[] lines = File.ReadAllLines(result);
                    if (lines.Length == 0) continue;

                    var messages = new List<string>();
                    messages.Add(lines[0].Trim() == "OK"
                        ? "メモリリスト操作: OK（タスクスケジューラ経由・UAC なし）"
                        : "メモリリスト操作: 失敗（タスクスケジューラ経由）");
                    for (int k = 1; k < lines.Length; k++)
                        if (lines[k].Trim().Length > 0) messages.Add("  " + lines[k].Trim());
                    return messages;
                }
                catch
                {
                    // 書き込み途中の可能性があるので次の周回で読み直す
                }
            }

            return null;
        }

        private static List<string> TryRunAs(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = "--memory-commands";
                psi.UseShellExecute = true;
                psi.Verb = "runas";

                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(20000);
                    return new List<string> { p.ExitCode == 0
                        ? "メモリリスト操作: OK（UAC 昇格して実行）"
                        : string.Format("メモリリスト操作: 失敗（終了コード {0}）", p.ExitCode) };
                }
            }
            catch (Exception ex)
            {
                // UAC をキャンセルした場合もここに来る
                return new List<string> { "メモリリスト操作: 実行せず（" + ex.Message + "）" };
            }
        }

        /// <summary>タスクが登録済みかどうか（状態表示用）。</summary>
        public static bool TaskRegistered()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/query /tn \"" + TaskName + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
