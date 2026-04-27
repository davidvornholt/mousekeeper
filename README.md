# MouseKeeper

MouseKeeper is a small Windows app that gently moves the mouse after a short idle delay. It is meant for simple personal use: start it, leave it running, and stop it again when you no longer need it.

## Features

- Starts and stops from one button.
- Uses a global keyboard shortcut when one is available.
- Pauses immediately when you move the mouse yourself.
- Shows whether it is waiting, active, or off.

## Requirements

- Windows 10 version 1809 or newer.
- .NET SDK 8 or newer for building from source.

## Privacy

MouseKeeper runs locally and does not collect, store, transmit, sell, or share personal data. See [PRIVACY.md](PRIVACY.md).

## Build

Restore and build:

```powershell
dotnet restore
dotnet build MouseKeeper.csproj -c Release -p:Platform=x64
```

Create an MSIX package:

```powershell
dotnet build MouseKeeper.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package is written under `AppPackages/`.

## Distribution

MouseKeeper is intended to be distributed through the Microsoft Store as an MSIX package. Store distribution provides signing, installation, updates, Start Menu registration, and uninstall support through Windows Settings.

For local testing or private builds, you can also use the generated MSIX package or the loose publish output:

```powershell
dotnet publish MouseKeeper.csproj -c Release -p:Platform=x64
```
