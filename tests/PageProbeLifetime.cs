using System;
using System.Diagnostics;
using System.Threading;
using DragonLauncher;

// Uses a deliberately non-answering isolated probe stub; never touches Codex.
public static class PageProbeLifetime
{
    public static int Main(string[] args)
    {
        bool deadline, cancel;
        using (var client = new PageReadinessClient(args[0]))
        {
            var clock = Stopwatch.StartNew();
            PageProbeResult result = client.Check(new IntPtr(1));
            deadline = !result.Ready && result.Detail == "probe_timeout" && clock.ElapsedMilliseconds >= 1700 && clock.ElapsedMilliseconds < 3500;
            Console.WriteLine((deadline ? "PASS" : "FAIL") + ": blocked-provider-deadline; milliseconds=" + clock.ElapsedMilliseconds);
        }
        var pending = new PageReadinessClient(args[0]);
        var thread = new Thread(delegate() { pending.Check(new IntPtr(1)); });
        thread.Start();
        Thread.Sleep(250);
        var close = Stopwatch.StartNew();
        pending.Dispose();
        cancel = thread.Join(1000) && close.ElapsedMilliseconds < 1000;
        Console.WriteLine((cancel ? "PASS" : "FAIL") + ": close-cancels-blocked-provider; milliseconds=" + close.ElapsedMilliseconds);
        return deadline && cancel ? 0 : 1;
    }
}
