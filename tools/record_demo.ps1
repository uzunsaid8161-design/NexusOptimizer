# Nimmt die laufende App als Frames auf (für das Demo-GIF).
param(
    [string]$Exe = "..\app\NexusOptimizer.exe",
    [int]$Seconds = 18,
    [int]$Fps = 12,
    [string]$OutDir = "..\demo-frames"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Rec {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    mouse_event(0x02, 0, 0, 0, IntPtr.Zero); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
  }
}
'@

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = [IO.Path]::GetFullPath((Join-Path $root $Exe))
$outPath = [IO.Path]::GetFullPath((Join-Path $root $OutDir))
if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }
New-Item -ItemType Directory -Path $outPath | Out-Null

Get-Process NexusOptimizer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

$appDir = Split-Path -Parent $exePath
cmd /c start "" /D "$appDir" "$exePath"
$proc = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 400
    $proc = Get-Process NexusOptimizer -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.MainWindowHandle -ne 0) { break }
}
if (-not $proc) { throw "App-Fenster nicht gefunden" }

$W = 1280; $H = 820
[void][Rec]::SetWindowPos($proc.MainWindowHandle, [IntPtr](-1), 0, 0, $W, $H, 0x0040)
[void][Rec]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Milliseconds 900

$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 88L)

$smallW = 960; $smallH = [int]($H * ($smallW / $W))
$interval = 1000.0 / $Fps
$sw = [Diagnostics.Stopwatch]::StartNew()
$frame = 0
$actions = @{
    4.0  = { [Rec]::Click(1106, 118) }    # "Optimizer öffnen"
    9.5  = { [Rec]::Click(48, 80) }       # Dashboard-Nav
    12.5 = { [Rec]::SetCursorPos(40, 400) } # Sidebar aufklappen
    15.5 = { [Rec]::SetCursorPos(700, 700) }
}
$done = @{}

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $elapsed = $sw.Elapsed.TotalSeconds
    foreach ($key in @($actions.Keys)) {
        if (-not $done.ContainsKey($key) -and $elapsed -ge [double]$key) {
            $done[$key] = $true
            & $actions[$key]
        }
    }

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $small = New-Object System.Drawing.Bitmap($smallW, $smallH)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.DrawImage($bmp, 0, 0, $smallW, $smallH)
    $file = Join-Path $outPath ("frame_{0:0000}.jpg" -f $frame)
    $small.Save($file, $jpeg, $encParams)
    $g2.Dispose(); $small.Dispose(); $g.Dispose(); $bmp.Dispose()
    $frame++

    $target = $frame * $interval
    $sleep = $target - $sw.Elapsed.TotalMilliseconds
    if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]$sleep) }
}

"frames=$frame dir=$outPath"
