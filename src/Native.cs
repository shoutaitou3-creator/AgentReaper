using System;
using System.Runtime.InteropServices;

namespace AgentReaper
{
    /// <summary>
    /// Win32 / NT ネイティブ API のラッパー。
    /// メモリリスト操作は NtSetSystemInformation(SystemMemoryListInformation, ...) 経由。
    /// </summary>
    internal static class Native
    {
        // ---- SYSTEM_MEMORY_LIST_COMMAND (ntexapi.h) ----
        public const int MemoryCaptureAccessedBits = 0;
        public const int MemoryCaptureAndResetAccessedBits = 1;
        public const int MemoryEmptyWorkingSets = 2;
        public const int MemoryFlushModifiedList = 3;
        public const int MemoryPurgeStandbyList = 4;
        public const int MemoryPurgeLowPriorityStandbyList = 5;

        private const int SystemMemoryListInformation = 0x50;

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;

        private const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
        private const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privilege0;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
            ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previous, IntPtr returnLength);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>
        /// OS から見える物理メモリの総量（GB）。取得できなければ 0。
        /// しきい値を搭載量に合わせて決めるために使う。設定読み込みのたびに呼ぶので、
        /// WMI ではなく GlobalMemoryStatusEx を使う（プロセス列挙が要らず即座に返る）。
        /// </summary>
        public static double TotalPhysicalGb()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (!GlobalMemoryStatusEx(ref m)) return 0;
                return m.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>GetHicon() で作った HICON を解放する（GDI ハンドルリーク防止）。</summary>
        public static void ReleaseIcon(IntPtr handle)
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }

        /// <summary>指定した特権を現在のプロセストークンで有効化する。</summary>
        private static bool EnablePrivilege(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                    return false;

                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    return false;

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privilege0.Luid = luid;
                tp.Privilege0.Attributes = SE_PRIVILEGE_ENABLED;

                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;

                // AdjustTokenPrivileges は一部だけ成功しても TRUE を返す。
                return Marshal.GetLastWin32Error() == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>
        /// メモリリスト操作を実行する。成功で null、失敗時は理由文字列を返す。
        /// 管理者権限（SeProfileSingleProcessPrivilege）が必要。
        /// </summary>
        public static string MemoryListCommand(int command)
        {
            if (!EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME))
                return "特権 SeProfileSingleProcessPrivilege を有効化できません（管理者として実行してください）";

            EnablePrivilege(SE_INCREASE_QUOTA_NAME);

            int cmd = command;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
            if (status != 0)
                return string.Format("NtSetSystemInformation が NTSTATUS 0x{0:X8} を返しました", status);

            return null;
        }

        /// <summary>単一プロセスのワーキングセットを切り詰める（既定では使わない）。</summary>
        public static bool TrimWorkingSet(IntPtr processHandle)
        {
            try { return EmptyWorkingSet(processHandle); }
            catch { return false; }
        }
    }
}
