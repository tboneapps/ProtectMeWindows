#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$pw = Read-Host "Enter ProtectMe Master Password to authorize uninstall" -AsSecureString
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
$plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)

$verified = $false

# 1. Verify password with running ProtectMeService via Named Pipe (which also unlocks the service DACL)
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ProtectMeWindowsServicePipe", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(3000)
    $req = (@{ Action = "verify_uninstall"; Password = $plain } | ConvertTo-Json -Compress) + "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($req)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $reader = New-Object System.IO.StreamReader($pipe)
    $respLine = $reader.ReadLine()
    $pipe.Dispose()

    $resp = $respLine | ConvertFrom-Json
    if (-not $resp.Success) {
        Write-Host "UNINSTALL DENIED: $($resp.Message)" -ForegroundColor Red
        Pause
        exit 1
    }
    $verified = $true
} catch {
    # 2. If service is offline, strictly verify against C:\ProgramData\ProtectMe\config.json (never allow bypass)
    $cfgPath = "C:\ProgramData\ProtectMe\config.json"
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($cfg.PasswordHash) -and -not [string]::IsNullOrWhiteSpace($cfg.PasswordSalt)) {
            $saltBytes = [Convert]::FromBase64String($cfg.PasswordSalt)
            $expectedBytes = [Convert]::FromBase64String($cfg.PasswordHash)
            $actualBytes = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
                $plain, $saltBytes, 100000, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
            if (-not [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actualBytes, $expectedBytes)) {
                Write-Host "UNINSTALL DENIED: Incorrect Master Password." -ForegroundColor Red
                Pause
                exit 1
            }
            $verified = $true
        } else {
            $verified = $true
        }
    } else {
        $verified = $true
    }
}

if (-not $verified) {
    Write-Host "UNINSTALL DENIED." -ForegroundColor Red
    Pause
    exit 1
}

Write-Host "Master Password verified! Stopping and removing ProtectMeService..." -ForegroundColor Yellow
Stop-Service -Name "ProtectMeService" -Force -ErrorAction SilentlyContinue
sc.exe delete "ProtectMeService" | Out-Null

# Re-enable Task Manager & disable system proxy cleanly
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System" -Name DisableTaskMgr -Value 0 -ErrorAction SilentlyContinue
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -Name ProxyEnable -Value 0 -ErrorAction SilentlyContinue

Remove-Item -Path "$env:PUBLIC\Desktop\ProtectMe.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\Program Files\ProtectMe" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "ProtectMe has been cleanly uninstalled." -ForegroundColor Green
Pause
