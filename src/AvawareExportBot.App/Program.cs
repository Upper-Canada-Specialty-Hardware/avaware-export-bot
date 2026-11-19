using AvawareExportBot.Core;
using AvawareExportBot.Core.Updates;

namespace AvawareExportBot.App;

internal static class Program
{
    private const string MutexName = @"Local\AvawareExportBot";
    private const string ShowEventName = @"Local\AvawareExportBot.Show";

    [STAThread]
    private static void Main(string[] argv)
    {
        var start = RestartArgs.Parse(argv);
        var paths = AppPaths.Default;
        paths.EnsureCreated();
        var log = new FileLog(paths.LogsDir);
        PendingUpdate? pending;

        var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        try
        {
            if (!first)
            {
                // A second launch: ask the running app to show its window, then exit.
                try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return;
            }

            CleanUpdateLeftovers();

            // A staged update left by an exit during a hold (or found at the last check) is applied before any work starts.
            pending = StagedAtStartup(paths, log, start);
            if (pending is null) pending = RunApp(paths, log, start);
        }
        finally
        {
            if (first) mutex.ReleaseMutex();
            mutex.Dispose();
        }

        // The app has shut down and released the mutex, so the new exe can take over.
        if (pending is not null) Swap(paths, log, pending);
    }

    /// <summary>Runs the window until the app exits. Returns the update to apply when it exited for one.</summary>
    private static PendingUpdate? RunApp(AppPaths paths, FileLog log, RestartArgs start)
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();
        using var host = new AppHost(paths, log, start);
        using var form = new MainForm(host, start);
        // Create the window handle now, before the listener can call BeginInvoke on it.
        _ = form.Handle;

        var stopping = false;
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (Volatile.Read(ref stopping) || form.IsDisposed) return;
                // A show that cannot be delivered right now is skipped; the listener keeps running for the next launch.
                try { form.BeginInvoke(form.ShowFromTray); } catch (InvalidOperationException) { }
            }
        }) { IsBackground = true, Name = "show-listener" };
        listener.Start();

        // An updated app tells the old one it is up once its message loop runs.
        if (start.UpdatedFrom is not null) form.BeginInvoke(SignalReady);

        Application.Run(form);

        // Stop the listener before the event closes: after an update this process lives on for a while as the
        // swap's supervisor, and the new app's show event is the same named event.
        Volatile.Write(ref stopping, true);
        showEvent.Set();
        listener.Join(TimeSpan.FromSeconds(2));
        return host.PendingUpdate;
    }

    private static PendingUpdate? StagedAtStartup(AppPaths paths, FileLog log, RestartArgs start)
    {
        if (!AppInfo.IsReleaseBuild || !Settings.Load(paths.SettingsFile).AutoUpdate) return null;
        if (!UpdateSwapper.CanWriteBeside(AppInfo.ExePath)) return null;
        try
        {
            using var http = AppHost.MakeHttpClient();
            var failed = start.UpdateFailed ?? UpdateStateFile.LoadFailed(paths.UpdateStateFile);
            var staged = new UpdateDownloader(http, paths.UpdatesDir).FindStaged(AppInfo.Version, failed);
            if (staged is null) return null;
            log.Info($"Staged update {AppVersion.Format(staged.Version)} found at startup; applying it before any work starts.");
            return new PendingUpdate(staged, start with { UpdatedFrom = null, UpdateFailed = null });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("Could not read the staged updates: " + ex.Message);
            return null;
        }
    }

    private static void Swap(AppPaths paths, FileLog log, PendingUpdate pending)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, AppInfo.UpdateReadyEventName);
        ready.Reset();
        var swapper = new UpdateSwapper(StartedProcess.Start, (p, timeout) => StartedProcess.WaitReady(ready, p, timeout), log);
        swapper.Apply(AppInfo.ExePath, pending.Staged, AppInfo.Version, pending.Restart, version =>
        {
            try
            {
                UpdateStateFile.SaveFailed(paths.UpdateStateFile, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("Could not record the failed update: " + ex.Message);
            }
        });
    }

    private static void SignalReady()
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(AppInfo.UpdateReadyEventName);
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The old app is gone already (it gave up waiting); nothing to tell.
        }
    }

    /// <summary>
    /// Deletes what a swap leaves beside the exe. The replaced exe (<c>.old</c>) is still running right after an update,
    /// as the old app's supervisor, so it is retried every 2 s for up to 2 min. Every startup runs this, which also
    /// clears files a power cut left mid-swap.
    /// </summary>
    private static void CleanUpdateLeftovers()
    {
        var exe = AppInfo.ExePath;
        try { File.Delete(UpdateSwapper.NewPath(exe)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!File.Exists(UpdateSwapper.OldPath(exe))) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !UpdateSwapper.TryDeleteOld(exe); i++)
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        });
    }
}
