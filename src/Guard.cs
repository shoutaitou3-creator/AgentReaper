using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AgentReaper
{
    /// <summary>
    /// 設定ミスを機械で拒否する層。
    ///
    /// このツールは配布物であり、signatures.conf は利用者（またはそのエージェント）が書く。
    /// 「広すぎるパターンを書かないでください」と文章でお願いしても事故は防げないので、
    /// 危険な設定は実行時に拒否する。ここの閾値は設定ファイルから変更できない。
    ///
    /// 三層ある:
    ///   1) 静的検査   読み込み時。パターンの形だけで危険と分かるものを落とす
    ///   2) 動的検査   走査ごと。実際に何個の・どれだけ多様なプロセスに当たったかで落とす
    ///   3) 承認ゲート ドライランを通していない signatures.conf では実回収を行わない
    /// </summary>
    internal static class Guard
    {
        /// <summary>これより短いパターンは、何に当たるか書いた本人にも予測できない。</summary>
        public const int MinPatternLength = 4;

        /// <summary>1 つのシグネチャが当たってよい実行ファイル名の種類数。超えたら広すぎる。</summary>
        public const int MaxDistinctExeNames = 6;

        /// <summary>1 つのシグネチャが当たってよい全プロセスに対する割合。</summary>
        public const double MaxMatchShare = 0.15;

        /// <summary>
        /// 割合で判定するのは、プロセス表がこの数以上あるときだけ。
        /// 全体が 6 個しかない環境では 3 個当たっただけで 50% になり、割合に意味が無い。
        /// </summary>
        public const int MinProcessesForShareCheck = 50;

        /// <summary>
        /// これだけ CPU 時間を使っていて、かつ直近も動いているプロセスは「仕事中」とみなす。
        /// 放置された子プロセスの実測は CPU 0 秒だった（フック孤児 55 件が全件 0 秒）ので、
        /// この線を越えたものに当たるパターンは、本物の作業を掴んでいる。
        /// </summary>
        public const double WorkingProcessCpuSeconds = 60.0;

        /// <summary>
        /// 4 文字以上あっても致命的に広いパターン。パターン全体がこれと一致する場合だけ拒否する
        /// （部分一致で見ると "codex-cua-node" のような正当なパターンまで巻き添えになる）。
        /// </summary>
        private static readonly HashSet<string> DenyExact =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "node.exe", "nodejs", "deno", "electron",
            "python", "python.exe", "pythonw", "python3", "java", "javaw", "dotnet",
            "ruby", "perl", "php", "bash", "pwsh", "wscript", "cscript", "rundll32",
            ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".js", ".mjs", ".py",
            "node_modules", "program files", "programdata", "appdata", "users",
            "windows", "system32", "syswow64", "temp", "local", "roaming",
            "chrome", "msedge", "firefox", "brave", "opera",
            "docker", "wsl.exe", "server", "server.js", "index.js", "main.js",
            "http", "https", "localhost", "true", "false", "null"
        };

        // ---------------- 1) 静的検査 ----------------

        /// <summary>読み込み時の検査。問題なければ null、危険なら拒否理由を返す。</summary>
        public static string RejectStatic(Signature s)
        {
            if (s == null) return "シグネチャが空です";

            if (s.Field != "cmdline" && s.Field != "name" && s.Field != "path")
                return string.Format("照合先 \"{0}\" は cmdline / name / path のいずれかである必要があります", s.Field);

            string p = s.Pattern.Trim();

            if (p.Length < MinPatternLength)
                return string.Format("パターン \"{0}\" が短すぎます（{1} 文字以上が必要）。"
                    + "短いパターンは無関係なプロセスに当たります", p, MinPatternLength);

            if (DenyExact.Contains(p))
                return string.Format("パターン \"{0}\" は広すぎます。"
                    + "そのランタイム／フォルダで動く全プロセスに当たります。"
                    + "回収したいサーバーだけを特定できる文字列（パッケージ名など）にしてください", p);

            // パターンが保護対象プロセス名の一部なら、そのプロセス自体に当たる。
            // 逆向き（パターンが保護名を含む）は正当なので見ない: "codex-cua-node" は "code" を含む。
            foreach (string prot in Config.Protected)
            {
                if (prot.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return string.Format("パターン \"{0}\" は保護対象プロセス \"{1}\" に当たります。"
                        + "エディタ・端末・OS 中核プロセスは終了させられません", p, prot);
            }

            return null;
        }

        // ---------------- 2) 動的検査 ----------------

        /// <summary>
        /// 走査ごとの検査。実際に当たったプロセスを見て、広すぎるパターンを落とす。
        /// 問題なければ null、危険なら拒否理由を返す。
        /// </summary>
        public static string RejectDynamic(Signature s, List<ProcInfo> matched, int totalProcesses)
        {
            if (matched == null || matched.Count == 0) return null;

            var names = new HashSet<string>(matched.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            if (names.Count > MaxDistinctExeNames)
                return string.Format("パターン \"{0}\" が {1} 種類の実行ファイルに当たっています（上限 {2}）: {3}",
                    s.Pattern, names.Count, MaxDistinctExeNames,
                    string.Join(", ", names.OrderBy(n => n).Take(10).ToArray()));

            if (totalProcesses >= MinProcessesForShareCheck && matched.Count > totalProcesses * MaxMatchShare)
                return string.Format("パターン \"{0}\" が全 {1} プロセス中 {2} 個に当たっています（上限 {3:P0}）",
                    s.Pattern, totalProcesses, matched.Count, MaxMatchShare);

            ProcInfo working = matched.FirstOrDefault(m => m.CpuSeconds > WorkingProcessCpuSeconds);
            if (working != null)
                return string.Format("パターン \"{0}\" が CPU を {1:F0} 秒使っているプロセス {2}#{3} に当たっています。"
                    + "放置された子プロセスは CPU をほとんど使いません。仕事中のプロセスを掴んでいます",
                    s.Pattern, working.CpuSeconds, working.RawName, working.Pid);

            return null;
        }

        // ---------------- 3) 承認ゲート ----------------

        public static string ApprovalPath { get { return Path.Combine(Config.BaseDir, "approved.conf"); } }

        /// <summary>signatures.conf の内容から決まる指紋。1 文字でも変えれば別の値になる。</summary>
        public static string Fingerprint(List<Signature> sigs)
        {
            var lines = (sigs ?? new List<Signature>())
                .Select(s => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}",
                    s.Name, s.Field, s.Pattern, s.KeepNewestPerClient, s.MinAgeMinutes))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>いまの signatures.conf がドライランで承認済みか。</summary>
        public static bool IsApproved(List<Signature> sigs)
        {
            if (sigs == null || sigs.Count == 0) return true;   // 何も定義されていなければ回収も起きない

            try
            {
                if (!File.Exists(ApprovalPath)) return false;
                string want = Fingerprint(sigs);
                foreach (string raw in File.ReadAllLines(ApprovalPath))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("fingerprint", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(line.Substring(eq + 1).Trim(), want, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>ドライランを通した signatures.conf を承認済みとして記録する。</summary>
        public static void WriteApproval(List<Signature> sigs, string dryRunSummary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AgentReaper — ドライラン承認記録");
            sb.AppendLine("#");
            sb.AppendLine("# このファイルが無い、または指紋が signatures.conf と一致しない間は、");
            sb.AppendLine("# AgentReaper はプロセスを一切終了させない（ドライランに落ちる）。");
            sb.AppendLine("# signatures.conf を編集したら、--dry-run で結果を確認してから");
            sb.AppendLine("# --approve をもう一度実行すること。");
            sb.AppendLine("#");
            sb.AppendLine("# 手で書き換えないこと。指紋を偽装すれば確認していない設定で回収が走る。");
            sb.AppendLine();
            sb.AppendLine("fingerprint = " + Fingerprint(sigs));
            sb.AppendLine("approvedAt  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("# 承認時のシグネチャ");
            foreach (Signature s in sigs)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "#   {0} | {1} | {2} | keep {3} | grace {4}min",
                    s.Name, s.Field, s.Pattern, s.KeepNewestPerClient, s.MinAgeMinutes));

            if (!string.IsNullOrEmpty(dryRunSummary))
            {
                sb.AppendLine();
                sb.AppendLine("# 承認時のドライラン結果");
                foreach (string line in dryRunSummary.Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine("#   " + line);
            }

            File.WriteAllText(ApprovalPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
