using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DragonLauncher
{
    // Some Windows shell launches belong to an OS job even when they are
    // independent of Codex. Verify the real Explorer parent as well as the job
    // flags, rather than treating all job membership as a failure.
    internal static class WatcherLifetime
    {
        internal sealed class Snapshot
        {
            public bool InJob, DesktopParent, Independent;
            public uint? JobLimitFlags;
            public int ParentPid, QueryError;
            public string ParentExecutable;
        }

        internal static Snapshot Current()
        {
            var result = new Snapshot();
            bool member;
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out member))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查监视器的进程生命周期。");
            result.InJob = member;
            if (member)
            {
                BasicJobLimits limits;
                uint returned;
                if (QueryInformationJobObject(IntPtr.Zero, 2, out limits, (uint)Marshal.SizeOf(typeof(BasicJobLimits)), out returned))
                    result.JobLimitFlags = limits.LimitFlags;
                else result.QueryError = Marshal.GetLastWin32Error();
            }
            result.ParentPid = ParentPid();
            try
            {
                using (var parent = Process.GetProcessById(result.ParentPid))
                using (var current = Process.GetCurrentProcess())
                {
                    result.ParentExecutable = parent.MainModule.FileName;
                    result.DesktopParent = parent.StartTime <= current.StartTime && parent.SessionId == current.SessionId &&
                        string.Equals(result.ParentExecutable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                            StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            result.Independent = !result.InJob || result.DesktopParent && result.JobLimitFlags.HasValue && (result.JobLimitFlags.Value & 0x2000) == 0;
            return result;
        }

        private static int ParentPid()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) return 0;
            try
            {
                var entry = new ProcessEntry(); entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                int current = Process.GetCurrentProcess().Id;
                if (!Process32First(snapshot, ref entry)) return 0;
                do { if (entry.ProcessId == current) return (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
                return 0;
            }
            finally { CloseHandle(snapshot); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
            out BasicJobLimits information, uint length, out uint returned);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
