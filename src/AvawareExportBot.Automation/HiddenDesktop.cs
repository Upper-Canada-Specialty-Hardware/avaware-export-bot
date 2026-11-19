using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AvawareExportBot.Automation;

/// <summary>
/// A desktop in the user's window station that is never shown. Processes started on it draw there, so nothing appears
/// on the user's screen and nothing takes focus. The thread that drives windows on it must be attached with
/// <see cref="AttachCurrentThread"/>, which only works on a thread that has created no windows.
/// </summary>
public sealed class HiddenDesktop : IDisposable
{
    public HiddenDesktop(string name)
    {
        Name = name;
        Handle = Native.CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, Native.DESKTOP_ALL_ACCESS | Native.GENERIC_ALL, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateDesktop({name}) failed");
    }

    public string Name { get; }
    public IntPtr Handle { get; }

    /// <summary>The name CreateProcess takes in STARTUPINFO.lpDesktop.</summary>
    public string StartupName => @"WinSta0\" + Name;

    public void AttachCurrentThread()
    {
        if (!Native.SetThreadDesktop(Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetThreadDesktop({Name}) failed");
    }

    /// <summary>
    /// Starts a process on this desktop with a fresh copy of the user's environment (not this process's) and no
    /// inherited handles. AVAproject crashed on opening a file when started with an inherited environment.
    /// </summary>
    public Process Launch(string exePath, string arguments, string workingDirectory)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        IntPtr env = IntPtr.Zero;
        try
        {
            if (!Native.CreateEnvironmentBlock(out env, token, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed");
            var si = new Native.STARTUPINFO { lpDesktop = StartupName };
            si.cb = Marshal.SizeOf(si);
            var cmd = new StringBuilder($"\"{exePath}\" {arguments}");
            if (!Native.CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NEW_PROCESS_GROUP, env, workingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess({exePath}) failed");
            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                // Open the Process object's own handle now, while the process is alive (ours is still open, so its
                // id cannot be reused): without it, ExitCode cannot be read after the process exits.
                _ = proc.Handle;
                return proc;
            }
            finally
            {
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            Native.CloseHandle(token);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) Native.CloseDesktop(Handle);
    }
}
