# Backup Manager

Native Windows Backup Manager rebuilt in C#/.NET 8. It replaces the prior Python and PowerShell application completely.

## Requirements

- Windows Server 2016/2019/2022/2025
- .NET 8 SDK to build; .NET 8 Windows Desktop Runtime to run
- MySQL command-line tools for database export/import
- Inno Setup or WiX to create the installer

## Build

```powershell
dotnet restore BackupManager.sln
dotnet build BackupManager.sln -c Release
```

The .NET 8 SDK is installed on the build machine. A self-contained installer has been created at `installer\Output\BackupManagerSetup.exe`.

## Structure

- `src/BackupManager.Core` — domain models, schedule calculation, safe staging, ZIP validation and SHA-256.
- `src/BackupManager.Service` — Windows Service host for unattended scheduling.
- `src/BackupManager.Desktop` — WPF administration shell.
- `docs` — compatibility, architecture, security, deployment, restore, and troubleshooting guidance.

See [Windows compatibility](docs/WINDOWS-COMPATIBILITY.md), [architecture](docs/ARCHITECTURE.md), and [security](docs/SECURITY.md) before deployment.
