#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = "C:\Program Files\ProtectMe"
$serviceExe = Join-Path $installDir "Service\ProtectMe.Service.exe"

Write-Host "Publishing ProtectMe Service and App..." -ForegroundColor Yellow
dotnet publish "$root\ProtectMe.Service\ProtectMe.Service.csproj" -c Release -r win-x64 --self-contained false -o "$installDir\Service"
dotnet publish "$root\ProtectMe.App\ProtectMe.App.csproj" -c Release -r win-x64 --self-contained false -o "$installDir\App"

# Stop existing service if present
$existing = Get-Service -Name "ProtectMeService" -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name "ProtectMeService" -Force -ErrorAction SilentlyContinue
    sc.exe delete "ProtectMeService" | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Registering ProtectMeService as LocalSystem with auto-recovery..." -ForegroundColor Yellow
sc.exe create "ProtectMeService" binPath= "`"$serviceExe`"" start= auto obj= LocalSystem DisplayName= "ProtectMe Protection Service" | Out-Null
sc.exe description "ProtectMeService" "Bypass-proof app and website whitelist protection service." | Out-Null
sc.exe failure "ProtectMeService" reset= 0 actions= restart/1000/restart/1000/restart/1000 | Out-Null
sc.exe failureflag "ProtectMeService" 1 | Out-Null

Start-Service -Name "ProtectMeService"

# Create Desktop shortcut for ProtectMe Tablet UI
$ws = New-Object -ComObject WScript.Shell
$shortcut = $ws.CreateShortcut("$env:PUBLIC\Desktop\ProtectMe.lnk")
$shortcut.TargetPath = "$installDir\App\ProtectMe.exe"
$shortcut.WorkingDirectory = "$installDir\App"
$shortcut.Save()

Write-Host "ProtectMe installed and SYSTEM service is running!" -ForegroundColor Green
Write-Host "Launch ProtectMe from your Desktop shortcut to set your Master Password and Lock." -ForegroundColor Cyan
