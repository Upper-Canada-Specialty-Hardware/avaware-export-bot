using AvawareExportBot.Core;

namespace AvawareExportBot.Automation;

/// <summary>
/// The user's real keyboard/mouse input, from GetLastInputInfo. Messages the bot posts to windows on hidden desktops
/// are not input and do not reset it.
/// </summary>
public sealed class WindowsIdleSource : IIdleSource
{
    public TimeSpan IdleTime
    {
        get
        {
            var tick = LastInputTick();
            // GetLastInputInfo's tick is 32-bit and wraps every 49.7 days; compare against the low 32 bits of now.
            var now = (uint)Native.GetTickCount64();
            return TimeSpan.FromMilliseconds(unchecked(now - tick));
        }
    }

    public long LastInputMarker => LastInputTick();

    private static uint LastInputTick()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        return Native.GetLastInputInfo(ref info) ? info.dwTime : 0;
    }
}
