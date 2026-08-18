[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Install,

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Uninstall,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(250, 60000)]
    [int]$PollIntervalMilliseconds = 1500
)

$ErrorActionPreference = 'Stop'

$shortcutName = 'Workspace Color Icons.lnk'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory $shortcutName
$currentPowerShell = (Get-Process -Id $PID).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$instanceName = "Local\WorkspaceColorIcons-$identity"
$stopEventName = "$instanceName-stop"
$workspacePalette = @(
    '#E45B69' # coral
    '#E7833C' # orange
    '#D1A72E' # gold
    '#69A84F' # green
    '#258EA6' # cyan
    '#347DBB' # blue
    '#9A58B5' # purple
    '#D65C9E' # pink
)
$workspaceStyles = @{}
$claimedStyles = @{}

function Stop-RunningWatcher {
    try {
        $event = [Threading.EventWaitHandle]::OpenExisting($stopEventName)
        $event.Set() | Out-Null
        $event.Dispose()
    }
    catch [Threading.WaitHandleCannotBeOpenedException] {
        # No watcher is currently running.
    }
}

if ($Uninstall) {
    Stop-RunningWatcher
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath
    }
    Write-Host 'Workspace Color Icons was removed from Startup.'
    exit 0
}

if ($Install) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $currentPowerShell
    $shortcut.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $shortcut.WorkingDirectory = Split-Path -Parent $PSCommandPath
    $shortcut.Description = 'Color VS Code icons by workspace name'
    $shortcut.Save()

    Stop-RunningWatcher
    Start-Sleep -Milliseconds 250
    Start-Process -FilePath $currentPowerShell -WindowStyle Hidden -ArgumentList @(
        '-NoProfile',
        '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`""
    )
    Write-Host 'Workspace Color Icons is running and will start automatically when you sign in.'
    exit 0
}

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WorkspaceIconNative
{
    public const uint WM_SETICON = 0x0080;
    public static readonly IntPtr ICON_SMALL = (IntPtr)0;
    public static readonly IntPtr ICON_BIG = (IntPtr)1;
    public static readonly IntPtr ICON_SMALL2 = (IntPtr)2;

    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public uint ProcessId { get; set; }
        public string Title { get; set; }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHDefExtractIcon(
        string iconFile,
        int iconIndex,
        uint flags,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconSize);

    public static WindowInfo[] GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            int length = GetWindowTextLength(window);
            if (!IsWindowVisible(window) || length == 0)
                return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            windows.Add(new WindowInfo {
                Handle = window,
                ProcessId = processId,
                Title = title.ToString()
            });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@

function Get-WorkspaceName {
    param([string]$WindowTitle)

    $name = $WindowTitle -replace '\s+-\s+(Visual Studio Code(?:\s+-\s+Insiders)?|Code\s+-\s+Insiders)$', ''
    if ($name -eq $WindowTitle) {
        return $null
    }

    $parts = $name -split '\s+-\s+'
    $name = $parts[-1].Trim()
    if ([string]::IsNullOrWhiteSpace($name)) {
        return $null
    }
    return $name
}

function Get-WorkspaceColors {
    param([string]$WorkspaceName)

    if ($workspaceStyles.ContainsKey($WorkspaceName)) {
        $style = $workspaceStyles[$WorkspaceName]
    }
    else {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($WorkspaceName.ToLowerInvariant())
            $hash = $sha256.ComputeHash($bytes)
            $styleCount = $workspacePalette.Count * 8
            $style = [BitConverter]::ToUInt32($hash, 0) % $styleCount
            $startStyle = $style
            while ($claimedStyles.ContainsKey($style) -and $claimedStyles[$style] -ne $WorkspaceName) {
                $style = ($style + 1) % $styleCount
                if ($style -eq $startStyle) {
                    break
                }
            }
            $workspaceStyles[$WorkspaceName] = $style
            $claimedStyles[$style] = $WorkspaceName
        }
        finally {
            $sha256.Dispose()
        }
    }

    $primaryIndex = [Math]::Floor($style / 8)
    $variant = $style % 8
    $secondaryDirection = if ($variant -lt 4) { -1 } else { 1 }
    $blendAmounts = @(0.35, 0.55, 0.75, 1.0)
    $blendAmount = $blendAmounts[$variant % 4]
    $secondaryIndex = ($primaryIndex + $secondaryDirection + $workspacePalette.Count) % $workspacePalette.Count
    $primary = [Drawing.ColorTranslator]::FromHtml($workspacePalette[$primaryIndex])
    $neighbor = [Drawing.ColorTranslator]::FromHtml($workspacePalette[$secondaryIndex])

    $secondary = [Drawing.Color]::FromArgb(
        [Math]::Round(($primary.R * (1 - $blendAmount)) + ($neighbor.R * $blendAmount)),
        [Math]::Round(($primary.G * (1 - $blendAmount)) + ($neighbor.G * $blendAmount)),
        [Math]::Round(($primary.B * (1 - $blendAmount)) + ($neighbor.B * $blendAmount))
    )

    return @{
        Primary = $primary
        Secondary = $secondary
    }
}

function New-WorkspaceIcon {
    param(
        [string]$WorkspaceName,
        [int]$Size,
        [string]$ExecutablePath
    )

    $largeIcon = [IntPtr]::Zero
    $smallIcon = [IntPtr]::Zero
    $packedSize = [uint32]($Size -bor ($Size -shl 16))
    $result = [WorkspaceIconNative]::SHDefExtractIcon(
        $ExecutablePath,
        0,
        0,
        [ref]$largeIcon,
        [ref]$smallIcon,
        $packedSize
    )
    if ($result -ne 0 -or $largeIcon -eq [IntPtr]::Zero) {
        throw "Could not load a $Size-pixel application icon from '$ExecutablePath' (HRESULT $result)."
    }

    $applicationIcon = [Drawing.Icon]::FromHandle($largeIcon)
    $bitmap = $applicationIcon.ToBitmap()
    try {
        $colors = Get-WorkspaceColors $WorkspaceName
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.A -ne 0) {
                    $color = if ($pixel.GetBrightness() -ge 0.38) {
                        $colors.Secondary
                    }
                    else {
                        $colors.Primary
                    }
                    $bitmap.SetPixel($x, $y, [Drawing.Color]::FromArgb($pixel.A, $color.R, $color.G, $color.B))
                }
            }
        }
        return $bitmap.GetHicon()
    }
    finally {
        $bitmap.Dispose()
        $applicationIcon.Dispose()
        [WorkspaceIconNative]::DestroyIcon($largeIcon) | Out-Null
        if ($smallIcon -ne [IntPtr]::Zero) {
            [WorkspaceIconNative]::DestroyIcon($smallIcon) | Out-Null
        }
    }
}

$createdNew = $false
$mutex = New-Object Threading.Mutex $true, $instanceName, ([ref]$createdNew)
if (-not $createdNew) {
    $mutex.Dispose()
    exit 0
}

$stopEvent = New-Object Threading.EventWaitHandle $false, ([Threading.EventResetMode]::ManualReset), $stopEventName
$iconCache = @{}
$trackedWindows = @{}

try {
    do {
        $liveHandles = @{}
        foreach ($window in [WorkspaceIconNative]::GetVisibleWindows()) {
            $process = Get-Process -Id $window.ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $process -or $process.ProcessName -notin @('Code', 'Code - Insiders')) {
                continue
            }

            $workspaceName = Get-WorkspaceName $window.Title
            if ($null -eq $workspaceName) {
                continue
            }

            $key = $window.Handle.ToInt64().ToString()
            $dpi = [WorkspaceIconNative]::GetDpiForWindow($window.Handle)
            if ($dpi -eq 0) {
                $dpi = 96
            }
            $smallSize = [WorkspaceIconNative]::GetSystemMetricsForDpi(49, $dpi)
            $bigSize = [WorkspaceIconNative]::GetSystemMetricsForDpi(11, $dpi)
            if ($smallSize -le 0) {
                $smallSize = 16
            }
            if ($bigSize -le 0) {
                $bigSize = 32
            }

            $iconKey = "$($process.Path)|$workspaceName|$smallSize|$bigSize"
            $liveHandles[$key] = $true
            if ($trackedWindows.ContainsKey($key) -and $trackedWindows[$key].IconKey -eq $iconKey) {
                continue
            }

            if (-not $iconCache.ContainsKey($iconKey)) {
                $iconCache[$iconKey] = @{
                    Small = New-WorkspaceIcon -WorkspaceName $workspaceName -Size $smallSize -ExecutablePath $process.Path
                    Big = New-WorkspaceIcon -WorkspaceName $workspaceName -Size $bigSize -ExecutablePath $process.Path
                }
            }

            $icons = $iconCache[$iconKey]
            if (-not $trackedWindows.ContainsKey($key)) {
                $trackedWindows[$key] = @{
                    Handle = $window.Handle
                    OriginalSmall = [WorkspaceIconNative]::SendMessage($window.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_SMALL, $icons.Small)
                    OriginalBig = [WorkspaceIconNative]::SendMessage($window.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_BIG, $icons.Big)
                    IconKey = $iconKey
                }
            }
            else {
                [WorkspaceIconNative]::SendMessage($window.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_SMALL, $icons.Small) | Out-Null
                [WorkspaceIconNative]::SendMessage($window.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_BIG, $icons.Big) | Out-Null
                $trackedWindows[$key].IconKey = $iconKey
            }
            [WorkspaceIconNative]::SendMessage($window.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_SMALL2, $icons.Small) | Out-Null
        }

        foreach ($key in @($trackedWindows.Keys)) {
            if (-not $liveHandles.ContainsKey($key)) {
                $trackedWindows.Remove($key)
            }
        }

    } while (-not $stopEvent.WaitOne($PollIntervalMilliseconds))
}
finally {
    foreach ($entry in $trackedWindows.Values) {
        if ([WorkspaceIconNative]::IsWindow($entry.Handle)) {
            [WorkspaceIconNative]::SendMessage($entry.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_SMALL, $entry.OriginalSmall) | Out-Null
            [WorkspaceIconNative]::SendMessage($entry.Handle, [WorkspaceIconNative]::WM_SETICON, [WorkspaceIconNative]::ICON_BIG, $entry.OriginalBig) | Out-Null
        }
    }
    foreach ($icons in $iconCache.Values) {
        [WorkspaceIconNative]::DestroyIcon($icons.Small) | Out-Null
        [WorkspaceIconNative]::DestroyIcon($icons.Big) | Out-Null
    }
    $stopEvent.Dispose()
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
