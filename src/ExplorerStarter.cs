using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace DragonLauncher
{
    // Shell.Application itself is an in-process COM object. Use it only to find
    // the existing Explorer desktop; ShellExecute must be invoked on that
    // desktop's Document.Application, which lives in Explorer's process.
    internal static class ExplorerStarter
    {
        internal static int Start(string root, string[] args)
        {
            if (args.Any(delegate(string value) { return value == "--status" || value == "--stop" || value == "--self-test"; }))
                throw new ArgumentException("--start-detached 不能与状态、停止或自测命令同时使用。");
            string directory = Path.GetFullPath(root);
            string executable = Path.Combine(directory, "Watch-Codex-Dragon.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到后台监视器。", executable);
            string[] forwarded = args.Where(delegate(string value) { return value != "--start-detached"; }).ToArray();
            string arguments = string.Join(" ", forwarded.Select(Quote).ToArray()) + " --desktop-broker-child";
            string expectedExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            object lookup = null, windows = null, desktop = null, document = null, application = null;
            var receipt = new Dictionary<string, object>();
            receipt["launchMode"] = "explorer_desktop_broker";
            receipt["startedUtc"] = DateTime.UtcNow.ToString("o");
            receipt["starterPid"] = Process.GetCurrentProcess().Id;
            receipt["requestedExecutable"] = executable;
            receipt["requestedWorkingDirectory"] = directory;
            receipt["watcherPid"] = null;
            receipt["watcherInJob"] = null;
            receipt["detachedConfirmed"] = false;
            receipt["requestAccepted"] = false;
            try
            {
                lookup = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                windows = Method(lookup, "Windows", new object[0]);
                object[] find = new object[] { 0, 0, 8, 0, 1 };
                var modifiers = new ParameterModifier(5); modifiers[3] = true;
                desktop = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod,
                    null, windows, find, new ParameterModifier[] { modifiers }, CultureInfo.InvariantCulture, null);
                if (desktop == null) throw new InvalidOperationException("当前登录会话没有可用的 Explorer 桌面，未退回到受宿主控制的子进程启动。");
                string browserExecutable = Convert.ToString(Property(desktop, "FullName"), CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(browserExecutable) ||
                    !string.Equals(Path.GetFullPath(browserExecutable), expectedExplorer, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("桌面 COM 对象不属于 Windows Explorer，已取消监视器启动。");
                IntPtr window = new IntPtr(Convert.ToInt64(Property(desktop, "HWND"), CultureInfo.InvariantCulture));
                uint explorerPid;
                if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out explorerPid) == 0 || explorerPid == 0)
                    throw new InvalidOperationException("无法确认 Explorer 桌面窗口所属进程，已取消监视器启动。");
                using (Process explorer = Process.GetProcessById((int)explorerPid))
                {
                    string nativePath = explorer.MainModule.FileName;
                    if (!string.Equals(Path.GetFullPath(nativePath), expectedExplorer, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("桌面窗口所属进程不是 Windows Explorer，已取消监视器启动。");
                    if (explorer.SessionId != Process.GetCurrentProcess().SessionId)
                        throw new InvalidOperationException("Explorer 桌面与启动器不属于同一登录会话，已取消监视器启动。");
                    receipt["explorerPid"] = explorer.Id;
                    receipt["explorerExecutable"] = nativePath;
                    receipt["explorerWindow"] = window.ToInt64();
                }
                document = Property(desktop, "Document");
                application = Property(document, "Application");
                if (application == null || !Marshal.IsComObject(application))
                    throw new InvalidOperationException("无法取得现存 Explorer 的启动接口，已取消监视器启动。");
                // This call is marshalled to the already-running Explorer. It
                // supplies a complete executable and working directory, with no
                // changes to official shortcuts and no Codex activation request.
                Method(application, "ShellExecute", new object[] { executable, arguments, directory, "open", 0 });
                receipt["requestAccepted"] = true;
                receipt["verification"] = "pending_monitor_heartbeat_and_job_check";
                WriteReceipt(directory, receipt);
                return 0;
            }
            catch (Exception error)
            {
                receipt["error"] = error.ToString();
                WriteReceipt(directory, receipt);
                throw;
            }
            finally
            {
                // Release the COM references obtained by this bootstrap without
                // touching Explorer's lifecycle or closing any Explorer window.
                Release(application); Release(document); Release(desktop); Release(windows); Release(lookup);
            }
        }

        private static object Property(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        }
        private static object Method(object target, string name, object[] arguments)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
        }
        private static void Release(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.ReleaseComObject(value); } catch { }
        }
        private static void WriteReceipt(string root, IDictionary<string, object> receipt)
        {
            string output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            receipt["receiptUpdatedUtc"] = DateTime.UtcNow.ToString("o");
            File.WriteAllText(Path.Combine(output, "watcher-launch.json"), new JavaScriptSerializer().Serialize(receipt), new UTF8Encoding(false));
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
