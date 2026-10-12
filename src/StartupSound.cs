using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DragonLauncher
{
    /// <summary>A one-shot cue synchronized to video, then to its own tail clock.</summary>
    public sealed class StartupSound : IDisposable
    {
        private const double FadeSeconds = 0.10;
        private readonly string root;
        private readonly Settings settings;
        private readonly Action<string, string> record;
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer fadeTimer;
        private readonly DispatcherTimer continuationTimer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Stopwatch fadeClock = new Stopwatch();
        private readonly Stopwatch continuationClock = new Stopwatch();
        private readonly List<Action> completions = new List<Action>();
        private MediaPlayer player;
        private bool opened, hasVideo, stopped, disposed, fading, everPlayed;
        private bool videoReleased;
        private volatile bool playing;
        private int disposeRequested, corrections;
        private double position, soundDuration, lastProgress, lastCorrection = -10, fadeVolume;
        private double continuationPosition;

        public bool IsPlaying { get { return playing && Interlocked.CompareExchange(ref disposeRequested, 0, 0) == 0; } }
        public bool IsFinished { get { return stopped || Interlocked.CompareExchange(ref disposeRequested, 0, 0) != 0; } }

        public StartupSound(string projectRoot, Settings config, Action<string, string> recorder)
        {
            root = projectRoot;
            settings = config;
            record = recorder;
            dispatcher = Dispatcher.CurrentDispatcher;
            fadeTimer = new DispatcherTimer(DispatcherPriority.Send, dispatcher);
            fadeTimer.Interval = TimeSpan.FromMilliseconds(15);
            fadeTimer.Tick += OnFadeTick;
            continuationTimer = new DispatcherTimer(DispatcherPriority.Send, dispatcher);
            continuationTimer.Interval = TimeSpan.FromMilliseconds(33);
            continuationTimer.Tick += OnContinuationTick;
            dispatcher.ShutdownStarted += OnDispatcherShutdown;
            if (config == null || !config.SoundEnabled || DesiredVolume() <= 0)
                Log("sound_disabled", "startup sound disabled or volume is zero");
            else
                EnsureOpen();
        }

        /// <summary>Call while the video is playing; these are video seconds, not wall time.</summary>
        public void TrackVideo(double videoPosition, double duration)
        {
            if (Interlocked.CompareExchange(ref disposeRequested, 0, 0) != 0) return;
            RunOnOwner(delegate { TrackVideoCore(videoPosition, duration); }, null);
        }

        private void TrackVideoCore(double videoPosition, double duration)
        {
            if (disposed || stopped || fading || videoReleased || !IsFinite(videoPosition) || !IsFinite(duration) || duration <= 0) return;
            videoPosition = Math.Max(0, Math.Min(duration, videoPosition));
            if (hasVideo && videoPosition < position - 0.10)
            {
                StopPermanently("sound_stopped", "video moved backwards; startup audio does not repeat");
                return;
            }
            if (!hasVideo || videoPosition > position + 0.001) lastProgress = clock.Elapsed.TotalSeconds;
            hasVideo = true;
            position = videoPosition;
            UpdatePlayback();
        }

        /// <summary>Release video synchronization once; late audio uses the advancing tail timestamp.</summary>
        public void ContinueAfterVideo(double videoPosition, double duration)
        {
            if (Interlocked.CompareExchange(ref disposeRequested, 0, 0) != 0) return;
            RunOnOwner(delegate
            {
                if (disposed || stopped || fading || videoReleased || !IsFinite(videoPosition) || !IsFinite(duration) || duration <= 0) return;
                videoPosition = Math.Max(0, Math.Min(duration, videoPosition));
                if (hasVideo && videoPosition < position - 0.10)
                {
                    StopPermanently("sound_stopped", "video moved backwards before audio handoff; startup audio does not repeat");
                    return;
                }
                hasVideo = true;
                videoReleased = true;
                position = continuationPosition = videoPosition;
                continuationClock.Restart();
                continuationTimer.Start();
                Log("sound_video_released", "video position=" + Number(position) + "s; audio continues on its own clock");
                UpdatePlayback();
            }, null);
        }

        private void OnContinuationTick(object sender, EventArgs args)
        {
            if (disposed || stopped || fading || !videoReleased) return;
            position = continuationPosition + continuationClock.Elapsed.TotalSeconds;
            // Even failed/asynchronous opens and disabled cues have a bounded
            // lifetime. Valid audio files are limited to 30 seconds on open.
            if (position >= (opened ? soundDuration : 30.0))
            {
                StopPermanently("sound_finished", "independent audio clock reached the one-shot cue end; decoder released");
                return;
            }
            UpdatePlayback();
        }

        private void UpdatePlayback()
        {
            if (settings == null || !settings.SoundEnabled || DesiredVolume() <= 0)
            {
                if (player != null && playing)
                {
                    try { player.IsMuted = true; player.Pause(); }
                    catch (Exception error) { Fail(error); return; }
                    playing = false;
                    Log("sound_paused", "sound switch or zero volume");
                }
                return;
            }
            EnsureOpen();
            Synchronize();
        }

        private void EnsureOpen()
        {
            if (disposed || stopped || fading || player != null) return;
            try
            {
                string configured = settings == null ? null : settings.SoundFile;
                if (string.IsNullOrWhiteSpace(configured))
                {
                    StopPermanently("sound_failed", "audio path is empty; continuing silently");
                    return;
                }
                string path = Path.GetFullPath(Path.Combine(root, Environment.ExpandEnvironmentVariables(configured)));
                if (!File.Exists(path))
                {
                    StopPermanently("sound_failed", "audio file is missing; continuing silently: " + path);
                    return;
                }
                player = new MediaPlayer { Volume = 0, IsMuted = true };
                player.MediaOpened += OnMediaOpened;
                player.MediaEnded += OnMediaEnded;
                player.MediaFailed += OnMediaFailed;
                player.Open(new Uri(path, UriKind.Absolute));
                Log("sound_open_requested", "asynchronous audio load; no startup wait");
            }
            catch (Exception error) { Fail(error); }
        }

        private void OnMediaOpened(object sender, EventArgs args)
        {
            if (disposed || stopped || fading || player == null) return;
            try
            {
                if (!player.NaturalDuration.HasTimeSpan)
                    throw new InvalidDataException("Startup sound duration is unavailable.");
                soundDuration = player.NaturalDuration.TimeSpan.TotalSeconds;
                if (!IsFinite(soundDuration) || soundDuration <= 0 || soundDuration > 30)
                    throw new InvalidDataException("Startup sound duration must be at most 30 seconds.");
                opened = true;
                Log("sound_opened", "duration=" + Number(soundDuration) + "s; asynchronous load complete");
                Synchronize();
            }
            catch (Exception error) { Fail(error); }
        }

        private void Synchronize()
        {
            if (disposed || stopped || fading || !opened || !hasVideo || player == null) return;
            if (settings == null || !settings.SoundEnabled || DesiredVolume() <= 0) return;
            try
            {
                if (videoReleased) position = continuationPosition + continuationClock.Elapsed.TotalSeconds;
                if (position >= soundDuration)
                {
                    StopPermanently("sound_finished", "one-shot cue reached its own duration; decoder released");
                    return;
                }
                // Avoid starting from a stale timestamp if the video has stalled
                // or an asynchronous decode event arrives during a UI pause.
                if (!videoReleased && (position < 0.025 || clock.Elapsed.TotalSeconds - lastProgress > 0.22))
                {
                    if (playing)
                    {
                        player.Pause();
                        playing = false;
                        Log("sound_paused", "waiting for video progress");
                    }
                    return;
                }
                player.Volume = DesiredVolume();
                player.IsMuted = false;
                if (!playing)
                {
                    // Always seek before Play. Late opening must not replay the
                    // beginning while the material or eyes are already visible.
                    player.Position = TimeSpan.FromSeconds(position);
                    player.Play();
                    playing = true;
                    lastCorrection = clock.Elapsed.TotalSeconds;
                    Log(everPlayed ? "sound_resumed" : "sound_started", "video position=" + Number(position) + "s; volume=" + Number(player.Volume) + (videoReleased ? "; independent audio clock" : ""));
                    everPlayed = true;
                    return;
                }
                // Once the video is gone the decoder runs forward freely.
                // Its reported Position can reset at EOS before MediaEnded is
                // dispatched; seeking then would restart the tail or cut it.
                if (videoReleased) return;
                double drift = player.Position.TotalSeconds - position;
                double elapsed = clock.Elapsed.TotalSeconds;
                if (IsFinite(drift) && Math.Abs(drift) > 0.35 && elapsed - lastCorrection >= 0.80 && corrections < 3)
                {
                    player.Position = TimeSpan.FromSeconds(position);
                    lastCorrection = elapsed;
                    corrections++;
                    Log("sound_resynchronized", "drift=" + Number(drift) + "s; video position=" + Number(position) + "s");
                }
            }
            catch (Exception error) { Fail(error); }
        }

        /// <summary>Fade and release the decoder; callbacks also complete on failure or disposal.</summary>
        public void FadeOut(Action completed = null)
        {
            if (Interlocked.CompareExchange(ref disposeRequested, 0, 0) != 0)
            {
                InvokeCompletion(completed);
                return;
            }
            RunOnOwner(delegate { FadeOutCore(completed); }, completed);
        }

        private void FadeOutCore(Action completed)
        {
            if (completed != null) completions.Add(completed);
            if (disposed || stopped || player == null || !playing)
            {
                StopPermanently("sound_fade_completed", "sound already silent");
                return;
            }
            if (fading) return;
            try
            {
                fading = true;
                continuationTimer.Stop();
                continuationClock.Stop();
                fadeVolume = player.Volume;
                fadeClock.Restart();
                fadeTimer.Start();
            }
            catch (Exception error) { Fail(error); }
        }

        private void OnFadeTick(object sender, EventArgs args)
        {
            if (disposed || stopped || player == null)
            {
                StopPermanently("sound_fade_completed", "sound already silent");
                return;
            }
            try
            {
                double progress = Math.Min(1, fadeClock.Elapsed.TotalSeconds / FadeSeconds);
                player.Volume = Math.Max(0, fadeVolume * (1 - progress));
                if (progress >= 1)
                    StopPermanently("sound_fade_completed", "100ms fade; decoder released");
            }
            catch (Exception error) { Fail(error); }
        }

        private void OnMediaEnded(object sender, EventArgs args)
        {
            if (!disposed && !stopped) StopPermanently("sound_finished", "one-shot cue ended; decoder released");
        }

        private void OnMediaFailed(object sender, ExceptionEventArgs args)
        {
            if (!disposed && !stopped) Fail(args.ErrorException);
        }

        private void Fail(Exception error)
        {
            string details = error == null ? "audio decoder failed" : error.GetType().Name + ": " + error.Message;
            StopPermanently("sound_failed", details + "; continuing silently");
        }

        private void StopPermanently(string eventName, string details)
        {
            bool first = !stopped;
            stopped = true;
            fading = false;
            playing = false;
            fadeTimer.Stop();
            fadeClock.Stop();
            continuationTimer.Stop();
            continuationClock.Stop();
            ClosePlayer();
            if (first && eventName != null) Log(eventName, details);
            CompleteFade();
        }

        private void ClosePlayer()
        {
            var closing = player;
            player = null;
            opened = false;
            if (closing == null) return;
            closing.MediaOpened -= OnMediaOpened;
            closing.MediaEnded -= OnMediaEnded;
            closing.MediaFailed -= OnMediaFailed;
            try { closing.IsMuted = true; closing.Stop(); }
            catch { }
            try { closing.Close(); }
            catch { }
        }

        private double DesiredVolume()
        {
            double value = settings == null ? 0 : settings.SoundVolume;
            return IsFinite(value) ? Math.Max(0, Math.Min(1, value)) : 0;
        }

        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Number(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }

        private void Log(string name, string details)
        {
            try { if (record != null) record(name, details); }
            catch { }
        }

        private void InvokeCompletion(Action completed)
        {
            try { if (completed != null) completed(); }
            catch (Exception error) { Log("sound_completion_failed", error.GetType().Name + ": " + error.Message); }
        }

        private void CompleteFade()
        {
            Action[] callbacks = completions.ToArray();
            completions.Clear();
            foreach (Action callback in callbacks) InvokeCompletion(callback);
        }

        private void RunOnOwner(Action action, Action unavailable)
        {
            Action guarded = delegate
            {
                try { action(); }
                catch (Exception error) { Fail(error); InvokeCompletion(unavailable); }
            };
            if (dispatcher.CheckAccess()) { guarded(); return; }
            try
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    InvokeCompletion(unavailable);
                    return;
                }
                // Scalar video timestamps can cross threads; WPF objects stay on
                // the constructor's dispatcher for their entire lifetime.
                DispatcherOperation operation = dispatcher.BeginInvoke(DispatcherPriority.Send, guarded);
                if (unavailable != null)
                    operation.Aborted += delegate { InvokeCompletion(unavailable); };
            }
            catch (InvalidOperationException) { InvokeCompletion(unavailable); }
        }

        private void OnDispatcherShutdown(object sender, EventArgs args) { DisposeCore(); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeRequested, 1) != 0) return;
            RunOnOwner(DisposeCore, null);
        }

        private void DisposeCore()
        {
            if (disposed) return;
            Interlocked.Exchange(ref disposeRequested, 1);
            disposed = true;
            StopPermanently(null, null);
            fadeTimer.Tick -= OnFadeTick;
            continuationTimer.Tick -= OnContinuationTick;
            dispatcher.ShutdownStarted -= OnDispatcherShutdown;
            clock.Stop();
        }
    }
}
