using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkspaceColorIcons;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--install", StringComparison.OrdinalIgnoreCase)))
        {
            Installer.Install();
            return;
        }

        if (args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Installer.Uninstall();
            return;
        }

        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var instanceName = $@"Local\WorkspaceColorIcons-{identity}";
        var stopEventName = $"{instanceName}-stop";

        using var mutex = new Mutex(true, instanceName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        using var stopEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            stopEventName);

        ApplicationConfiguration.Initialize();
        using var context = new WatcherApplicationContext(stopEvent);
        Application.Run(context);
    }
}

internal static class Installer
{
    private const string ShortcutName = "Workspace Color Icons.lnk";

    public static void Install()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the executable path.");
        var startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutName);
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Workspace Color Icons");
        var startMenuPath = Path.Combine(startMenuDirectory, ShortcutName);

        Directory.CreateDirectory(startMenuDirectory);
        CreateShortcut(startupPath, executablePath);
        CreateShortcut(startMenuPath, executablePath);
        StopRunningInstance();

        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
        });

        MessageBox.Show(
            "Workspace Color Icons is running and was added to Startup and the Start menu.",
            "Workspace Color Icons",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public static void Uninstall()
    {
        StopRunningInstance();

        var startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutName);
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Workspace Color Icons");
        var startMenuPath = Path.Combine(startMenuDirectory, ShortcutName);

        File.Delete(startupPath);
        File.Delete(startMenuPath);
        if (Directory.Exists(startMenuDirectory)
            && !Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
        {
            Directory.Delete(startMenuDirectory);
        }

        MessageBox.Show(
            "Workspace Color Icons was removed from Startup and the Start menu.",
            "Workspace Color Icons",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void StopRunningInstance()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var stopEventName = $@"Local\WorkspaceColorIcons-{identity}-stop";
        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            stopEvent.Set();
            Thread.Sleep(300);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private static void CreateShortcut(string shortcutPath, string executablePath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows Script Host.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath);
            shortcut.Description = "Color VS Code icons by workspace name";
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

internal sealed class WatcherApplicationContext : ApplicationContext
{
    private readonly WorkspaceWatcher watcher = new();
    private readonly EventWaitHandle stopEvent;
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem toggleMenuItem;
    private readonly System.Windows.Forms.Timer scanTimer;
    private readonly System.Windows.Forms.Timer resumeTimer;
    private readonly PowerMessageWindow powerWindow;
    private readonly WindowEventListener windowEvents;
    private readonly RegisteredWaitHandle stopRegistration;
    private readonly Icon trayIcon;
    private bool watcherEnabled = true;
    private bool powerSuspended;

    public WatcherApplicationContext(EventWaitHandle stopEvent)
    {
        this.stopEvent = stopEvent;
        trayIcon = LoadTrayIcon();

        toggleMenuItem = new ToolStripMenuItem("Stop coloring");
        toggleMenuItem.Click += (_, _) => ToggleWatcher();
        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggleMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = trayIcon,
            Text = "Workspace Color Icons - running",
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ToggleWatcher();

        scanTimer = new System.Windows.Forms.Timer { Interval = 150 };
        scanTimer.Tick += (_, _) =>
        {
            scanTimer.Stop();
            if (watcherEnabled && !powerSuspended)
            {
                watcher.Scan();
            }
        };

        resumeTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        resumeTimer.Tick += (_, _) =>
        {
            resumeTimer.Stop();
            powerSuspended = false;
            if (watcherEnabled)
            {
                notifyIcon.Text = "Workspace Color Icons - running";
                watcher.Scan();
            }
        };

        powerWindow = new PowerMessageWindow();
        powerWindow.Suspending += OnSuspending;
        powerWindow.Resumed += OnResumed;
        powerWindow.SessionEnding += ExitThread;
        powerWindow.ScanRequested += ScheduleScan;
        powerWindow.ExitRequested += ExitThread;
        windowEvents = new WindowEventListener(powerWindow.RequestScan);
        stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            this.stopEvent,
            static (state, _) => ((PowerMessageWindow)state!).RequestExit(),
            powerWindow,
            Timeout.Infinite,
            true);

        watcher.Scan();
    }

    private void ToggleWatcher()
    {
        watcherEnabled = !watcherEnabled;
        if (watcherEnabled)
        {
            toggleMenuItem.Text = "Stop coloring";
            notifyIcon.Text = powerSuspended
                ? "Workspace Color Icons - suspended"
                : "Workspace Color Icons - running";
            if (!powerSuspended)
            {
                watcher.Scan();
            }
        }
        else
        {
            watcher.RestoreAll();
            toggleMenuItem.Text = "Start coloring";
            notifyIcon.Text = "Workspace Color Icons - stopped";
        }
    }

    private void OnSuspending()
    {
        powerSuspended = true;
        resumeTimer.Stop();
        watcher.RestoreAll(retry: false);
        notifyIcon.Text = "Workspace Color Icons - suspended";
    }

    private void OnResumed()
    {
        resumeTimer.Stop();
        resumeTimer.Start();
    }

    private void ScheduleScan()
    {
        if (!watcherEnabled || powerSuspended)
        {
            return;
        }

        scanTimer.Stop();
        scanTimer.Start();
    }

    protected override void ExitThreadCore()
    {
        scanTimer.Stop();
        resumeTimer.Stop();
        watcher.RestoreAll();
        notifyIcon.Visible = false;
        stopRegistration.Unregister(null);
        windowEvents.Dispose();
        powerWindow.Dispose();
        scanTimer.Dispose();
        resumeTimer.Dispose();
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        trayIcon.Dispose();
        watcher.Dispose();
        base.ExitThreadCore();
    }

    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream(
            "WorkspaceColorIcons.AppIcon.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}

internal sealed class PowerMessageWindow : NativeWindow, IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int WmAppScan = 0x8001;
    private const int WmAppExit = 0x8002;

    public event Action? Suspending;
    public event Action? Resumed;
    public event Action? SessionEnding;
    public event Action? ScanRequested;
    public event Action? ExitRequested;

    public PowerMessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Workspace Color Icons Power Window",
            Parent = NativeMethods.HwndMessage
        });
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmPowerBroadcast)
        {
            switch (message.WParam.ToInt32())
            {
                case PbtApmSuspend:
                    Suspending?.Invoke();
                    break;
                case PbtApmResumeSuspend:
                case PbtApmResumeAutomatic:
                    Resumed?.Invoke();
                    break;
            }
        }
        else if (message.Msg == WmQueryEndSession)
        {
            SessionEnding?.Invoke();
        }
        else if (message.Msg == WmAppScan)
        {
            ScanRequested?.Invoke();
        }
        else if (message.Msg == WmAppExit)
        {
            ExitRequested?.Invoke();
        }

        base.WndProc(ref message);
    }

    public void RequestScan()
    {
        NativeMethods.PostMessage(Handle, WmAppScan, 0, 0);
    }

    public void RequestExit()
    {
        NativeMethods.PostMessage(Handle, WmAppExit, 0, 0);
    }

    public void Dispose()
    {
        DestroyHandle();
    }
}

internal sealed class WindowEventListener : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjectIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly NativeMethods.WinEventDelegate callback;
    private readonly Action changed;
    private readonly nint[] hooks;

    public WindowEventListener(Action changed)
    {
        this.changed = changed;
        callback = OnWinEvent;
        hooks =
        [
            AddHook(EventSystemForeground, EventSystemForeground),
            AddHook(EventObjectDestroy, EventObjectShow),
            AddHook(EventObjectNameChange, EventObjectNameChange)
        ];
    }

    public void Dispose()
    {
        foreach (var hook in hooks)
        {
            if (hook != 0)
            {
                NativeMethods.UnhookWinEvent(hook);
            }
        }
    }

    private nint AddHook(uint minimumEvent, uint maximumEvent)
    {
        var hook = NativeMethods.SetWinEventHook(
            minimumEvent,
            maximumEvent,
            0,
            callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (hook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register a window event hook.");
        }

        return hook;
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (window == 0)
        {
            return;
        }

        if (eventType == EventSystemForeground || (objectId == ObjectIdWindow && childId == 0))
        {
            changed();
        }
    }
}

internal sealed class WorkspaceWatcher : IDisposable
{
    private static readonly string[] WorkspacePalette =
    [
        "#E45B69",
        "#E7833C",
        "#D1A72E",
        "#69A84F",
        "#258EA6",
        "#347DBB",
        "#9A58B5",
        "#D65C9E"
    ];

    private static readonly Regex ApplicationSuffix = new(
        @"\s+-\s+(Visual Studio Code(?:\s+-\s+Insiders)?|Code\s+-\s+Insiders)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, int> workspaceStyles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> claimedStyles = [];
    private readonly Dictionary<string, IconPair> iconCache = [];
    private readonly Dictionary<nint, TrackedWindow> trackedWindows = [];

    public void Scan()
    {
        var liveHandles = new HashSet<nint>();
        foreach (var window in NativeMethods.GetVisibleWindows())
        {
            var wasTracked = trackedWindows.TryGetValue(window.Handle, out var tracked);
            if (wasTracked)
            {
                liveHandles.Add(window.Handle);
            }

            string? processPath;
            string processName;
            try
            {
                using var process = Process.GetProcessById((int)window.ProcessId);
                processName = process.ProcessName;
                var isVsCode = processName is "Code" or "Code - Insiders";
                var isEdgeDevTools = processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                    && IsEdgeDevToolsWindow(window.Title);
                if (!isVsCode && !isEdgeDevTools)
                {
                    if (wasTracked)
                    {
                        RestoreWindow(window.Handle);
                    }
                    continue;
                }

                processPath = process.MainModule?.FileName;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or InvalidOperationException
                or Win32Exception
                or NotSupportedException)
            {
                continue;
            }

            liveHandles.Add(window.Handle);
            if (string.IsNullOrWhiteSpace(processPath))
            {
                if (wasTracked)
                {
                    RestoreWindow(window.Handle);
                }
                continue;
            }

            var isVsCodeWindow = processName is "Code" or "Code - Insiders";
            var workspaceName = isVsCodeWindow ? GetWorkspaceName(window.Title) : null;
            if (isVsCodeWindow && workspaceName is null)
            {
                if (wasTracked)
                {
                    RestoreWindow(window.Handle);
                }
                continue;
            }

            var dpi = NativeMethods.GetDpiForWindow(window.Handle);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var smallSize = NativeMethods.GetSystemMetricsForDpi(49, dpi);
            var bigSize = NativeMethods.GetSystemMetricsForDpi(11, dpi);
            smallSize = smallSize > 0 ? smallSize : 16;
            bigSize = bigSize > 0 ? bigSize : 32;

            var iconKey = isVsCodeWindow
                ? $"{processPath}|workspace|{workspaceName}|{smallSize}|{bigSize}"
                : $"{processPath}|edge-devtools|{smallSize}|{bigSize}";
            if (wasTracked && tracked!.IconKey == iconKey)
            {
                continue;
            }

            if (!iconCache.TryGetValue(iconKey, out var icons))
            {
                icons = isVsCodeWindow
                    ? new IconPair(
                        CreateWorkspaceIcon(workspaceName!, smallSize, processPath),
                        CreateWorkspaceIcon(workspaceName!, bigSize, processPath))
                    : new IconPair(
                        CreateDebugBadgeIcon(smallSize, processPath),
                        CreateDebugBadgeIcon(bigSize, processPath));
                iconCache.Add(iconKey, icons);
            }

            if (tracked is null)
            {
                var smallSet = NativeMethods.TrySetIcon(
                    window.Handle,
                    NativeMethods.IconSmall,
                    icons.Small,
                    out var originalSmall);
                nint originalBig = 0;
                var bigSet = smallSet && NativeMethods.TrySetIcon(
                    window.Handle,
                    NativeMethods.IconBig,
                    icons.Big,
                    out originalBig);
                if (!bigSet)
                {
                    if (smallSet)
                    {
                        NativeMethods.TrySetIcon(
                            window.Handle,
                            NativeMethods.IconSmall,
                            originalSmall,
                            out _);
                    }
                    if (bigSet)
                    {
                        NativeMethods.TrySetIcon(
                            window.Handle,
                            NativeMethods.IconBig,
                            originalBig,
                            out _);
                    }
                    continue;
                }

                trackedWindows.Add(
                    window.Handle,
                    new TrackedWindow(
                        iconKey,
                        originalSmall,
                        originalBig));
            }
            else
            {
                NativeMethods.TrySetIcon(
                    window.Handle,
                    NativeMethods.IconSmall,
                    icons.Small,
                    out _);
                NativeMethods.TrySetIcon(
                    window.Handle,
                    NativeMethods.IconBig,
                    icons.Big,
                    out _);
                tracked!.IconKey = iconKey;
            }
        }

        foreach (var handle in trackedWindows.Keys.Where(handle => !liveHandles.Contains(handle)).ToArray())
        {
            trackedWindows.Remove(handle);
        }
    }

    public void RestoreAll(bool retry = true)
    {
        var attempts = retry ? 3 : 1;
        for (var attempt = 0; attempt < attempts && trackedWindows.Count > 0; attempt++)
        {
            foreach (var handle in trackedWindows.Keys.ToArray())
            {
                RestoreWindow(handle);
            }

            if (trackedWindows.Count > 0 && attempt + 1 < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        RestoreAll();
        foreach (var icons in iconCache.Values)
        {
            NativeMethods.DestroyIcon(icons.Small);
            NativeMethods.DestroyIcon(icons.Big);
        }
    }

    private static string? GetWorkspaceName(string windowTitle)
    {
        var name = ApplicationSuffix.Replace(windowTitle, "");
        if (name == windowTitle)
        {
            return null;
        }

        var parts = name.Split(" - ", StringSplitOptions.None);
        var workspaceName = parts[^1].Trim();
        return workspaceName.Length == 0 ? null : workspaceName;
    }

    private static bool IsEdgeDevToolsWindow(string windowTitle)
    {
        return windowTitle.Equals("DevTools", StringComparison.OrdinalIgnoreCase)
            || windowTitle.StartsWith("DevTools - ", StringComparison.OrdinalIgnoreCase);
    }

    private nint CreateWorkspaceIcon(string workspaceName, int size, string executablePath)
    {
        using var bitmap = CreateExecutableIconBitmap(size, executablePath);
        var colors = GetWorkspaceColors(workspaceName);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0)
                {
                    continue;
                }

                var color = pixel.GetBrightness() >= 0.38f
                    ? colors.Secondary
                    : colors.Primary;
                bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, color));
            }
        }

        return bitmap.GetHicon();
    }

    private static nint CreateDebugBadgeIcon(int size, string executablePath)
    {
        using var bitmap = CreateExecutableIconBitmap(size, executablePath);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var glyphSize = Math.Max(10f, size * 0.64f);
        var cornerOffset = size * 0.05f;
        var glyphBounds = new RectangleF(
            size - glyphSize + cornerOffset,
            size - glyphSize + cornerOffset,
            glyphSize,
            glyphSize);
        var scale = glyphSize / 20f;
        var originX = glyphBounds.Left;
        var originY = glyphBounds.Top;
        PointF Point(float x, float y) => new(originX + x * scale, originY + y * scale);

        using var wrenchPath = new GraphicsPath();
        wrenchPath.StartFigure();
        wrenchPath.AddBezier(
            Point(13.5f, 2f),
            Point(11.35f, 2f),
            Point(9.45f, 3.52f),
            Point(9.08f, 5.62f));
        wrenchPath.AddBezier(
            Point(9.08f, 5.62f),
            Point(8.98f, 6.2f),
            Point(8.98f, 6.78f),
            Point(9.08f, 7.36f));
        wrenchPath.AddLine(Point(2.66f, 14.02f), Point(2.66f, 14.02f));
        wrenchPath.AddBezier(
            Point(2.66f, 14.02f),
            Point(1.76f, 14.95f),
            Point(1.78f, 16.43f),
            Point(2.7f, 17.33f));
        wrenchPath.AddBezier(
            Point(2.7f, 17.33f),
            Point(3.63f, 18.24f),
            Point(5.11f, 18.23f),
            Point(6.03f, 17.32f));
        wrenchPath.AddLine(Point(6.03f, 17.32f), Point(12.4f, 10.86f));
        wrenchPath.AddBezier(
            Point(12.4f, 10.86f),
            Point(14.48f, 11.38f),
            Point(16.72f, 10.38f),
            Point(17.62f, 8.47f));
        wrenchPath.AddBezier(
            Point(17.62f, 8.47f),
            Point(18.06f, 7.54f),
            Point(18.16f, 6.48f),
            Point(17.89f, 5.49f));
        wrenchPath.AddBezier(
            Point(17.89f, 5.49f),
            Point(17.78f, 5.08f),
            Point(17.28f, 4.94f),
            Point(17.05f, 5.25f));
        wrenchPath.AddLine(Point(17.05f, 5.25f), Point(14.5f, 7.79f));
        wrenchPath.AddLine(Point(14.5f, 7.79f), Point(12.2f, 5.5f));
        wrenchPath.AddLine(Point(12.2f, 5.5f), Point(14.75f, 2.95f));
        wrenchPath.AddBezier(
            Point(14.75f, 2.95f),
            Point(15.06f, 2.64f),
            Point(14.92f, 2.14f),
            Point(14.51f, 2.11f));
        wrenchPath.AddBezier(
            Point(14.51f, 2.11f),
            Point(14.18f, 2.04f),
            Point(13.84f, 2f),
            Point(13.5f, 2f));
        wrenchPath.CloseFigure();

        using var wrenchBrush = new LinearGradientBrush(
            Point(8.5f, 3f),
            Point(11.36f, 18.58f),
            Color.FromArgb(245, 248, 250),
            Color.FromArgb(112, 124, 136));
        graphics.FillPath(wrenchBrush, wrenchPath);
        return bitmap.GetHicon();
    }

    private static Bitmap CreateExecutableIconBitmap(int size, string executablePath)
    {
        var packedSize = (uint)(size | (size << 16));
        var result = NativeMethods.SHDefExtractIcon(
            executablePath,
            0,
            0,
            out var largeIcon,
            out var smallIcon,
            packedSize);
        if (result != 0 || largeIcon == 0)
        {
            throw new Win32Exception(
                result,
                $"Could not load a {size}-pixel application icon from '{executablePath}'.");
        }

        try
        {
            using var applicationIcon = Icon.FromHandle(largeIcon);
            using var sourceBitmap = applicationIcon.ToBitmap();
            var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(sourceBitmap, 0, 0);
            return bitmap;
        }
        finally
        {
            NativeMethods.DestroyIcon(largeIcon);
            if (smallIcon != 0)
            {
                NativeMethods.DestroyIcon(smallIcon);
            }
        }
    }

    private WorkspaceColors GetWorkspaceColors(string workspaceName)
    {
        if (!workspaceStyles.TryGetValue(workspaceName, out var style))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceName.ToLowerInvariant()));
            var styleCount = WorkspacePalette.Length * 8;
            style = (int)(BitConverter.ToUInt32(hash, 0) % styleCount);
            var startStyle = style;
            while (claimedStyles.TryGetValue(style, out var claimedWorkspace)
                   && !claimedWorkspace.Equals(workspaceName, StringComparison.OrdinalIgnoreCase))
            {
                style = (style + 1) % styleCount;
                if (style == startStyle)
                {
                    break;
                }
            }

            workspaceStyles.Add(workspaceName, style);
            claimedStyles[style] = workspaceName;
        }

        var primaryIndex = style / 8;
        var variant = style % 8;
        var secondaryDirection = variant < 4 ? -1 : 1;
        double[] blendAmounts = [0.35, 0.55, 0.75, 1.0];
        var blendAmount = blendAmounts[variant % 4];
        var secondaryIndex =
            (primaryIndex + secondaryDirection + WorkspacePalette.Length) % WorkspacePalette.Length;
        var primary = ColorTranslator.FromHtml(WorkspacePalette[primaryIndex]);
        var neighbor = ColorTranslator.FromHtml(WorkspacePalette[secondaryIndex]);
        var secondary = Color.FromArgb(
            (int)Math.Round(primary.R * (1 - blendAmount) + neighbor.R * blendAmount),
            (int)Math.Round(primary.G * (1 - blendAmount) + neighbor.G * blendAmount),
            (int)Math.Round(primary.B * (1 - blendAmount) + neighbor.B * blendAmount));
        return new WorkspaceColors(primary, secondary);
    }

    private bool RestoreWindow(nint handle)
    {
        if (!trackedWindows.TryGetValue(handle, out var tracked))
        {
            return true;
        }

        if (!NativeMethods.IsWindow(handle))
        {
            trackedWindows.Remove(handle);
            return true;
        }

        var smallRestored = NativeMethods.TrySetIcon(
            handle,
            NativeMethods.IconSmall,
            tracked.OriginalSmall,
            out _);
        var bigRestored = NativeMethods.TrySetIcon(
            handle,
            NativeMethods.IconBig,
            tracked.OriginalBig,
            out _);
        if (smallRestored && bigRestored)
        {
            trackedWindows.Remove(handle);
            return true;
        }

        return false;
    }

    private sealed record IconPair(nint Small, nint Big);

    private sealed class TrackedWindow(
        string iconKey,
        nint originalSmall,
        nint originalBig)
    {
        public string IconKey { get; set; } = iconKey;
        public nint OriginalSmall { get; } = originalSmall;
        public nint OriginalBig { get; } = originalBig;
    }

    private sealed record WorkspaceColors(Color Primary, Color Secondary);
}

internal static class NativeMethods
{
    internal static readonly nint HwndMessage = new(-3);
    internal static readonly nint IconSmall = 0;
    internal static readonly nint IconBig = 1;

    private const uint WmSetIcon = 0x0080;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    internal sealed record WindowInfo(nint Handle, uint ProcessId, string Title);

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    internal delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint eventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHDefExtractIcon(
        string iconFile,
        int iconIndex,
        uint flags,
        out nint largeIcon,
        out nint smallIcon,
        uint iconSize);

    internal static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows(
            (window, _) =>
            {
                var length = GetWindowTextLength(window);
                if (!IsWindowVisible(window) || length == 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);
                GetWindowThreadProcessId(window, out var processId);
                windows.Add(new WindowInfo(window, processId, title.ToString()));
                return true;
            },
            0);
        return windows;
    }

    internal static bool TrySetIcon(nint window, nint iconType, nint icon, out nint previousIcon)
    {
        return SendMessageTimeout(
                   window,
                   WmSetIcon,
                   iconType,
                   icon,
                   SmtoBlock | SmtoAbortIfHung,
                   250,
                   out previousIcon)
               != 0;
    }
}
