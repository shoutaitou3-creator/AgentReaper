using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentReaper
{
    internal sealed class PoolTagEntry
    {
        public string Tag;
        public uint PagedAllocs;
        public uint PagedFrees;
        public long PagedUsed;
        public uint NonPagedAllocs;
        public uint NonPagedFrees;
        public long NonPagedUsed;

        /// <summary>解放されずに残っている確保回数。リークの目安になる。</summary>
        public long NonPagedOutstanding { get { return (long)NonPagedAllocs - NonPagedFrees; } }
        public long PagedOutstanding { get { return (long)PagedAllocs - PagedFrees; } }
    }

    /// <summary>
    /// カーネルプールをタグ単位で読み出す診断。
    /// プロセスの NonpagedSystemMemory 合計で説明できない分がどのドライバー由来かを突き止める用途。
    /// </summary>
    internal static class PoolTags
    {
        private const int SystemPoolTagInformation = 22;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        // x64 の SYSTEM_POOLTAG は SIZE_T 境界に合わせて 40 バイト
        private const int EntrySize = 40;
        private const int ArrayOffset = 8;

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int infoClass, IntPtr buffer, int length, out int returnLength);

        /// <summary>成功でエントリ一覧、失敗で null（error に理由）。</summary>
        public static List<PoolTagEntry> Query(out string error)
        {
            error = null;
            int size = 1 << 20;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int needed;
                    int status = NtQuerySystemInformation(SystemPoolTagInformation, buf, size, out needed);

                    if (status == STATUS_INFO_LENGTH_MISMATCH)
                    {
                        size = Math.Max(needed + 65536, size * 2);
                        continue;
                    }
                    if (status != 0)
                    {
                        error = string.Format(
                            "NtQuerySystemInformation が NTSTATUS 0x{0:X8} を返しました" +
                            "（プールタグ機能が無効か、管理者権限が必要です）", status);
                        return null;
                    }

                    int count = Marshal.ReadInt32(buf);
                    var list = new List<PoolTagEntry>(count);

                    for (int i = 0; i < count; i++)
                    {
                        long baseOff = ArrayOffset + (long)i * EntrySize;
                        if (baseOff + EntrySize > size) break;
                        IntPtr p = new IntPtr(buf.ToInt64() + baseOff);

                        var e = new PoolTagEntry();
                        var raw = new byte[4];
                        Marshal.Copy(p, raw, 0, 4);
                        e.Tag = CleanTag(raw);
                        e.PagedAllocs = (uint)Marshal.ReadInt32(p, 4);
                        e.PagedFrees = (uint)Marshal.ReadInt32(p, 8);
                        e.PagedUsed = Marshal.ReadInt64(p, 16);
                        e.NonPagedAllocs = (uint)Marshal.ReadInt32(p, 24);
                        e.NonPagedFrees = (uint)Marshal.ReadInt32(p, 28);
                        e.NonPagedUsed = Marshal.ReadInt64(p, 32);
                        list.Add(e);
                    }
                    return list;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            error = "バッファサイズを決められませんでした";
            return null;
        }

        private static string CleanTag(byte[] raw)
        {
            var sb = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                // 最上位ビットは「保護されたプール」フラグとして使われることがあるので落とす
                int c = raw[i] & 0x7F;
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            return sb.ToString();
        }

        /// <summary>
        /// タグ文字列をドライバーのバイナリから逆引きする。
        /// タグは確保元ドライバーのイメージ内に定数として埋まっているため、
        /// .sys を全走査して一致するものを探せば所有者を推定できる（poolmon の pooltag.txt に頼らない実測）。
        /// </summary>
        public static Dictionary<string, List<string>> AttributeToDrivers(IEnumerable<string> tags, int maxFileMb)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var patterns = new List<byte[]>();
            var names = new List<string>();

            foreach (string t in tags)
            {
                if (t == null || t.Length != 4 || t.IndexOf('.') >= 0) continue;
                result[t] = new List<string>();
                names.Add(t);
                patterns.Add(Encoding.ASCII.GetBytes(t));
            }
            if (patterns.Count == 0) return result;

            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers"),
                Environment.GetFolderPath(Environment.SpecialFolder.System)
            };

            long limit = (long)maxFileMb * 1024 * 1024;

            foreach (string dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.sys"); }
                catch { continue; }

                foreach (string f in files)
                {
                    byte[] data;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length > limit) continue;
                        data = File.ReadAllBytes(f);
                    }
                    catch { continue; }

                    for (int k = 0; k < patterns.Count; k++)
                    {
                        if (Contains(data, patterns[k]))
                            result[names[k]].Add(Path.GetFileName(f));
                    }
                }
            }

            return result;
        }

        private static bool Contains(byte[] hay, byte[] needle)
        {
            int last = hay.Length - needle.Length;
            byte n0 = needle[0];
            for (int i = 0; i <= last; i++)
            {
                if (hay[i] != n0) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }
    }
}
