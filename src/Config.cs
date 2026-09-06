using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgentReaper
{
    /// <summary>回収対象を定義するシグネチャ 1 件。</summary>
    internal sealed class Signature
    {
        public string Name;
        public string Field;              // cmdline | name | path
        public string Pattern;            // 大文字小文字を無視した部分一致
        public int KeepNewestPerClient;   // クライアントごとに残す最新インスタンス数
        public int MinAgeMinutes;         // 生成からこの分数以内は必ず残す

        public bool Matches(ProcInfo p)
        {
            string target;
            if (Field == "name") target = p.Name;
            else if (Field == "path") target = p.ExePath;
            else target = p.CommandLine;

            if (string.IsNullOrEmpty(target)) return false;
            return target.IndexOf(Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal sealed class Settings
    {
        public int ScanIntervalSeconds = 30;
        public bool AutoReap = true;

        // 配布時の既定は必ずドライラン。設定を書いた人が --dry-run で結果を見て
        // --approve を実行するまで、AgentReaper は一切プロセスを終了させない。
        public bool DryRun = true;

        public int IdleScansRequired = 3;
        public int MaxKillPerPass = 500;
        public bool PurgeLowPriorityStandby = true;
        public bool FlushModifiedList = true;
        public bool PurgeAllStandby = false;      // 既定 OFF: キャッシュを捨てて逆に遅くなる
        public bool EmptyAllWorkingSets = false;  // 既定 OFF: 次に触った瞬間フォルトで戻る

        // 自動解放（フリーズ予防の本命）。定期走査で実空きページが低下したら、
        // 設定済みのメモリリスト操作（既定でスタンバイ全削除まで）を昇格タスク経由で自動実行する。
        // マウス停止＝実空きページ枯渇なので、閾値を割った時点で先回りして実空きを回復させる。
        // 配布時の既定は OFF。スタンバイの解放は「重くなってから」ではなく先回りで効くが、
        // 昇格を要するうえ効き方が機種で違う。--diagnose を見てから有効化する。
        public bool AutoLighten = false;
        public double AutoLightenFreeGb = 6.0;         // 実空きがこれ未満で発動
        public int AutoLightenCooldownSeconds = 180;   // 連続発動の間隔（スラッシング防止）

        // 警告はプロセス数で判定しない。2026-09-06 の実測でプロセス数は重さと相関しないと確定した。
        //   9/5 重かった時 726 プロセス → DWM コマ落ち 11%
        //   9/6 快調な時   522 プロセス → コマ落ち 0%（463〜479 でも 0%）
        // 実際の機序は実空きページの枯渇なので、それだけで判定する。プロセス数は表示のみ。
        public double WarnFreeGb = 5.0;
        public readonly HashSet<int> ExcludePids = new HashSet<int>();
    }

    internal static class Config
    {
        /// <summary>
        /// 絶対に終了させないプロセス名（設定では上書きできない安全弁）。
        /// MCP クライアント本体と OS の中核プロセスを含む。
        /// </summary>
        public static readonly HashSet<string> Protected =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "svchost", "explorer", "dwm", "fontdrvhost",
            "sihost", "ctfmon", "runtimebroker", "taskhostw", "memory compression",
            "secure system", "conhost", "dllhost", "wudfhost", "audiodg", "spoolsv",
            // MCP クライアント本体（親を殺したら本末転倒）
            "chatgpt", "cursor", "claude", "code", "codex", "antigravity",
            "devenv", "windowsterminal", "powershell", "pwsh", "cmd", "wsl", "wslhost",
            // 自分自身
            "agentreaper"
        };

        /// <summary>
        /// 「どのクライアントに属するか」を数えるときの基準になるプロセス名。
        /// 同じアンカー配下で同一シグネチャのインスタンスが増えていくのがリークの形。
        /// </summary>
        public static readonly HashSet<string> ClientAnchors =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chatgpt", "cursor", "claude", "code", "codex", "antigravity",
            "devenv", "windowsterminal", "explorer"
        };

        /// <summary>
        /// 起動のたびに使い捨てられるラッパー。クライアントの区別には使えないので
        /// 祖先を辿るときは透過させる。ここを基準にすると常に 1 個ずつのグループになり、
        /// 「最新 N 個を残す」が全件に当たって回収が一切効かなくなる。
        /// </summary>
        public static readonly HashSet<string> TransparentWrappers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "conhost", "powershell", "pwsh", "npx", "npm", "sh", "bash", "env"
        };

        public static string BaseDir
        {
            get
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
                return dir;
            }
        }

        public static string SignaturesPath { get { return Path.Combine(BaseDir, "signatures.conf"); } }
        public static string SettingsPath { get { return Path.Combine(BaseDir, "settings.conf"); } }
        public static string LogPath { get { return Path.Combine(BaseDir, "agent-reaper.log"); } }

        /// <summary>
        /// 直近の LoadSignatures() で Guard に拒否された行。--diagnose と起動時通知で使う。
        /// 拒否を握りつぶさないための置き場。黙って無効になるのが一番たちが悪い。
        /// </summary>
        public static readonly List<string> RejectedSignatures = new List<string>();

        public static List<Signature> LoadSignatures()
        {
            var list = new List<Signature>();
            RejectedSignatures.Clear();
            if (!File.Exists(SignaturesPath)) return list;

            foreach (string raw in File.ReadAllLines(SignaturesPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string[] f = line.Split('|');
                if (f.Length < 5)
                {
                    RejectedSignatures.Add(string.Format(
                        "{0} — 列が {1} 個しかありません（名前|照合先|パターン|残す最新数|猶予分 の 5 列が必要）",
                        line, f.Length));
                    continue;
                }

                var s = new Signature();
                s.Name = f[0].Trim();
                s.Field = f[1].Trim().ToLowerInvariant();
                s.Pattern = f[2].Trim();
                s.KeepNewestPerClient = ParseInt(f[3], 1);
                s.MinAgeMinutes = ParseInt(f[4], 20);

                if (s.Name.Length == 0 || s.Pattern.Length == 0)
                {
                    RejectedSignatures.Add(line + " — 名前またはパターンが空です");
                    continue;
                }

                if (s.KeepNewestPerClient < 1) s.KeepNewestPerClient = 1;   // 最低 1 個は必ず残す
                if (s.MinAgeMinutes < 5) s.MinAgeMinutes = 5;               // 最低 5 分は必ず猶予

                // 設定では無効化できない静的検査。危険なパターンはここで落ちる。
                string reject = Guard.RejectStatic(s);
                if (reject != null)
                {
                    RejectedSignatures.Add(s.Name + " — " + reject);
                    Log.Write("シグネチャを拒否: " + s.Name + " — " + reject);
                    continue;
                }

                list.Add(s);
            }
            return list;
        }

        public static Settings LoadSettings()
        {
            var s = new Settings();
            if (!File.Exists(SettingsPath)) return s;

            foreach (string raw in File.ReadAllLines(SettingsPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();

                switch (k)
                {
                    case "scanintervalseconds": s.ScanIntervalSeconds = Math.Max(10, ParseInt(v, 30)); break;
                    case "autoreap": s.AutoReap = ParseBool(v, true); break;
                    case "dryrun": s.DryRun = ParseBool(v, false); break;
                    // 下限 2 は設定で下げられない。1 回の観測では PID 再利用と初観測を区別できない。
                    case "idlescansrequired": s.IdleScansRequired = Math.Max(2, ParseInt(v, 3)); break;
                    case "maxkillperpass": s.MaxKillPerPass = Math.Max(1, ParseInt(v, 500)); break;
                    case "purgelowprioritystandby": s.PurgeLowPriorityStandby = ParseBool(v, true); break;
                    case "flushmodifiedlist": s.FlushModifiedList = ParseBool(v, true); break;
                    case "purgeallstandby": s.PurgeAllStandby = ParseBool(v, false); break;
                    case "emptyallworkingsets": s.EmptyAllWorkingSets = ParseBool(v, false); break;
                    case "autolighten": s.AutoLighten = ParseBool(v, true); break;
                    case "autolightenfreegb": s.AutoLightenFreeGb = ParseDouble(v, 6.0); break;
                    case "autolightencooldownseconds": s.AutoLightenCooldownSeconds = Math.Max(30, ParseInt(v, 180)); break;
                    // warnProcessCount は 2026-09-06 に廃止（プロセス数は重さと相関しない）。
                    // 古い settings.conf に残っていても落ちないよう、読み飛ばすだけにする。
                    case "warnprocesscount": break;
                    case "warnfreegb": s.WarnFreeGb = ParseDouble(v, 5.0); break;
                    case "excludepids":
                        foreach (string part in v.Split(','))
                        {
                            int pid;
                            if (int.TryParse(part.Trim(), out pid)) s.ExcludePids.Add(pid);
                        }
                        break;
                }
            }
            return s;
        }

        private static int ParseInt(string v, int fallback)
        {
            int r;
            if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        private static double ParseDouble(string v, double fallback)
        {
            double r;
            if (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        private static bool ParseBool(string v, bool fallback)
        {
            string t = v.Trim().ToLowerInvariant();
            if (t == "true" || t == "1" || t == "yes" || t == "on") return true;
            if (t == "false" || t == "0" || t == "no" || t == "off") return false;
            return fallback;
        }
    }
}
