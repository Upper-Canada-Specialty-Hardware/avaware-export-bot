using AvawareExportBot.Core;

namespace AvawareExportBot.App;

internal static class Program
{
    private const string MutexName = @"Local\AvawareExportBot";
    private const string ShowEventName = @"Local\AvawareExportBot.Show";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // A second launch: ask the running app to show its window, then exit.
            try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();
        using var host = new AppHost(AppPaths.Default);
        using var form = new MainForm(host);

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed) return;
                try { form.BeginInvoke(form.ShowFromTray); } catch (InvalidOperationException) { return; }
            }
        }) { IsBackground = true, Name = "show-listener" };
        listener.Start();

        Application.Run(form);
    }
}
