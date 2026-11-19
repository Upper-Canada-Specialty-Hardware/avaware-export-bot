using System.Diagnostics;
using System.Reflection;
using AvawareExportBot.Core.Updates;

namespace AvawareExportBot.App;

/// <summary>The running exe: its version, whether the release workflow built it, and where it is.</summary>
internal static class AppInfo
{
    public const string UpdateReadyEventName = @"Local\AvawareExportBot.UpdateReady";

    public static Version Version { get; } = AppVersion.ParseInformational(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Only the release workflow builds with -p:AutoUpdateBuild=true; any other build is a dev build that never updates.</summary>
    public static bool IsReleaseBuild { get; } = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "AutoUpdateBuild" && a.Value == "true");

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AvawareExportBot.exe");
}

/// <summary>Starts an exe for the swapper and waits for it to say it is up.</summary>
internal sealed class StartedProcess : IStartedProcess
{
    private readonly Process _process;

    private StartedProcess(Process process) => _process = process;

    public static IStartedProcess Start(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) info.ArgumentList.Add(a);
        return new StartedProcess(Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned nothing."));
    }

    public bool HasExited => _process.HasExited;

    public void Kill() => _process.Kill(entireProcessTree: true);

    /// <summary>True once the new app sets the ready event; false if it exits first or the time runs out.</summary>
    public static bool WaitReady(EventWaitHandle ready, IStartedProcess process, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (ready.WaitOne(250)) return true;
            if (process.HasExited) return ready.WaitOne(0);
        }
        return false;
    }
}
