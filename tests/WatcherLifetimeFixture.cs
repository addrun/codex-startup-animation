using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// A disposable parent process for lifetime tests. It never starts or stops
// Codex. Run only against a watcher copied into an isolated test directory.
internal static class WatcherLifetimeFixture
{
    private const uint JobKillOnClose = 0x2000, JobBreakawayOk = 0x0800;
    // Keep the sole, non-inheritable job handle until OS process teardown. Closing
    // it explicitly in Main would also kill this parent before it can return.
    private static IntPtr ownedJob;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--run-parent")
        {
            Console.Error.WriteLine("Usage: WatcherLifetimeFixture.exe --run-parent <isolatedWatchExe> <reportRoot>");
            return 2;
        }

        string watcher = Path.GetFullPath(args[1]);
        string reportRoot = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(reportRoot);
        var data = new Dictionary<string, object>();
        data["passed"] = false;
        data["parentPid"] = Process.GetCurrentProcess().Id;
        data["parentStartedUtc"] = DateTime.UtcNow.ToString("o");
        data["watchExe"] = watcher;
        data["jobFlags"] = JobKillOnClose | JobBreakawayOk;
        Process control = null, bootstrap = null;
        try
        {
            if (!File.Exists(watcher)) throw new FileNotFoundException("Isolated watcher missing", watcher);
            ownedJob = CreateJobObjectW(IntPtr.Zero, null);
            if (ownedJob == IntPtr.Zero) throw NativeFailure("CreateJobObjectW");
            var limits = new JobExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = JobKillOnClose | JobBreakawayOk;
            if (!SetInformationJobObject(ownedJob, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobExtendedLimits))))
                throw NativeFailure("SetInformationJobObject");
            if (!AssignProcessToJobObject(ownedJob, Process.GetCurrentProcess().Handle))
                throw NativeFailure("AssignProcessToJobObject(parent)");
            data["parentInOwnedJob"] = InJob(Process.GetCurrentProcess().Handle, ownedJob);

            string controlRoot = Path.Combine(reportRoot, "control-watcher");
            if (Directory.Exists(controlRoot)) throw new InvalidOperationException("Control directory must be fresh: " + controlRoot);
            Directory.CreateDirectory(controlRoot);
            string controlExe = Path.Combine(controlRoot, "Watch-Codex-Dragon.exe");
            File.Copy(watcher, controlExe);
            // This real control watcher matches a deliberately absent test EXE,
            // so it cannot observe or animate the user's real Codex session.
            File.WriteAllText(Path.Combine(controlRoot, "launcher.config.xml"),
                "<launcher><appUserModelId></appUserModelId><executablePath>" +
                System.Security.SecurityElement.Escape(Path.Combine(controlRoot, "NeverStartedByLifetimeFixture.exe")) +
                "</executablePath></launcher>", new UTF8Encoding(false));
            DateTime controlLaunchedUtc = DateTime.UtcNow;
            control = Process.Start(new ProcessStartInfo(controlExe, "--watch-owned=codex-dragon-animation-v1")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = controlRoot
            });
            data["controlRoot"] = controlRoot;
            data["controlExe"] = controlExe;
            data["controlPid"] = control.Id;
            data["controlStartedUtc"] = control.StartTime.ToUniversalTime().ToString("o");
            data["controlInOwnedJob"] = InJob(control.Handle, ownedJob);
            data["controlInAnyJob"] = InJob(control.Handle, IntPtr.Zero);
            if (!(bool)data["controlInOwnedJob"]) throw new InvalidOperationException("Control child did not inherit parent job");
            string controlStatusPath = Path.Combine(controlRoot, "output", "watcher-status.json");
            data["controlStatusPath"] = controlStatusPath;
            data["controlStatusBeforeParentExit"] = WaitStatus(controlStatusPath, control.Id, controlLaunchedUtc, 12000);
            if (control.HasExited) throw new InvalidOperationException("Control watcher exited before parent teardown");

            DateTime launchedUtc = DateTime.UtcNow;
            bootstrap = Process.Start(new ProcessStartInfo(watcher, "--start-detached --watch-owned=codex-dragon-animation-v1")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(watcher)
            });
            data["bootstrapPid"] = bootstrap.Id;
            data["bootstrapInOwnedJob"] = InJob(bootstrap.Handle, ownedJob);
            if (!bootstrap.WaitForExit(15000)) throw new TimeoutException("Detached-start bootstrap did not exit");
            data["bootstrapExitCode"] = bootstrap.ExitCode;
            if (bootstrap.ExitCode != 0) throw new InvalidOperationException("Detached-start bootstrap failed");

            string statusPath = Path.Combine(Path.GetDirectoryName(watcher), "output", "watcher-status.json");
            data["statusPath"] = statusPath;
            string status = WaitStatus(statusPath, 0, launchedUtc, 12000);
            int targetPid = Integer(status, "pid");
            data["watcherStatusBeforeParentExit"] = status;
            data["watcherDesktopParent"] = IsTrue(status, "desktopParent");
            data["watcherLifetimeIndependent"] = IsTrue(status, "lifetimeIndependent");
            if (!(bool)data["watcherDesktopParent"] || !(bool)data["watcherLifetimeIndependent"])
                throw new InvalidOperationException("Watcher heartbeat has not confirmed an independent Explorer parent");
            using (Process detached = Process.GetProcessById(targetPid))
            {
                data["watcherPid"] = targetPid;
                DateTime detachedStarted = detached.StartTime.ToUniversalTime();
                data["watcherStartedUtc"] = detachedStarted.ToString("o");
                data["watcherExe"] = detached.MainModule.FileName;
                if (!string.Equals(Path.GetFullPath(detached.MainModule.FileName), watcher, StringComparison.OrdinalIgnoreCase) ||
                    detachedStarted < launchedUtc.AddSeconds(-1))
                    throw new InvalidOperationException("Heartbeat does not identify this freshly detached isolated watcher");
                data["watcherInAnyJob"] = InJob(detached.Handle, IntPtr.Zero);
                data["watcherInOwnedJob"] = InJob(detached.Handle, ownedJob);
                if ((bool)data["watcherInOwnedJob"]) throw new InvalidOperationException("Watcher still belongs to the disposable parent job");
                int actualParentPid = ParentOf(targetPid);
                data["watcherParentPid"] = actualParentPid;
                if (actualParentPid == 0 || actualParentPid != Integer(status, "parentPid"))
                    throw new InvalidOperationException("Watcher parent PID differs from live heartbeat");
                using (Process parent = Process.GetProcessById(actualParentPid))
                {
                    string parentExe = parent.MainModule.FileName;
                    data["watcherParentExecutable"] = parentExe;
                    if (!string.Equals(Path.GetFullPath(parentExe),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase) ||
                        parent.SessionId != detached.SessionId || parent.StartTime.ToUniversalTime() > detachedStarted)
                        throw new InvalidOperationException("Watcher is not a child of the existing desktop Explorer");
                }
            }
            data["controlStatusBeforeParentExit"] = WaitStatus(controlStatusPath, control.Id, controlLaunchedUtc, 2000);
            data["parentWillExit"] = true;
            data["passed"] = true;
            return 0;
        }
        catch (Exception error)
        {
            data["error"] = error.ToString();
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            data["reportWrittenUtc"] = DateTime.UtcNow.ToString("o");
            File.WriteAllText(Path.Combine(reportRoot, "lifetime-parent.json"), ToJson(data), new UTF8Encoding(false));
            if (bootstrap != null) bootstrap.Dispose();
            if (control != null) control.Dispose();
            // Do not close ownedJob here. Exit closes it and kills only inherited
            // fixture children. The runner verifies the detached watcher survives.
        }
    }

    private static bool InJob(IntPtr process, IntPtr job)
    {
        bool result;
        if (!IsProcessInJob(process, job, out result)) throw NativeFailure("IsProcessInJob");
        return result;
    }
    private static int Integer(string status, string name)
    {
        Match value = Regex.Match(status, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(\\d+)");
        return value.Success ? int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
    private static bool IsTrue(string status, string name)
    {
        return Regex.IsMatch(status, "\"" + Regex.Escape(name) + "\"\\s*:\\s*true");
    }
    private static string WaitStatus(string path, int expectedPid, DateTime notBefore, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeout)
        {
            try
            {
                string status = File.ReadAllText(path);
                Match updated = Regex.Match(status, "\"updatedUtc\"\\s*:\\s*\"([^\"]+)\"");
                DateTime timestamp;
                int pid = Integer(status, "pid");
                if (IsTrue(status, "running") && pid > 0 && (expectedPid == 0 || expectedPid == pid) && updated.Success &&
                    DateTime.TryParse(updated.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out timestamp) &&
                    timestamp >= notBefore.AddSeconds(-1) && Math.Abs((DateTime.UtcNow - timestamp).TotalSeconds) <= 6)
                    return status;
            }
            catch (IOException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("No fresh running watcher heartbeat: " + path);
    }
    private static int ParentOf(int pid)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw NativeFailure("CreateToolhelp32Snapshot");
        try
        {
            var entry = new ProcessEntry(); entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
            if (!Process32FirstW(snapshot, ref entry)) throw NativeFailure("Process32FirstW");
            do { if (entry.ProcessId == pid) return (int)entry.ParentProcessId; } while (Process32NextW(snapshot, ref entry));
            return 0;
        }
        finally { CloseHandle(snapshot); }
    }
    private static Exception NativeFailure(string operation)
    {
        return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
    private static string Quote(string argument)
    {
        // These are absolute Windows paths; double only trailing backslashes so
        // the quote is not swallowed by Windows command-line parsing.
        if (argument.IndexOf('"') >= 0) throw new ArgumentException("A file path cannot contain a quote");
        int trailing = argument.Length - argument.TrimEnd('\\').Length;
        return "\"" + argument + new string('\\', trailing) + "\"";
    }
    private static string ToJson(object value)
    {
        if (value == null) return "null";
        if (value is bool) return (bool)value ? "true" : "false";
        if (value is int || value is uint || value is long) return Convert.ToString(value, CultureInfo.InvariantCulture);
        var dictionary = value as IDictionary<string, object>;
        if (dictionary != null)
        {
            var parts = new List<string>();
            foreach (var item in dictionary) parts.Add(ToJson(item.Key) + ":" + ToJson(item.Value));
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }
        var text = new StringBuilder("\"");
        foreach (char character in Convert.ToString(value, CultureInfo.InvariantCulture))
        {
            if (character == '\\' || character == '"') text.Append('\\').Append(character);
            else if (character == '\r') text.Append("\\r");
            else if (character == '\n') text.Append("\\n");
            else if (character == '\t') text.Append("\\t");
            else if (character < 32) text.Append("\\u").Append(((int)character).ToString("x4"));
            else text.Append(character);
        }
        return text.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
