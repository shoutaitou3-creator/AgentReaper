using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AgentReaper
{
    // Deterministic process fixtures only. Every Execute call uses dryRun: true.
    internal static class ReaperSafetyTests
    {
        private static int checks;
        private static readonly Signature Sig = new Signature
        { Name = "mcp", Field = "cmdline", Pattern = "fixture-mcp", KeepNewestPerClient = 2, MinAgeMinutes = 30 };

        private static ProcInfo P(int pid, string name, int age, ProcInfo parent = null)
        {
            var p = new ProcInfo { Pid = pid, Name = name, RawName = name + ".exe", Created = DateTime.Now.AddMinutes(-age),
                CommandLine = "fixture-mcp", Parent = parent, CpuSeconds = 1, ExePath = "fixture", WorkingSet = 1024 };
            if (parent != null) parent.Children.Add(p);
            return p;
        }

        private static Reaper Ready(params ProcInfo[] processes)
        {
            ProcessScanner.Map = processes.ToDictionary(p => p.Pid);
            var reaper = new Reaper();
            reaper.Signatures.Add(Sig);
            for (int i = 0; i <= reaper.Settings.IdleScansRequired; i++) reaper.Plan();
            return reaper;
        }

        private static ReapTarget Target(ProcInfo root, params ProcInfo[] tree)
        {
            return new ReapTarget { Root = root, Sig = Sig, Tree = new List<ProcInfo>(tree.Length == 0 ? new[] { root } : tree) };
        }

        private static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }

        private static ReapResult Execute(Reaper reaper, ReapTarget target)
        {
            var plan = new ReapPlan();
            plan.Kill.Add(target);
            return reaper.Execute(plan, true);
        }

        public static void Main()
        {
            var client = P(10001, "codex", 500);
            var wrapper = P(10002, "cmd", 490, client);
            var a = P(10003, "node", 60, wrapper);
            var b = P(10004, "node", 90, wrapper);
            var c = P(10005, "node", 120, wrapper);
            var child = P(10006, "node", 110, c);
            child.CommandLine = "child";
            var reaper = Ready(client, wrapper, a, b, c, child);
            Check(reaper.LastPlan.Kill.Count == 0 && reaper.LastPlan.Keep.Count == 3,
                "all live codex MCP trees protected after repeated idle scans, including third oldest");
            Check(reaper.LastPlan.Keep.All(t => t.Reason.Contains("codex.exe#10001")), "live client is the protection reason");
            Check(Execute(reaper, Target(c, c, child)).ProcessesKilled == 0,
                "Execute rejects stale plan with missing Client using fresh ancestry");
            var forged = Target(a, a, P(10009, "node", 120));
            Check(Execute(reaper, forged).TargetsKilled == 0, "one live member protects entire forged tree");

            a.Parent = null; b.Parent = null; c.Parent = null;
            reaper = Ready(a, b, c, child);
            Check(reaper.LastPlan.Kill.Count == 1 && reaper.LastPlan.Kill[0].Root == c,
                "old idle orphan third tree remains reclaimable");
            Check(reaper.LastPlan.Keep.Count == 2, "orphan newest two still protected");
            Check(Execute(reaper, Target(c, c, child)).ProcessesKilled == 2, "orphan dry-run still counts both members");
            var cachedClient = Target(c, c, child); cachedClient.Client = client;
            Check(Execute(reaper, cachedClient).ProcessesKilled == 0, "Execute retains client-protected plan even if client exits later");

            var why = typeof(Reaper).GetMethod("WhyKeep", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<ReapTarget, string> reason = t => (string)why.Invoke(reaper, new object[] { t, 2 });
            c.Created = DateTime.Now.AddMinutes(-10);
            Check(reason(Target(c)) != null, "orphan age grace still protects");
            c.Created = DateTime.Now.AddMinutes(-120);
            child.CpuSeconds += 1; reaper.Plan();
            Check(reason(Target(c, c, child)) != null, "busy orphan child protects full tree");
            for (int i = 0; i < reaper.Settings.IdleScansRequired; i++) reaper.Plan();
            Check(reason(Target(c, c, child)) == null, "orphan becomes eligible only after idle history recovers");
            reaper.Settings.ExcludePids.Add(child.Pid);
            Check(reason(Target(c, c, child)) != null, "excluded descendant protects planned tree");
            Check(Execute(reaper, Target(child)).ProcessesKilled == 0, "Execute excluded PID guard preserved");
            reaper.Settings.ExcludePids.Clear();
            child.Name = "powershell";
            Check(reason(Target(c, c, child)) != null, "protected descendant protects planned tree");
            Check(Execute(reaper, Target(child)).ProcessesKilled == 0, "Execute protected name guard preserved");
            child.Name = "node";

            var reused = P(c.Pid, "node", 1);
            ProcessScanner.Map[c.Pid] = reused;
            Check(Execute(reaper, Target(c)).ProcessesKilled == 0, "Execute refuses PID reuse even during dry run");
            ProcessScanner.Map.Remove(c.Pid);
            Check(Execute(reaper, Target(c)).ProcessesKilled == 0, "Execute skips disappeared PID");
            var self = P(System.Diagnostics.Process.GetCurrentProcess().Id, "node", 120);
            ProcessScanner.Map[self.Pid] = self;
            Check(Execute(reaper, Target(self)).ProcessesKilled == 0, "Execute self PID guard preserved");

            GuardChecks();
            ApprovalGateChecks();
            Console.WriteLine("PASS " + checks + " checks; no real process was terminated");
        }

        private static Signature S(string pattern, string field)
        {
            return new Signature
            { Name = "t", Field = field, Pattern = pattern, KeepNewestPerClient = 2, MinAgeMinutes = 30 };
        }

        private static Signature S(string pattern) { return S(pattern, "cmdline"); }

        // The guard limits are what make this safe to hand to a stranger's coding agent.
        // They must reject bad configuration without being asked to.
        private static void GuardChecks()
        {
            Check(Guard.RejectStatic(S("npx")) != null, "guard rejects a pattern shorter than the minimum");
            Check(Guard.RejectStatic(S("node")) != null, "guard rejects the generic runtime name node");
            Check(Guard.RejectStatic(S("python")) != null, "guard rejects the generic runtime name python");
            Check(Guard.RejectStatic(S("server.js")) != null, "guard rejects the generic entry point server.js");
            Check(Guard.RejectStatic(S("claude")) != null, "guard rejects a pattern matching a protected client");
            Check(Guard.RejectStatic(S("code")) != null, "guard rejects a pattern matching a protected editor");
            Check(Guard.RejectStatic(S("mcpvault", "pid")) != null, "guard rejects an unknown match field");
            Check(Guard.RejectStatic(S("mcpvault")) == null, "guard accepts a specific package name");
            Check(Guard.RejectStatic(S("codex-cua-node")) == null,
                "guard accepts a specific pattern that merely contains a generic word");

            // Too many distinct executables: the pattern caught something it should not have.
            var wide = new List<ProcInfo>();
            for (int i = 0; i < 8; i++) wide.Add(P(20000 + i, "exe" + i, 120));
            Check(Guard.RejectDynamic(S("mcpvault"), wide, 800) != null,
                "guard disables a pattern spanning too many distinct executables");

            // Same count, one executable, small process table: the share rule must not fire.
            var narrow = new List<ProcInfo>();
            for (int i = 0; i < 8; i++) narrow.Add(P(21000 + i, "node", 120));
            Check(Guard.RejectDynamic(S("mcpvault"), narrow, 20) == null,
                "guard does not apply the share rule to a small process table");
            Check(Guard.RejectDynamic(S("mcpvault"), narrow, 800) == null,
                "guard accepts a narrow pattern on a large process table");

            var many = new List<ProcInfo>();
            for (int i = 0; i < 40; i++) many.Add(P(22000 + i, "node", 120));
            Check(Guard.RejectDynamic(S("mcpvault"), many, 100) != null,
                "guard disables a pattern matching too large a share of all processes");

            var busy = new List<ProcInfo> { P(23001, "node", 120) };
            busy[0].CpuSeconds = Guard.WorkingProcessCpuSeconds + 1;
            Check(Guard.RejectDynamic(S("mcpvault"), busy, 800) != null,
                "guard disables a pattern that caught a process doing real work");
        }

        // Reaping must be impossible until a human has looked at a dry run.
        private static void ApprovalGateChecks()
        {
            var sigs = new List<Signature> { S("fixture-approval") };
            string path = Guard.ApprovalPath;
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

            Check(!Guard.IsApproved(sigs), "unapproved signatures are not approved");
            Check(Guard.IsApproved(new List<Signature>()),
                "an empty signature set needs no approval (nothing can be reaped)");

            Guard.WriteApproval(sigs, "fixture");
            Check(Guard.IsApproved(sigs), "approval is recognised after --approve");

            sigs[0].MinAgeMinutes = 31;
            Check(!Guard.IsApproved(sigs), "editing a signature revokes the approval");

            sigs[0].MinAgeMinutes = 30;
            Check(Guard.IsApproved(sigs), "reverting the edit restores the approval");

            Check(!Guard.IsApproved(new List<Signature> { S("fixture-approval"), S("fixture-second") }),
                "adding a signature revokes the approval");

            System.IO.File.Delete(path);
            Check(!Guard.IsApproved(sigs), "deleting the approval file revokes the approval");
        }
    }

    // Only OS inspection is replaced. The production Reaper.cs and Config.cs are compiled unchanged.
    internal sealed class ProcInfo
    {
        public int Pid;
        public string Name, RawName, ExePath, CommandLine;
        public DateTime Created;
        public double CpuSeconds;
        public long WorkingSet;
        public ProcInfo Parent;
        public readonly List<ProcInfo> Children = new List<ProcInfo>();
        public TimeSpan Age { get { return DateTime.Now - Created; } }
    }
    internal sealed class MemSnapshot { }
    internal static class ProcessScanner
    {
        public static Dictionary<int, ProcInfo> Map;
        public static Dictionary<int, ProcInfo> Snapshot() { return Map; }
        public static MemSnapshot Memory() { return new MemSnapshot(); }
        public static IEnumerable<ProcInfo> Descendants(ProcInfo p)
        { return p.Children.SelectMany(c => new[] { c }.Concat(Descendants(c))); }
        public static ProcInfo ClientAncestor(ProcInfo p)
        {
            for (var parent = p.Parent; parent != null; parent = parent.Parent)
                if (Config.ClientAnchors.Contains(parent.Name)) return parent;
            return null;
        }
    }
    internal static class Native
    {
        public const int MemoryFlushModifiedList = 1, MemoryPurgeLowPriorityStandbyList = 2,
            MemoryPurgeStandbyList = 3, MemoryEmptyWorkingSets = 4;
        public static string MemoryListCommand(int command) { throw new Exception("Native operation forbidden in test"); }
    }
    internal static class Log { public static void Write(string message) { throw new Exception(message); } }
}
