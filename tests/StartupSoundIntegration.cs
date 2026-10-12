using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Threading;
using DragonLauncher;

// Loads StartupSound from the built production assembly. Original audio is only
// inspected; every Play() case uses newly generated, entirely silent PCM data.
public static class StartupSoundIntegration
{
    private sealed class CheckResult
    {
        public string name;
        public bool passed;
        public string detail;
        public List<string> events = new List<string>();
        public Dictionary<string, object> measurements = new Dictionary<string, object>();
        public void Record(string eventName, string message) { events.Add(eventName + "|" + message); }
    }

    private sealed class Pcm
    {
        public int rate, channels, frames;
        public short[] samples;
        public double Duration { get { return frames / (double)rate; } }
    }

    private static readonly List<CheckResult> results = new List<CheckResult>();
    private static readonly FieldInfo playerField = typeof(StartupSound).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic);
    private const double CueSeconds = 8.30;
    private const double SilentEndingSeconds = 0.05;
    private const double PlaybackVolume = 0.44;
    private const int ExpectedCheckCount = 24;
    private static string projectRoot, runRoot, audioPath, silentPath;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action<CheckResult> body)
    {
        var result = new CheckResult { name = name };
        results.Add(result);
        try { body(result); result.passed = true; result.detail = "verified"; }
        catch (Exception error) { result.passed = false; result.detail = error.GetType().Name + ": " + error.Message; }
        Console.WriteLine((result.passed ? "PASS" : "FAIL") + ": " + name + "; " + result.detail);
    }

    private static int Count(CheckResult result, string name)
    {
        int count = 0;
        foreach (string entry in result.events)
            if (entry.StartsWith(name + "|", StringComparison.Ordinal)) count++;
        return count;
    }

    private static bool Wait(Func<bool> complete, int milliseconds)
    {
        if (complete()) return true;
        var clock = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        EventHandler tick = delegate
        {
            if (complete() || clock.ElapsedMilliseconds >= milliseconds)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Tick += tick;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= tick; }
        return complete();
    }

    private static void Pump(int milliseconds) { Wait(delegate { return false; }, milliseconds); }

    private static MediaPlayer Player(StartupSound sound)
    {
        Require(playerField != null, "Cannot verify decoder ownership in the production assembly.");
        return (MediaPlayer)playerField.GetValue(sound);
    }

    private static void RequireTimersStopped(StartupSound sound)
    {
        int inspected = 0;
        foreach (FieldInfo field in typeof(StartupSound).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (!typeof(DispatcherTimer).IsAssignableFrom(field.FieldType)) continue;
            var timer = (DispatcherTimer)field.GetValue(sound);
            if (timer == null) continue;
            inspected++;
            Require(!timer.IsEnabled, "The audio owner retained an active timer after releasing playback: " + field.Name);
        }
        Require(inspected >= 1, "The timer ownership check inspected no DispatcherTimer.");
    }

    private static Settings SilentSettings()
    {
        return new Settings { SoundEnabled = true, SoundVolume = PlaybackVolume, SoundFile = silentPath };
    }

    private static void AwaitOpen(StartupSound sound, CheckResult result)
    {
        Require(Wait(delegate { return Count(result, "sound_opened") > 0 || Count(result, "sound_failed") > 0; }, 3000),
            "Silent PCM did not finish loading asynchronously within 3 seconds.");
        Require(Count(result, "sound_opened") == 1 && Count(result, "sound_failed") == 0,
            "Silent PCM decoder failed: " + string.Join("; ", result.events.ToArray()));
        Require(!sound.IsPlaying, "Preloading unexpectedly started playback.");
        Player(sound).IsMuted = true;
    }

    private static void StartSilent(StartupSound sound, CheckResult result, double position, double videoSeconds = 8)
    {
        AwaitOpen(sound, result);
        sound.TrackVideo(position, videoSeconds);
        // TrackVideo enables audio according to config. This fixture still has
        // zero samples throughout, and is additionally muted after synchronization.
        MediaPlayer player = Player(sound);
        if (player != null) player.IsMuted = true;
        Require(sound.IsPlaying && Count(result, "sound_started") == 1, "The silent cue did not enter its one-shot playback state.");
    }

    private static void WriteSilent(string path)
    {
        const int rate = 44100, channels = 2, frames = 366030;
        int byteCount = frames * channels * 2;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + byteCount);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
            writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(byteCount);
            writer.Write(new byte[byteCount]);
        }
        var verify = ReadPcm(path);
        foreach (short sample in verify.samples) Require(sample == 0, "The playback fixture contains nonzero audio.");
    }

    private static Pcm ReadPcm(string path)
    {
        int tag = 0, channels = 0, rate = 0, bits = 0;
        byte[] data = null;
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            Require(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "RIFF", "WAV RIFF header is missing.");
            reader.ReadUInt32();
            Require(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "WAVE", "WAV type is invalid.");
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
                int length = checked((int)reader.ReadUInt32());
                long next = reader.BaseStream.Position + length;
                Require(next <= reader.BaseStream.Length, "WAV chunk is truncated.");
                if (chunk == "fmt ")
                {
                    Require(length >= 16, "WAV format chunk is incomplete.");
                    tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                    reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                }
                else if (chunk == "data") data = reader.ReadBytes(length);
                reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, next + (length & 1));
            }
        }
        Require(tag == 1 && channels == 2 && rate == 44100 && bits == 16 && data != null && data.Length % 4 == 0,
            "Expected complete stereo 44.1 kHz PCM16 WAV data.");
        var samples = new short[data.Length / 2];
        Buffer.BlockCopy(data, 0, samples, 0, data.Length);
        return new Pcm { rate = rate, channels = channels, samples = samples, frames = samples.Length / channels };
    }

    private static Dictionary<string, object> Measure(Pcm pcm, double begin, double end)
    {
        int first = (int)Math.Round(begin * pcm.rate) * pcm.channels;
        int last = Math.Min(pcm.samples.Length, (int)Math.Round(end * pcm.rate) * pcm.channels);
        double sum = 0, peak = 0;
        for (int index = first; index < last; index++)
        {
            double sample = pcm.samples[index] / 32768.0;
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return new Dictionary<string, object> {
            { "startSeconds", begin }, { "endSeconds", end }, { "peak", peak },
            { "rms", last > first ? Math.Sqrt(sum / (last - first)) : 0 }
        };
    }

    private static Dictionary<string, object> StrongestWindow(Pcm pcm)
    {
        const double length = 0.10, step = 0.01;
        double strongest = 0, center = 0;
        for (int index = 0; index <= (int)Math.Round((pcm.Duration - length) / step); index++)
        {
            double begin = index * step;
            double rms = (double)Measure(pcm, begin, begin + length)["rms"];
            if (rms <= strongest) continue;
            strongest = rms;
            center = begin + length / 2;
        }
        return new Dictionary<string, object> {
            { "windowSeconds", length }, { "stepSeconds", step }, { "centerSeconds", center }, { "rms", strongest }
        };
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Expected project-root, audio-path, output-root, report-path."); return 2; }
        projectRoot = args[0]; audioPath = args[1]; runRoot = args[2];
        silentPath = Path.Combine(runRoot, "silent-fixture.wav");
        try
        {
            WriteSilent(silentPath);
            Run("welcome-pcm-8.30s-tail-and-44pct-playback", delegate(CheckResult result)
            {
                Pcm pcm = ReadPcm(audioPath);
                Require(Math.Abs(pcm.Duration - CueSeconds) < 1.0 / pcm.rate, "The selected welcome cue must be exactly 8.30 seconds.");
                var whole = Measure(pcm, 0, pcm.Duration);
                var line = Measure(pcm, 0, 2.25);
                var sweep = Measure(pcm, 2.25, 3.25);
                var eyes = Measure(pcm, 3.25, 4.75);
                var decay = Measure(pcm, 4.75, pcm.Duration - SilentEndingSeconds);
                var ending = Measure(pcm, pcm.Duration - SilentEndingSeconds, pcm.Duration);
                var afterVideoEnd = Measure(pcm, 8.00, 8.25);
                var strongest = StrongestWindow(pcm);
                double peak = (double)whole["peak"], rms = (double)whole["rms"];
                Require(peak > 0.35 && peak <= 0.85 && rms > 0.018 && rms < 0.14, "Mixed audio loudness/headroom is outside its intended range.");
                Require((double)line["rms"] > 0.015 && (double)sweep["rms"] > 0.015 && (double)eyes["rms"] > 0.015,
                    "One of the three animation stages has no meaningful energy.");
                Require((double)sweep["rms"] > (double)line["rms"] && (double)eyes["rms"] > (double)sweep["rms"],
                    "Tracing, material reveal, and eyes/title stages do not build in intensity.");
                Require((double)decay["rms"] > 0.01 && (double)decay["rms"] < (double)eyes["rms"] * 0.5,
                    "The final portrait stage has lost its quieter natural release.");
                Require((double)strongest["centerSeconds"] >= 4.40 && (double)strongest["centerSeconds"] <= 4.55,
                    "The strongest 100 ms sound window is not aligned with the eyes/title emphasis near 4.46 seconds.");
                Require((double)Measure(pcm, 0, 0.02)["peak"] == 0 && (double)ending["peak"] == 0,
                    "The first 20 ms or final 50 ms is not exactly silent.");
                Require((double)afterVideoEnd["rms"] > 0.00005 && (double)afterVideoEnd["peak"] > 0,
                    "The selected cue has lost its natural release after the 8-second video duration.");
                int clipped = 0;
                double largestStep = 0;
                foreach (short sample in pcm.samples) if (Math.Abs((int)sample) >= 32767) clipped++;
                for (int index = pcm.channels; index < pcm.samples.Length; index++)
                    largestStep = Math.Max(largestStep, Math.Abs(pcm.samples[index] - pcm.samples[index - pcm.channels]) / 32768.0);
                Require(clipped == 0 && largestStep < 0.18, "The cue clips or has an excessive adjacent-sample step.");
                result.measurements["durationSeconds"] = pcm.Duration;
                result.measurements["wholeTrack"] = whole;
                result.measurements["lineFormation"] = line;
                result.measurements["materialSweep"] = sweep;
                result.measurements["eyesOpening"] = eyes;
                result.measurements["naturalRelease"] = decay;
                result.measurements["strongest100msWindow"] = strongest;
                result.measurements["silentEnding"] = ending;
                result.measurements["releaseAfterVideoEnd"] = afterVideoEnd;
                result.measurements["clippedSamples"] = clipped;
                result.measurements["largestSameChannelStep"] = largestStep;
                double volume = Settings.Load(projectRoot).SoundVolume;
                Require(Math.Abs(volume - PlaybackVolume) < 0.000001,
                    "The candidate must apply the selected 44 percent gain in the player, not bake it a second time.");
                result.measurements["configuredVolume"] = volume;
                result.measurements["rmsAtConfiguredVolume"] = rms * volume;
            });
            Run("disabled-sound-never-opens-a-decoder", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, new Settings { SoundEnabled = false }, result.Record))
                {
                    sound.TrackVideo(2, 8);
                    sound.ContinueAfterVideo(7.75, 8); Pump(40);
                    int callbacks = 0; sound.FadeOut(delegate { callbacks++; });
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_open_requested") == 0 && callbacks == 1,
                        "Disabled sound opened media, played, or failed to complete its fade callback.");
                }
            });
            Run("zero-volume-never-opens-a-decoder", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, new Settings { SoundEnabled = true, SoundVolume = 0 }, result.Record))
                {
                    sound.TrackVideo(2, 8);
                    sound.ContinueAfterVideo(7.75, 8); Pump(40);
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_open_requested") == 0,
                        "Zero volume opened or played a decoder.");
                }
            });
            Run("missing-audio-does-not-block-and-completes-callback", delegate(CheckResult result)
            {
                var clock = Stopwatch.StartNew();
                using (var sound = new StartupSound(runRoot, new Settings { SoundFile = "missing-audio.wav" }, result.Record))
                {
                    sound.TrackVideo(2, 8); int callbacks = 0;
                    sound.ContinueAfterVideo(7.75, 8);
                    sound.FadeOut(delegate { callbacks++; });
                    Require(!sound.IsPlaying && Player(sound) == null && callbacks == 1 && Count(result, "sound_failed") == 1,
                        "Missing audio did not remain silent or did not complete its callback.");
                    RequireTimersStopped(sound);
                }
                result.measurements["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds;
                Require(clock.ElapsedMilliseconds < 1000, "A missing audio file blocked startup.");
            });
            Run("decoder-error-remains-silent", delegate(CheckResult result)
            {
                string path = Path.Combine(runRoot, "corrupt-fixture.wav");
                File.WriteAllText(path, "Intentional invalid WAV for decoder failure.", Encoding.ASCII);
                using (var sound = new StartupSound(runRoot, new Settings { SoundFile = path }, result.Record))
                {
                    Require(Wait(delegate { return Count(result, "sound_failed") > 0; }, 3000), "Invalid WAV did not report its decoder failure.");
                    sound.TrackVideo(1, 8); int callbacks = 0; sound.FadeOut(delegate { callbacks++; });
                    Require(!sound.IsPlaying && Player(sound) == null && callbacks == 1 && Count(result, "sound_started") == 0,
                        "A decoder error played media, retained its decoder, or left its callback pending.");
                }
            });
            Run("asynchronous-preload-waits-for-video-progress", delegate(CheckResult result)
            {
                var clock = Stopwatch.StartNew();
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    result.measurements["constructorMilliseconds"] = clock.Elapsed.TotalMilliseconds;
                    Require(clock.ElapsedMilliseconds < 500, "Opening PCM synchronously blocked the UI.");
                    AwaitOpen(sound, result); Pump(40);
                    Require(!sound.IsPlaying && Count(result, "sound_started") == 0, "Preload played before any video progress.");
                }
            });
            Run("late-open-seeks-to-current-video-position", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    sound.TrackVideo(3.8, 8);
                    Require(Wait(delegate { return Count(result, "sound_opened") > 0 || Count(result, "sound_failed") > 0; }, 3000),
                        "A late open never reached a terminal state.");
                    // If loading exceeded the progress freshness threshold, feed
                    // the actual current timestamp again, as the video host does.
                    if (!sound.IsPlaying) sound.TrackVideo(3.9, 8);
                    MediaPlayer player = Player(sound);
                    if (player != null) player.IsMuted = true;
                    Require(sound.IsPlaying && player != null, "A late PCM open did not follow the current video timestamp.");
                    double actual = player.Position.TotalSeconds;
                    Require(actual >= 3.70 && actual < 4.15 && Count(result, "sound_started") == 1,
                        "Late audio restarted at zero or requested playback multiple times.");
                    result.measurements["playerPositionSeconds"] = actual;
                }
            });
            Run("late-open-after-cue-end-never-replays", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    sound.TrackVideo(CueSeconds, CueSeconds);
                    Require(Wait(delegate { return Count(result, "sound_finished") > 0 || Count(result, "sound_failed") > 0; }, 3000),
                        "A late open remained pending after the cue had ended.");
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_started") == 0 && Count(result, "sound_finished") == 1,
                        "Late opening after the cue's duration replayed or retained audio.");
                }
            });
            Run("natural-end-is-one-shot-and-releases-decoder", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, CueSeconds - 0.20, CueSeconds);
                    Require(Wait(delegate { return Count(result, "sound_finished") > 0; }, 1200), "Silent one-shot audio did not reach its natural end.");
                    sound.TrackVideo(CueSeconds, CueSeconds); sound.TrackVideo(0.20, CueSeconds); Pump(50);
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_started") == 1 && Count(result, "sound_open_requested") == 1,
                        "An ended audio cue replayed or kept its decoder.");
                    RequireTimersStopped(sound);
                }
            });
            Run("fade-completes-near-100ms-and-releases-decoder", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 2.0);
                    var clock = Stopwatch.StartNew(); int callbacks = 0; double elapsed = -1;
                    sound.FadeOut(delegate { callbacks++; elapsed = clock.Elapsed.TotalMilliseconds; });
                    Require(Wait(delegate { return callbacks > 0; }, 600), "Fade completion stayed pending.");
                    Pump(50);
                    Require(callbacks == 1 && !sound.IsPlaying && Player(sound) == null && elapsed >= 80 && elapsed < 300,
                        "Fade repeated, failed to release media, or exceeded its expected duration.");
                    result.measurements["completionMilliseconds"] = elapsed;
                }
            });
            Run("reentrant-fade-completes-every-callback-once", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 2.0);
                    var clock = Stopwatch.StartNew(); int first = 0, second = 0, third = 0; double elapsed = -1;
                    sound.FadeOut(delegate { first++; elapsed = clock.Elapsed.TotalMilliseconds; });
                    sound.FadeOut(delegate { second++; }); Pump(25);
                    sound.FadeOut(delegate { third++; });
                    Require(Wait(delegate { return first + second + third >= 3; }, 600), "Reentrant fade lost a callback.");
                    Pump(50);
                    Require(first == 1 && second == 1 && third == 1 && elapsed >= 80 && elapsed < 300 && !sound.IsPlaying && Player(sound) == null,
                        "Reentrant FadeOut restarted the timer, repeated callbacks, or retained media.");
                    result.measurements["completionMilliseconds"] = elapsed;
                }
            });
            Run("dispose-completes-pending-and-later-fade-callbacks", delegate(CheckResult result)
            {
                var sound = new StartupSound(projectRoot, SilentSettings(), result.Record);
                try
                {
                    StartSilent(sound, result, 2.0);
                    int pending = 0, later = 0;
                    sound.FadeOut(delegate { pending++; }); Pump(25);
                    sound.Dispose(); sound.Dispose(); sound.FadeOut(delegate { later++; }); Pump(150);
                    Require(pending == 1 && later == 1 && !sound.IsPlaying && Player(sound) == null,
                        "Dispose lost or repeated a callback or retained its player.");
                }
                finally { sound.Dispose(); }
            });
            Run("callback-failure-does-not-block-other-completions", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 2.0); int remaining = 0;
                    sound.FadeOut(delegate { throw new InvalidOperationException("Intentional completion failure."); });
                    sound.FadeOut(delegate { remaining++; }); sound.FadeOut(delegate { remaining++; });
                    Require(Wait(delegate { return remaining == 2; }, 600), "An exception in one callback suppressed other completions.");
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_completion_failed") == 1,
                        "A failing completion was not isolated or retained its decoder.");
                }
            });
            Run("runtime-mute-resumes-at-current-video-position", delegate(CheckResult result)
            {
                Settings settings = SilentSettings();
                using (var sound = new StartupSound(projectRoot, settings, result.Record))
                {
                    StartSilent(sound, result, 1.0);
                    settings.SoundEnabled = false; sound.TrackVideo(1.1, 8);
                    Require(!sound.IsPlaying && Player(sound).IsMuted, "Turning sound off did not mute and pause playback.");
                    settings.SoundEnabled = true; sound.TrackVideo(2.1, 8);
                    MediaPlayer player = Player(sound); player.IsMuted = true;
                    Require(sound.IsPlaying && player.Position.TotalSeconds >= 2.0 && Count(result, "sound_resumed") == 1,
                        "Unmuting restarted the cue rather than following the video.");
                }
            });
            Run("tail-audio-passes-video-end-and-finishes-once", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 7.75);
                    var handoffClock = Stopwatch.StartNew();
                    sound.ContinueAfterVideo(7.75, 8);
                    // The video is frozen and then released at the handoff. The
                    // cue still has 0.55 seconds left and must pass video end.
                    sound.TrackVideo(7.75, 8); Pump(330);
                    Require(sound.IsPlaying && Player(sound) != null && Count(result, "sound_finished") == 0,
                        "The cue was cut off at the frozen video position or 8-second video duration.");
                    result.measurements["afterVideoEndPlayerPositionSeconds"] = Player(sound).Position.TotalSeconds;
                    Require(Player(sound).Position.TotalSeconds >= 7.99 && Player(sound).Position.TotalSeconds < CueSeconds,
                        "Tail playback did not advance across the 8-second video boundary.");
                    Require(Wait(delegate { return Count(result, "sound_finished") == 1; }, 1400),
                        "The tail cue did not finish naturally at its own duration.");
                    result.measurements["handoffToNaturalEndMilliseconds"] = handoffClock.Elapsed.TotalMilliseconds;
                    Require(handoffClock.ElapsedMilliseconds >= 400 && handoffClock.ElapsedMilliseconds < 1700,
                        "Natural tail completion used the wrong clock or waited beyond the cue.");
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_fade_completed") == 0 &&
                        Count(result, "sound_video_released") == 1 && Count(result, "sound_started") == 1,
                        "The handoff restarted/faded audio or did not release its decoder at natural end.");
                    RequireTimersStopped(sound);
                    int eventsAtEnd = result.events.Count;
                    sound.TrackVideo(0.2, 8); sound.ContinueAfterVideo(7.75, 8); Pump(100);
                    Require(result.events.Count == eventsAtEnd && Count(result, "sound_open_requested") == 1,
                        "The waiting tail resurrected an already completed sound.");
                }
            });
            Run("tail-handoff-is-idempotent-and-ignores-backwards-video", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 7.45);
                    sound.ContinueAfterVideo(7.45, 8); Pump(180);
                    double before = Player(sound).Position.TotalSeconds;
                    sound.ContinueAfterVideo(7.45, 8);
                    sound.TrackVideo(0.10, 8); sound.TrackVideo(7.45, 8); Pump(130);
                    Require(sound.IsPlaying && Player(sound) != null && Player(sound).Position.TotalSeconds >= before + 0.07 &&
                        Count(result, "sound_video_released") == 1 && Count(result, "sound_stopped") == 0 &&
                        Count(result, "sound_started") == 1 && Count(result, "sound_resumed") == 0,
                        "A repeated handoff or stale video timestamp reset/stopped the independent audio clock.");
                    result.measurements["positionBeforeRepeatedHandoff"] = before;
                    result.measurements["positionAfterRepeatedHandoff"] = Player(sound).Position.TotalSeconds;
                    Require(Wait(delegate { return Count(result, "sound_finished") == 1; }, 1500),
                        "Repeated handoff extended the cue instead of preserving its original end.");
                    RequireTimersStopped(sound);
                }
            });
            Run("late-open-during-tail-seeks-to-current-clock", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    // Deliberately hold the dispatcher so the asynchronous
                    // decoder notification arrives after the video handoff.
                    sound.ContinueAfterVideo(7.75, 8);
                    Thread.Sleep(100);
                    Require(Wait(delegate { return Count(result, "sound_started") == 1 || Count(result, "sound_finished") > 0 || Count(result, "sound_failed") > 0; }, 1200),
                        "Late tail opening did not reach a playback or terminal state.");
                    MediaPlayer player = Player(sound);
                    if (player != null) player.IsMuted = true;
                    Require(sound.IsPlaying && player != null && player.Position.TotalSeconds >= 7.83 && player.Position.TotalSeconds < CueSeconds &&
                        Count(result, "sound_started") == 1 && Count(result, "sound_video_released") == 1,
                        "Late audio replayed from the start or used the frozen 7.75-second video timestamp.");
                    result.measurements["lateOpenPositionSeconds"] = player.Position.TotalSeconds;
                }
            });
            Run("late-open-after-tail-end-never-starts-or-retains-timers", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    sound.ContinueAfterVideo(7.75, 8);
                    Thread.Sleep(650);
                    Require(Wait(delegate { return Count(result, "sound_finished") == 1 || Count(result, "sound_failed") > 0; }, 1500),
                        "Audio which opened after the tail cue elapsed never reached its terminal state.");
                    Require(!sound.IsPlaying && Player(sound) == null && Count(result, "sound_started") == 0 &&
                        Count(result, "sound_finished") == 1 && Count(result, "sound_failed") == 0,
                        "A very late decoder notification replayed expired audio or retained its decoder.");
                    RequireTimersStopped(sound);
                    int terminalEvents = result.events.Count;
                    sound.TrackVideo(0.1, 8); sound.ContinueAfterVideo(0.1, 8); Pump(60);
                    Require(result.events.Count == terminalEvents, "An expired tail cue was reopened.");
                }
            });
            Run("tail-runtime-mute-resumes-at-independent-time", delegate(CheckResult result)
            {
                Settings settings = SilentSettings();
                using (var sound = new StartupSound(projectRoot, settings, result.Record))
                {
                    StartSilent(sound, result, 7.20); sound.ContinueAfterVideo(7.20, 8);
                    settings.SoundEnabled = false; Pump(100);
                    Require(!sound.IsPlaying && Player(sound) != null && Player(sound).IsMuted,
                        "Runtime mute did not pause the tail cue without requiring video callbacks.");
                    settings.SoundEnabled = true; Pump(160);
                    MediaPlayer player = Player(sound);
                    if (player != null) player.IsMuted = true;
                    Require(sound.IsPlaying && player != null && player.Position.TotalSeconds >= 7.40 &&
                        Count(result, "sound_resumed") == 1 && Count(result, "sound_started") == 1,
                        "Tail unmute resumed stale audio instead of seeking the independent timeline.");
                    result.measurements["resumePositionSeconds"] = player.Position.TotalSeconds;
                }
            });
            Run("tail-runtime-zero-volume-resumes-at-independent-time", delegate(CheckResult result)
            {
                Settings settings = SilentSettings();
                using (var sound = new StartupSound(projectRoot, settings, result.Record))
                {
                    StartSilent(sound, result, 7.20); sound.ContinueAfterVideo(7.20, 8);
                    settings.SoundVolume = 0; Pump(100);
                    Require(!sound.IsPlaying && Player(sound) != null && Player(sound).IsMuted,
                        "Zero volume did not pause/mute the tail cue without video callbacks.");
                    settings.SoundVolume = PlaybackVolume; Pump(160);
                    MediaPlayer player = Player(sound);
                    if (player != null) player.IsMuted = true;
                    Require(sound.IsPlaying && player != null && player.Position.TotalSeconds >= 7.40 &&
                        Math.Abs(player.Volume - PlaybackVolume) < 0.000001 && Count(result, "sound_resumed") == 1,
                        "Restoring tail volume restarted old audio or lost the selected playback gain.");
                    result.measurements["resumePositionSeconds"] = player.Position.TotalSeconds;
                }
            });
            Run("disabled-at-handoff-late-enable-seeks-without-replay", delegate(CheckResult result)
            {
                Settings settings = SilentSettings(); settings.SoundEnabled = false;
                using (var sound = new StartupSound(projectRoot, settings, result.Record))
                {
                    sound.ContinueAfterVideo(7.20, 8); Pump(100);
                    Require(Player(sound) == null && Count(result, "sound_open_requested") == 0,
                        "A disabled tail cue opened a decoder.");
                    settings.SoundEnabled = true;
                    Require(Wait(delegate { return Count(result, "sound_started") == 1 || Count(result, "sound_finished") > 0 || Count(result, "sound_failed") > 0; }, 1200),
                        "Enabling sound during the tail did not open or finish the cue.");
                    MediaPlayer player = Player(sound);
                    if (player != null) player.IsMuted = true;
                    Require(sound.IsPlaying && player != null && player.Position.TotalSeconds >= 7.29 &&
                        Count(result, "sound_started") == 1 && Count(result, "sound_open_requested") == 1,
                        "Enabling tail audio replayed the beginning instead of following elapsed tail time.");
                    result.measurements["lateEnablePositionSeconds"] = player.Position.TotalSeconds;
                }
            });
            Run("tail-fade-releases-clock-and-completes-all-callbacks", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 7.75); sound.ContinueAfterVideo(7.75, 8);
                    int first = 0, second = 0; var fadeClock = Stopwatch.StartNew();
                    sound.FadeOut(delegate { first++; }); sound.FadeOut(delegate { second++; });
                    Require(Wait(delegate { return first == 1 && second == 1; }, 600), "Tail fading lost one of its callbacks.");
                    double elapsed = fadeClock.Elapsed.TotalMilliseconds;
                    Require(elapsed >= 80 && elapsed < 300 &&
                        !sound.IsPlaying && Player(sound) == null && Count(result, "sound_fade_completed") == 1 && Count(result, "sound_finished") == 0,
                        "Tail fade waited for all speech, raced natural end, or retained its media owner.");
                    RequireTimersStopped(sound);
                    int eventsAfterFade = result.events.Count;
                    sound.TrackVideo(1, 8); Pump(650);
                    Require(first == 1 && second == 1 && result.events.Count == eventsAfterFade,
                        "A continuation callback ran after the tail fade released media.");
                    result.measurements["fadeCompletionMilliseconds"] = elapsed;
                }
            });
            Run("dispose-during-tail-releases-clock-and-pending-fade", delegate(CheckResult result)
            {
                var sound = new StartupSound(projectRoot, SilentSettings(), result.Record);
                try
                {
                    StartSilent(sound, result, 7.75); sound.ContinueAfterVideo(7.75, 8);
                    int pending = 0, later = 0;
                    sound.FadeOut(delegate { pending++; }); Pump(25);
                    sound.Dispose(); sound.Dispose(); sound.FadeOut(delegate { later++; });
                    Require(!sound.IsPlaying && Player(sound) == null && pending == 1 && later == 1,
                        "Disposing a tail cue retained media or lost/repeated a fade callback.");
                    RequireTimersStopped(sound);
                    int eventsAfterDispose = result.events.Count; Pump(650);
                    Require(result.events.Count == eventsAfterDispose && pending == 1 && later == 1,
                        "A stopped tail timer delivered callbacks after disposal.");
                }
                finally { sound.Dispose(); }
            });
            Run("decoder-failure-during-tail-releases-clock-and-stays-silent", delegate(CheckResult result)
            {
                using (var sound = new StartupSound(projectRoot, SilentSettings(), result.Record))
                {
                    StartSilent(sound, result, 7.75); sound.ContinueAfterVideo(7.75, 8);
                    MethodInfo failure = typeof(StartupSound).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(failure != null, "The decoder-failure seam is unavailable.");
                    failure.Invoke(sound, new object[] { new IOException("Intentional owned decoder failure after video handoff.") });
                    int callbacks = 0; sound.FadeOut(delegate { callbacks++; });
                    Require(!sound.IsPlaying && Player(sound) == null && callbacks == 1 && Count(result, "sound_failed") == 1,
                        "A tail decoder failure kept playing, retained media, or blocked its fade callback.");
                    RequireTimersStopped(sound);
                    int eventsAfterFailure = result.events.Count;
                    sound.TrackVideo(7.75, 8); sound.ContinueAfterVideo(7.75, 8); Pump(650);
                    Require(result.events.Count == eventsAfterFailure && Count(result, "sound_started") == 1 &&
                        Count(result, "sound_open_requested") == 1, "A failed tail cue was reopened by a stale video callback.");
                    result.measurements["failureInjection"] = "Owned component failure seam; no real application/media state modified.";
                }
            });
        }
        catch (Exception error)
        {
            results.Add(new CheckResult { name = "fixture-setup", passed = false, detail = error.ToString() });
            Console.WriteLine("FAIL: fixture-setup; " + error.Message);
        }
        int passedCount = 0;
        foreach (var result in results) if (result.passed) passedCount++;
        bool passed = passedCount == results.Count && results.Count == ExpectedCheckCount;
        var report = new Dictionary<string, object> {
            { "passed", passed }, { "passedCount", passedCount }, { "checkCount", results.Count },
            { "assembly", typeof(StartupSound).Assembly.Location },
            { "activationRequests", 0 }, { "originalAudioPlaybackRequested", false },
            { "silentFixture", silentPath }, { "silentFixtureSamplesVerifiedZero", true },
            { "silentFixtureDurationSeconds", CueSeconds },
            { "configuredPlaybackVolume", PlaybackVolume }, { "expectedCheckCount", ExpectedCheckCount },
            { "tailClockAndTimerLifetimeVerified", passed },
            { "checks", results }
        };
        File.WriteAllText(args[3], new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
        Console.WriteLine((passed ? "PASS" : "FAIL") + ": " + passedCount.ToString(CultureInfo.InvariantCulture) + "/" + results.Count +
            " sound checks; original WAV was only analyzed; playback fixture is entirely silent; activation requests=0");
        return passed ? 0 : 1;
    }
}
