# ProtectMe for Windows

**ProtectMe for Windows** is an open-source Windows productivity and focus blocker by [Tbone Apps](https://tboneapps.com/), featuring a Black & Gold desktop interface paired with a tamper-resistant `LocalSystem` Windows Service.

## Features
- **Hold-to-Protect Lock**: Hold to activate protection; tap with your Master Password to unlock.
- **App Blocking & Offline Mode**: Block distracting Windows apps or keep them offline (with an optional 10-second login window).
- **Schedules**: Daily Set-Time or Weekly focus schedules.
- **Websites Pro (Whitelist-Only & Safe Browsing)**: Whitelist-only website protection, Safe Browsing, and Image/Video Search blocking across search engines.
- **Anti-Tamper Protection**: Runs as a protected `LocalSystem` service with password-protected uninstall.

## Building from Source
Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows:

```powershell
dotnet build ProtectMeWindows.sln -c Release
```

## Code Signing Policy
Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Privacy Policy
See [tboneapps.com/privacy-policy/protectme](https://tboneapps.com/privacy-policy/protectme/).
