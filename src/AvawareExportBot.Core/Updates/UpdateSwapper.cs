namespace AvawareExportBot.Core.Updates;

/// <summary>A process the swapper started.</summary>
public interface IStartedProcess
{
    bool HasExited { get; }
    void Kill();
}

public enum SwapResult
{
    /// <summary>The new exe is in place and reported ready.</summary>
    Started,
    /// <summary>The swap failed; the old exe is back in place and was started again.</summary>
    RolledBack,
}

/// <summary>
/// Swaps the installed exe for a staged one and makes sure the new one comes up. It runs in the old process after
/// that process has shut its app down (no exports, store closed, mutex released), so the new exe can start cleanly.
/// <list type="number">
/// <item>Copy the staged exe beside the installed one as <c>.new</c>.</item>
/// <item>Rename the installed exe to <c>.old</c> (Windows allows renaming a running exe), then <c>.new</c> into its place.</item>
/// <item>Start it with the restart arguments and wait up to a minute for it to report ready.</item>
/// <item>If it doesn't: kill it, put the old exe back, record the version as failed, and start the old exe again.</item>
/// </list>
/// </summary>
public sealed class UpdateSwapper
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string, IReadOnlyList<string>, IStartedProcess> _start;
    private readonly Func<IStartedProcess, TimeSpan, bool> _waitReady;
    private readonly ILog _log;

    /// <param name="start">Starts an exe with arguments.</param>
    /// <param name="waitReady">Waits until the process reports ready (true), or exits or runs out of time (false).</param>
    public UpdateSwapper(Func<string, IReadOnlyList<string>, IStartedProcess> start, Func<IStartedProcess, TimeSpan, bool> waitReady, ILog log)
    {
        _start = start;
        _waitReady = waitReady;
        _log = log;
    }

    public static string NewPath(string exe) => exe + ".new";
    public static string OldPath(string exe) => exe + ".old";

    /// <summary>Whether files can be created beside the exe, which the swap needs. Probed with a throwaway file.</summary>
    public static bool CanWriteBeside(string exe)
    {
        var probe = exe + ".probe";
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a file is a Windows program: an MZ header whose e_lfanew points at a "PE\0\0" signature. Starting
    /// anything else makes Windows show a blocking "Unsupported 16-Bit Application" box, so it is never swapped in.
    /// </summary>
    public static bool IsWindowsExe(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[64];
            if (file.Read(header, 0, header.Length) < header.Length || header[0] != 'M' || header[1] != 'Z') return false;
            var peOffset = BitConverter.ToInt32(header, 0x3C);
            if (peOffset < 64 || peOffset > file.Length - 4) return false;
            file.Position = peOffset;
            var signature = new byte[4];
            return file.Read(signature, 0, 4) == 4 && signature[0] == 'P' && signature[1] == 'E' && signature[2] == 0 && signature[3] == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes a leftover <c>.old</c> beside the exe. False while it is still in use (its process hasn't exited).</summary>
    public static bool TryDeleteOld(string exe)
    {
        var old = OldPath(exe);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <param name="exe">The installed exe, which is the one running this code.</param>
    /// <param name="staged">The verified exe to put in its place.</param>
    /// <param name="current">The running version, passed to the new exe as <c>--updated-from</c>.</param>
    /// <param name="restart">The state and visibility to come back in.</param>
    /// <param name="recordFailed">Records a version that must not be offered again.</param>
    public SwapResult Apply(string exe, StagedUpdate staged, Version current, RestartArgs restart, Action<Version> recordFailed)
    {
        var version = AppVersion.Format(staged.Version);
        var newPath = NewPath(exe);
        var oldPath = OldPath(exe);
        _log.Info($"Updating {AppVersion.Format(current)} -> {version}.");

        try
        {
            if (!IsWindowsExe(staged.Path)) throw new IOException($"{Path.GetFileName(staged.Path)} is not a Windows program.");
            File.Copy(staged.Path, newPath, overwrite: true);
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(exe, oldPath);
            try
            {
                File.Move(newPath, exe);
            }
            catch
            {
                File.Move(oldPath, exe);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing was swapped; the old exe is still in place. Recording the version stops a restart loop.
            _log.Error($"Update to {version} could not replace the exe: {ex.Message}");
            TryDelete(newPath);
            recordFailed(staged.Version);
            Relaunch(exe, restart with { UpdatedFrom = null, UpdateFailed = staged.Version });
            return SwapResult.RolledBack;
        }

        IStartedProcess? process = null;
        try
        {
            process = _start(exe, (restart with { UpdatedFrom = current, UpdateFailed = null }).ToArgs());
            if (_waitReady(process, ReadyTimeout))
            {
                _log.Info($"Update to {version} started.");
                return SwapResult.Started;
            }
            _log.Error(process.HasExited
                ? $"Update to {version} exited before it was ready; rolling back."
                : $"Update to {version} did not report ready within {ReadyTimeout.TotalSeconds:0} s; rolling back.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Update to {version} could not be started ({ex.Message}); rolling back.");
        }

        try
        {
            if (process is { HasExited: false }) process.Kill();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("Could not stop the new exe: " + ex.Message);
        }

        try
        {
            TryDelete(newPath);
            File.Move(exe, newPath);
            File.Move(oldPath, exe);
            TryDelete(newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Rollback could not put the old exe back: {ex.Message}. Reinstall with the line in the README.");
        }
        recordFailed(staged.Version);
        Relaunch(exe, restart with { UpdatedFrom = null, UpdateFailed = staged.Version });
        return SwapResult.RolledBack;
    }

    private void Relaunch(string exe, RestartArgs args)
    {
        try
        {
            _start(exe, args.ToArgs());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Could not start the app again: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
