using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentReaper
{
    /// <summary>
    /// /target:winexe でビルドしているのでコンソールを持たない。
    /// それでも `AgentReaper.exe --diagnose --json | jq` のようにパイプで使えるよう、
    /// 呼び出し元のコンソールへ後付けで接続する。
    ///
    /// 接続できない場合（GUI から起動された等）でも呼び出し側はファイルを読めるので、
    /// ここは失敗しても構わない。
    /// </summary>
    internal static class ConsoleOut
    {
        private const int AttachParentProcess = -1;
        private static bool _attached;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        public static bool TryAttach()
        {
            if (_attached) return true;
            try
            {
                if (!AttachConsole(AttachParentProcess)) return false;

                Stream stdout = Console.OpenStandardOutput();
                if (stdout == Stream.Null) return false;

                var writer = new StreamWriter(stdout, new UTF8Encoding(false));
                writer.AutoFlush = true;
                Console.SetOut(writer);
                _attached = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>コンソールがあれば書く。無ければ何もしない。</summary>
        public static void Write(string text)
        {
            if (!TryAttach()) return;
            try { Console.Out.Write(text); }
            catch { }
        }
    }
}
