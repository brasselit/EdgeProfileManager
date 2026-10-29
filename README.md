# Edge Profile Manager

A Windows tray and taskbar tool that starts Microsoft Edge profiles of all channels (Stable, Beta, Dev, Canary) or switches to their windows with a single click.

- **Tray menu, taskbar button and Jump List** with all your Edge profiles, grouped the way you like.
- **Window switching:** clicking a profile brings its open window to the front or starts the profile; Shift+click opens a new window.
- **Virtual desktops:** assign a desktop per profile; windows are moved there automatically.
- **Menu board:** arrange groups and profiles with drag and drop.
- **Profile management:** create, rename, color, back up, restore and move Edge profiles between channels.
- **Automatic updates** from GitHub Releases.

## Installation

Download `EdgeProfileManager-win-Setup.exe` from the [latest release](https://github.com/brasselit/EdgeProfileManager/releases/latest) and run it. It installs per user without administrator rights and adds the .NET 10 Desktop Runtime if needed. The setup is not code-signed yet, so Windows SmartScreen may warn on first start (**More info › Run anyway**).

## Documentation

The **[user manual](https://github.com/brasselit/EdgeProfileManager/wiki)** in the wiki covers installation, the tray menu, the menu board, profile actions, virtual desktops, the `config.json` reference, command line arguments and troubleshooting.

What changed in each version is listed in the [CHANGELOG](CHANGELOG.md).

## Development

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet run --project tests/EdgeProfileManager.Tests            # all tests
dotnet run --project tests/EdgeProfileManager.Tests -- Config  # matching tests only
.\src\EdgeProfileManager\bin\Debug\net10.0-windows\EdgeProfileManager.exe
```

- A build started this way points the Start menu shortcut to itself; updates are only available in the installed version.
- The test project has no NuGet dependencies and comes with a minimal test runner of its own.

### Structure

- `src/EdgeProfileManager.Core`: UI-free logic
  - profile discovery
  - window lookup via AppUserModelID (`MSEdge`, `MSEdge.UserData.Profile1`, …)
  - launcher, configuration, virtual desktops, profile management
- `src/EdgeProfileManager`: WPF/WinForms tray app with tray menu, Jump List, management window, single instance, autostart, Start menu shortcut and updates
- `tests/EdgeProfileManager.Tests`: tests for the core logic

### Virtual desktops

Windows has no public API to move other programs' windows between desktops. Therefore [VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor) is used:

- File: `lib/VirtualDesktopAccessor.dll`, release `2024-12-16-windows11` for 24H2
- SHA256: `8740c572a1c000e3b87ffeb1e4c397eae9af3bd4a2abdc3bcffacab4493f8ff5`
- Copied next to the exe automatically during build.

A major Windows update may require a newer version of the DLL.

### Release

Pushing a version tag builds and publishes a release with GitHub Actions ([release.yml](.github/workflows/release.yml)): tests, `dotnet publish`, setup and update packages with [Velopack](https://velopack.io), upload to GitHub Releases. The release notes come from [CHANGELOG.md](CHANGELOG.md); see [CONTRIBUTING.md](CONTRIBUTING.md#releases-maintainers) for the steps.

## Contributing

Bug reports, ideas and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[Apache License 2.0](LICENSE), Copyright 2026 Brassel IT. Notices for bundled third-party components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
