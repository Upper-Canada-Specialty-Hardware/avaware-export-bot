using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace AvawareExportBot.Automation;

/// <summary>A top-level or child window as seen at one moment.</summary>
public readonly record struct Win(IntPtr Handle, string Class, string Title, bool Visible, int Width)
{
    public override string ToString() => $"{Class} | {Title}";
}

/// <summary>Window helpers. Callers enumerating a hidden desktop must run on a thread attached to it.</summary>
internal static class Windows
{
    public static string Text(IntPtr h)
    {
        // WM_GETTEXT with a timeout so a hung AVAproject cannot hang the worker.
        var sb = new StringBuilder(1024);
        if (Native.SendMessageTimeout(h, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb, Native.SMTO_ABORTIFHUNG, 2000, out _) == IntPtr.Zero)
        {
            sb.Clear();
            Native.GetWindowText(h, sb, sb.Capacity);
        }
        return sb.ToString();
    }

    public static string Class(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static Win Describe(IntPtr h)
    {
        Native.GetWindowRect(h, out var r);
        return new Win(h, Class(h), Text(h), Native.IsWindowVisible(h), r.Right - r.Left);
    }

    /// <summary>Top-level windows of a process on a desktop.</summary>
    public static List<Win> TopLevel(IntPtr desktop, int pid, bool visibleOnly = true)
    {
        var handles = new List<IntPtr>();
        Native.EnumDesktopWindows(desktop, (h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var p);
            if (p == pid && (!visibleOnly || Native.IsWindowVisible(h))) handles.Add(h);
            return true;
        }, IntPtr.Zero);
        return handles.Select(Describe).ToList();
    }

    /// <summary>True when the process has any visible top-level window on the desktop (used on the user's desktop).</summary>
    public static bool HasVisibleWindow(IntPtr desktop, int pid)
    {
        var found = false;
        Native.EnumDesktopWindows(desktop, (h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var p);
            if (p == pid && Native.IsWindowVisible(h)) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>All descendants of a window, depth first.</summary>
    public static List<Win> Children(IntPtr parent)
    {
        var handles = new List<IntPtr>();
        Native.EnumChildWindows(parent, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        return handles.Select(Describe).ToList();
    }

    public static IntPtr ChildById(IntPtr parent, int id, string cls)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(parent, (h, _) =>
        {
            if (Native.GetDlgCtrlID(h) == id && Class(h) == cls && Native.IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Cross-process sends never block a worker: 5 s, and abort at once if AVAproject is hung.</summary>
    public const uint SendTimeoutMs = 5000;

    /// <summary>Sends a message with the send timeout. False when AVAproject did not answer in time.</summary>
    public static bool Send(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        Native.SendMessageTimeoutPtr(h, msg, wParam, lParam, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out result) != IntPtr.Zero;

    /// <summary>WM_SETTEXT with the send timeout. False when AVAproject did not answer in time.</summary>
    public static bool SetText(IntPtr h, string text) =>
        Native.SendMessageTimeoutText(h, Native.WM_SETTEXT, IntPtr.Zero, text, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out _) != IntPtr.Zero;

    public static void PostClick(IntPtr h, int x, int y)
    {
        var lp = Native.MakeLParam(x, y);
        Native.PostMessage(h, Native.WM_MOUSEMOVE, IntPtr.Zero, lp);
        Native.PostMessage(h, Native.WM_LBUTTONDOWN, (IntPtr)Native.MK_LBUTTON, lp);
        Native.PostMessage(h, Native.WM_LBUTTONUP, IntPtr.Zero, lp);
    }

    public static void PostKey(IntPtr h, int vk)
    {
        Native.PostMessage(h, Native.WM_KEYDOWN, (IntPtr)vk, (IntPtr)1);
        Native.PostMessage(h, Native.WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001));
    }

    /// <summary>
    /// <see cref="Snapshot"/> on its own thread, attached to the window's desktop, given at most
    /// <paramref name="timeout"/>: PrintWindow waits on the target window, and a hung AVAproject must not hang the worker.
    /// A snapshot still stuck after the timeout is abandoned on its background thread.
    /// </summary>
    public static bool SnapshotWithTimeout(IntPtr desktop, IntPtr h, string path, TimeSpan timeout)
    {
        var ok = false;
        var thread = new Thread(() =>
        {
            if (Native.SetThreadDesktop(desktop)) ok = Snapshot(h, path);
        }) { IsBackground = true, Name = "snapshot" };
        thread.Start();
        return thread.Join(timeout) && ok;
    }

    /// <summary>Renders a window to a PNG with PrintWindow (works on hidden desktops). Returns false on failure.</summary>
    public static bool Snapshot(IntPtr h, string path)
    {
        try
        {
            if (!Native.GetWindowRect(h, out var r)) return false;
            int w = Math.Max(1, r.Right - r.Left), ht = Math.Max(1, r.Bottom - r.Top);
            using var bmp = new Bitmap(w, ht);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { Native.PrintWindow(h, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bmp.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or IOException)
        {
            return false;
        }
    }
}
