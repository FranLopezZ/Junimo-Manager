# Debug helper: launches the Debug build, waits, and saves a screenshot of its window.
# Usage: powershell -File scripts/dev/shot.ps1 -Out shot.png [-Tab 1] [-Lang en] [-Pad] [-Config <settings folder>] [-Keys Down,Down,Return] [-Show "config:Mod name"]
param([string]$Out, [int]$Tab = 0, [string]$Lang = "", [switch]$Pad, [int]$Wait = 12, [string]$Config = "", [string[]]$Keys = @(), [string]$Show = "", [string]$Exe = "")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; } }
"@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:JUNIMO_START_TAB = "$Tab"
if ($Lang) { $env:JUNIMO_LANG = $Lang }
if ($Pad) { $env:SteamGameId = "1" }
if ($Config) { $env:JUNIMO_CONFIG_DIR = $Config }
if ($Show) { $env:JUNIMO_DEBUG_SHOW = $Show }
$exePath = if ($Exe) { $Exe } else { Join-Path $root "src\SdvMods.App\bin\Debug\net10.0\JunimoManager.exe" }
$p = Start-Process -FilePath $exePath -PassThru
Start-Sleep $Wait
$p.Refresh(); $h = $p.MainWindowHandle
# Optional key presses (posted straight to the window, so no interactive desktop is needed)
$vk = @{ Up = 0x26; Down = 0x28; Left = 0x25; Right = 0x27; Return = 0x0D; Escape = 0x1B; PageUp = 0x21; PageDown = 0x22; Tab = 0x09; Space = 0x20 }
foreach ($k in ($Keys -split ",")) { [W]::PostMessage($h, 0x100, [IntPtr]$vk[$k], [IntPtr]0) | Out-Null; Start-Sleep -Milliseconds 150; [W]::PostMessage($h, 0x101, [IntPtr]$vk[$k], [IntPtr]0) | Out-Null; Start-Sleep -Milliseconds 400 }
if (-not $p.HasExited -and $h -ne [IntPtr]::Zero) {
  $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T); $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Out)
} else { "window not available (exited=$($p.HasExited))" }
if (-not $p.HasExited) { Stop-Process $p -Force }
