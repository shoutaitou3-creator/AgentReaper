using System;
using System.IO;
using System.Text;

namespace AgentReaper
{
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    string path = Config.LogPath;
                    Rotate(path);
                    string line = string.Format("{0:yyyy-MM-dd HH:mm:ss} {1}{2}",
                        DateTime.Now, message, Environment.NewLine);
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
                catch
                {
                    // ログが書けなくても本体は止めない
                }
            }
        }

        public static void Section(string title)
        {
            Write("");
            Write("===== " + title + " =====");
        }

        private static void Rotate(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                string old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch
            {
            }
        }
    }
}
