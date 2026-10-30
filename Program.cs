// LockMirror: Windows only shows the lock screen on the primary monitor and leaves the
// others black. On lock, this program saves where each window is and switches the
// displays to "Duplicate"; on unlock it switches back to "Extend" and puts the windows
// back where they were.
//
// Usage:
//   LockMirror.exe               keeps running in the background, with no window
//   LockMirror.exe --install     starts it with Windows (HKCU\...\Run)
//   LockMirror.exe --uninstall   removes it from startup
//
// Log: %LOCALAPPDATA%\LockMirror\log.txt

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LockMirror;

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "LockMirror";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
            return HandleCommand(args[0]);

        using var mutex = new Mutex(true, @"Local\LockMirror", out bool isFirstInstance);
        if (!isFirstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        using var watcher = new SessionWatcher();
        Application.Run();
        return 0;
    }

    static int HandleCommand(string command)
    {
        // It's a GUI app: attach to the calling console so the messages are visible.
        Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        switch (command)
        {
            case "--install":
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
                Console.WriteLine("LockMirror will start with Windows.");
                return 0;
            case "--uninstall":
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                Console.WriteLine("LockMirror removed from startup.");
                return 0;
            default:
                Console.WriteLine("Usage: LockMirror.exe [--install | --uninstall]");
                return 1;
        }
    }
}

/// <summary>Hidden window that receives the session lock and unlock notifications.</summary>
sealed class SessionWatcher : NativeWindow, IDisposable
{
    // Passes to put the windows back after extending: Windows also moves them on its
    // own for a few seconds, so it's done once more.
    static readonly int[] RestoreDelaysMs = [1000, 2000];

    const int MaxCloneAttempts = 5;

    readonly System.Windows.Forms.Timer restoreTimer = new();
    readonly System.Windows.Forms.Timer cloneTimer = new() { Interval = 500 };
    List<SavedWindow> savedWindows = [];
    bool clonedByUs;
    int cloneAttempt;
    int restorePass;

    public SessionWatcher()
    {
        CreateHandle(new CreateParams());

        // Right after sign-in the session service may not be ready yet:
        // retry up to 12 times, every 5 seconds.
        bool registered = false;
        for (int attempt = 1; attempt <= 12 && !registered; attempt++)
        {
            registered = Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION);
            if (!registered && attempt < 12)
                Thread.Sleep(5000);
        }
        Log.Write(registered ? "Started." : $"Could not register for session notifications (error {Marshal.GetLastWin32Error()}).");

        restoreTimer.Tick += (_, _) => RestorePass();
        cloneTimer.Tick += (_, _) => TryClone();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_WTSSESSION_CHANGE)
        {
            if (m.WParam == Native.WTS_SESSION_LOCK)
                OnLock();
            else if (m.WParam == Native.WTS_SESSION_UNLOCK)
                OnUnlock();
        }
        base.WndProc(ref m);
    }

    void OnLock()
    {
        restoreTimer.Stop();

        // With a single active monitor there's nothing to duplicate (or it already is).
        if (Native.GetSystemMetrics(Native.SM_CMONITORS) < 2)
            return;

        savedWindows = SavedWindow.CaptureAll();
        cloneAttempt = 0;
        TryClone();
    }

    // The lock notification arrives before Windows allows changing the displays: the
    // first attempt usually fails with "access denied" (5) and the next one, half a
    // second later, works.
    void TryClone()
    {
        cloneTimer.Stop();
        cloneAttempt++;
        int result = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_APPLY | Native.SDC_TOPOLOGY_CLONE);
        clonedByUs = result == 0;
        Log.Write($"Lock: {savedWindows.Count} windows saved; duplicate (attempt {cloneAttempt}) -> {result}.");
        if (!clonedByUs && cloneAttempt < MaxCloneAttempts)
            cloneTimer.Start();
    }

    void OnUnlock()
    {
        cloneTimer.Stop();
        if (!clonedByUs)
            return;
        clonedByUs = false;

        int result = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_APPLY | Native.SDC_TOPOLOGY_EXTEND);
        Log.Write($"Unlock: extend -> {result}.");
        if (result != 0)
            return;

        restorePass = 0;
        restoreTimer.Interval = RestoreDelaysMs[0];
        restoreTimer.Start();
    }

    void RestorePass()
    {
        restoreTimer.Stop();
        int moved = 0;
        // EnumWindows returns them top to bottom; walk them in reverse so the last one
        // placed is the one that was on top.
        for (int i = savedWindows.Count - 1; i >= 0; i--)
        {
            if (savedWindows[i].Restore())
                moved++;
        }
        Log.Write($"Pass {restorePass + 1}: {moved} windows moved back.");

        restorePass++;
        if (restorePass < RestoreDelaysMs.Length)
        {
            restoreTimer.Interval = RestoreDelaysMs[restorePass];
            restoreTimer.Start();
        }
    }

    public void Dispose()
    {
        restoreTimer.Dispose();
        cloneTimer.Dispose();
        if (Handle != IntPtr.Zero)
        {
            Native.WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }
    }
}

/// <summary>Position of a window at the time of locking.</summary>
sealed record SavedWindow(IntPtr Hwnd, Native.WINDOWPLACEMENT Placement, Native.RECT Rect)
{
    public static List<SavedWindow> CaptureAll()
    {
        var windows = new List<SavedWindow>();
        uint ownPid = (uint)Environment.ProcessId;
        var title = new StringBuilder(2);

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindowText(hwnd, title, title.Capacity) == 0)
                return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid)
                return true;
            // Hidden windows of Store apps and of other virtual desktops.
            Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (cloaked != 0)
                return true;

            var placement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(hwnd, ref placement) && Native.GetWindowRect(hwnd, out var rect))
                windows.Add(new SavedWindow(hwnd, placement, rect));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>Puts the window back where it was. Returns whether it had to be moved.</summary>
    public bool Restore()
    {
        if (!Native.IsWindow(Hwnd))
            return false;

        var current = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(Hwnd, ref current) || !Native.GetWindowRect(Hwnd, out var currentRect))
            return false;
        if (current.showCmd == Placement.showCmd && current.normalPosition.Equals(Placement.normalPosition) && currentRect.Equals(Rect))
            return false;

        var target = Placement;
        target.length = Marshal.SizeOf<Native.WINDOWPLACEMENT>();
        // Asynchronous: if the app is hung, it doesn't hang us.
        target.flags |= Native.WPF_ASYNCWINDOWPLACEMENT;

        switch (Placement.showCmd)
        {
            case Native.SW_SHOWMAXIMIZED:
                // First place it unmaximized so it lands on the right monitor, then
                // maximize it there.
                var normal = target;
                normal.showCmd = Native.SW_SHOWNOACTIVATE;
                Native.SetWindowPlacement(Hwnd, ref normal);
                break;
            case Native.SW_SHOWMINIMIZED:
                target.showCmd = Native.SW_SHOWMINNOACTIVE;
                break;
            default:
                target.showCmd = Native.SW_SHOWNOACTIVATE;
                break;
        }
        Native.SetWindowPlacement(Hwnd, ref target);
        return true;
    }
}

static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockMirror", "log.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                File.Delete(Path);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static class Native
{
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int NOTIFY_FOR_THIS_SESSION = 0;

    public const uint SDC_TOPOLOGY_CLONE = 0x2;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4;
    public const uint SDC_APPLY = 0x80;

    public const int SM_CMONITORS = 80;
    public const int DWMWA_CLOAKED = 14;
    public const int ATTACH_PARENT_PROCESS = -1;

    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const int WPF_ASYNCWINDOWPLACEMENT = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public record struct POINT(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    public record struct RECT(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT minPosition;
        public POINT maxPosition;
        public RECT normalPosition;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
