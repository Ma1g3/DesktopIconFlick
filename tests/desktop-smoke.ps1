$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$file = Join-Path $root 'bin\DesktopIconFlick.exe'
$testFile = Join-Path $root 'bin\DesktopIconFlick.Tests.exe'
if (!(Test-Path -LiteralPath $file -PathType Leaf) -or !(Test-Path -LiteralPath $testFile -PathType Leaf)) {
    throw '请先运行 .\build.ps1 -Test。'
}
if (Get-Process -Name DesktopIconFlick -ErrorAction SilentlyContinue) {
    throw '请先关闭其他桌面甩枪实例。'
}

$assembly = [Reflection.Assembly]::LoadFile($testFile)
$native = $assembly.GetType('DesktopIconFlick.WindowsDesktop', $true)
$probe = $assembly.GetType('DesktopIconFlick.DesktopProbe', $true)
$rect = $native.GetNestedType('Rect', [Reflection.BindingFlags]'Public,NonPublic')
$dpiContext = $probe.GetMethod('SetThreadDpiAwarenessContext')
$previousDpi = $dpiContext.Invoke($null, [object[]]@([IntPtr]::new(-4)))
if ($previousDpi -eq [IntPtr]::Zero) {
    throw '无法将测试线程切换到每显示器 DPI 感知。'
}
$process = $null
try {
    $work = $native.GetMethod('PrimaryWorkArea').Invoke($null, $null)
    $virtual = $native.GetMethod('VirtualScreen').Invoke($null, $null)
    $process = Start-Process -FilePath $file -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "程序提前退出（$($process.ExitCode)）。" }
    } until ($process.MainWindowHandle -ne 0 -or $clock.Elapsed.TotalSeconds -ge 10)
    if ($process.MainWindowHandle -eq 0 -or !$process.Responding) {
        throw '程序未创建可响应的主窗口。'
    }
    $window = $process.MainWindowHandle
    $query = [object[]]@($window, [Activator]::CreateInstance($rect))
    if (!$native.GetMethod('GetWindowRect').Invoke($null, $query) -or !$query[1].Equals($work)) {
        throw '窗口边界与主屏工作区不一致。'
    }
    $clip = [object[]]@([Activator]::CreateInstance($rect))
    $getClip = $native.GetMethod('GetClipCursor', [Reflection.BindingFlags]'Static,NonPublic')
    if (!$getClip.Invoke($null, $clip) -or !$clip[0].Equals($virtual)) {
        throw '启动后未开始训练时，光标应能遍历整个桌面。'
    }
    if (!$probe.GetMethod('IsNonClickThroughLayer').Invoke($null, [object[]]@($window))) {
        throw '训练窗口必须是未设置点击穿透的分层窗口。'
    }
    $inset = [Math]::Min(24, [int]([Math]::Min($work.Height, $work.Width) / 8))
    $points = @(
        @([int]($work.Left + $inset), [int]($work.Top + $inset)),
        @([int]($work.Right - 1 - $inset), [int]($work.Top + $inset)),
        @([int]($work.Left + $inset), [int]($work.Bottom - 1 - $inset)),
        @([int]($work.Right - 1 - $inset), [int]($work.Bottom - 1 - $inset))
    )
    $hit = $probe.GetMethod('HitWindow')
    do {
        $misses = 0
        foreach ($point in $points) {
            if ($hit.Invoke($null, [object[]]$point) -ne $window) { $misses++ }
        }
        if ($misses) { Start-Sleep -Milliseconds 100 }
    } until (!$misses -or $clock.Elapsed.TotalSeconds -ge 10)
    if ($misses) {
        throw "未能命中叠层的 $misses 个空白采样点；请确认没有其他窗口遮挡后重试。"
    }
    $dpi = $probe.GetMethod('GetDpiForWindow').Invoke($null, [object[]]@($window))
    $monitorCount = $native.GetMethod('MonitorCount').Invoke($null, $null)
    $refreshRate = $native.GetMethod('PrimaryRefreshRate').Invoke($null, $null)
    if ($monitorCount -lt 1) { throw '无法读取显示器数量。' }
    $refreshLabel = if ($refreshRate -gt 0) { "${refreshRate} Hz" } else { 'unknown refresh rate' }
    Write-Output "Passed: window $($work.Width)x$($work.Height) at $dpi DPI, desktop $($virtual.Width)x$($virtual.Height) physical pixels, $monitorCount monitor(s), primary $refreshLabel, free cursor, $($points.Count) blank overlay hit-test points."
}
finally {
    if ($process) {
        if (!$process.HasExited -and (!$process.CloseMainWindow() -or !$process.WaitForExit(5000))) {
            Write-Warning '测试启动的窗口未能正常关闭，请手动关闭。'
        }
        $process.Dispose()
    }
    $null = $dpiContext.Invoke($null, [object[]]@($previousDpi))
}
