# Edge Profile Manager

A tray and taskbar tool that starts Edge profiles of all channels (Stable, Beta, Dev, Canary) or switches to their windows with a single click.

## Features

- **Tray menu:** A click (left or right) shows all profiles, grouped as defined in `config.json`.
  - Profiles with an open window are shown in **bold**.
  - Clicking a profile activates its window if one is open; otherwise the profile is started.
  - If the active window already belongs to that profile, clicking again switches to the next window of the same profile.
  - **Shift+click** always opens a new window.
- **Taskbar and Start:** Pin the Start menu shortcut “Edge Profile Manager”. The easiest way is **Settings › Pin to Start…** or **Pin to taskbar…** in the tray menu: these items select the shortcut in Explorer and show which context menu command to choose. Windows does not let programs pin themselves.
  - Left-clicking the taskbar button shows the profile menu at the mouse pointer.
  - Right-clicking shows the **Jump List** with the profiles.
- **Virtual desktops:** A desktop can be assigned per profile (see `desktop` / `desktopMode`). This requires `VirtualDesktopAccessor.dll`; without the DLL the feature is disabled.
- **Live updates:**
  - Changes to `config.json` are applied immediately.
  - New or renamed Edge profiles are detected, because the tool watches Edge's `Local State` files.

## Managing profiles and the menu

Tray menu › **Manage profiles and menu…** or `EdgeProfileManager.exe --manage` opens the menu board:

- **Columns** are the groups of the tray menu: “Main menu” (profiles without a group) on the left, “Unassigned” on the right.
- **Cards** are profiles, showing profile color, avatar, channel, assigned desktop and a “running” hint.
- **Dragging a card:**
  - to another column: changes the group
  - onto a card: places it before that card
  - to “Unassigned”: removes it from the menu
- **Groups:** The ⋯ menu of a column offers Rename, Move left/right, Add separator, Add subgroup and Delete group. Create new groups with **+ Group**.
- The view can be filtered to one channel at the top.
- Every change is saved to `config.json` immediately; tray menu and Jump List follow right away.

Clicking a card shows its details on the right, split into two sections.

**Edge profile** – changes Edge itself; Edge must be closed for this (the tool offers to close it):

| Action | Description |
|---|---|
| Rename, Avatar | Edit buttons next to the name and the avatar; changes are written to `Preferences` and `Local State` |
| **Color** | Sets the profile's color theme (like “Appearance › Color” in Edge) as well as the avatar and highlight color. Choose from a palette or any custom color; “No color” restores the default theme. |
| Transfer ⇄ | Copies the profile to another channel (new or existing target profile) or performs a **verified move**: every file is checked with SHA256 and the source is deleted only afterwards. Menu entries are updated automatically. |
| Open, New window | Starts the profile or switches to its window (also by double-clicking the card) |
| Backup, Profile folder, Delete | Backup as ZIP including `meta.json`. Delete asks for confirmation first. |

**Menu entry** – changes `config.json` only: group, label (shows the Edge profile name in grey until you enter your own; clearing the field resets it), desktop, behavior when a window is already open, Jump List, “Remove from menu”. For unassigned profiles this section offers “Add to …”.

In the header:
- **+ New profile**: name, email and an optional PNG avatar; the email is stored in `customer-profile.json`
- **Restore**
- **Backup …**: scope is all channels, the channel of the selected profile, or only the selected profile
- **Settings** (gear): default behavior and Jump List

The backup, restore and transfer dialogs offer these options where they apply (the last choice is preselected):
- **Local data only**: transfers Local Storage, IndexedDB etc. instead of the whole profile.
- **Include caches**
- **Allow running source**: allows a backup or copy while Edge is open.

If an affected Edge channel is running, the tool asks whether it should be closed. It first closes the windows normally and then ends any remaining processes.

## Installation

Download `EdgeProfileManager-win-Setup.exe` from the [latest release](https://github.com/brasselit/EdgeProfileManager/releases/latest) and run it.

- Installs per user to `%LOCALAPPDATA%\EdgeProfileManager`; no administrator rights needed.
- Installs the .NET 10 Desktop Runtime if it is missing.
- After installation the tool starts in the tray. Pin it via **Settings › Pin to taskbar…** and enable autostart under **Settings › Start with Windows**.
- **Updates:** The tool checks the GitHub releases at start and once a day and shows a notification when a new version is available; clicking it installs the update and restarts the tool. **Settings › Check for updates…** checks manually; automatic checks can be turned off there as well.
- Uninstall via Windows **Settings › Apps**. This also removes the Start menu shortcut and the autostart entry; `config.json` is kept.

The setup is not code-signed yet, so Windows SmartScreen may warn on first start (**More info › Run anyway**).

## Build and run

```powershell
dotnet build
.\src\EdgeProfileManager\bin\Debug\net10.0-windows\EdgeProfileManager.exe
```

A build started this way points the Start menu shortcut to itself; updates are only available in the installed version.

### Release

Pushing a version tag builds and publishes a release with GitHub Actions ([release.yml](.github/workflows/release.yml)): tests, `dotnet publish`, setup and update packages with [Velopack](https://velopack.io), upload to GitHub Releases. The release notes come from [CHANGELOG.md](CHANGELOG.md); see [CONTRIBUTING.md](CONTRIBUTING.md#releases-maintainers) for the steps.

### Command line

| Call | Effect |
|---|---|
| *(no arguments)* | Show the profile menu at the mouse pointer (starts in the tray if needed) |
| `--background` | Start in the tray only (used for autostart) |
| `--manage` | Open the management window |
| `--open "Stable/Profile 1" [--new-window]` | Activate or start a profile (used by the Jump List) |
| `--dump-windows` | List Edge windows with detected profile, desktop state and AUMID |
| `--reload`, `--exit` | Reload the configuration or exit the tray instance |

If an instance is already running, the command is passed to it via a named pipe.

## Configuration

File: `%APPDATA%\EdgeProfileManager\config.json`. On first start it is created with one group per channel. Comments (`//`) and trailing commas are allowed.

```jsonc
{
  "defaultDesktopMode": "switchTo",   // switchTo | bringHere | moveToAssigned
  "showUnassigned": true,             // profiles not in the menu appear under "Unassigned"
  "jumpListUnassigned": false,        // ... and in the Jump List as well
  "jumpListStyle": "categories",      // categories | tasks
  "checkForUpdates": true,            // look for new releases at start and once a day
  "menu": [
    { "group": "Customers", "children": [
      { "profile": "Stable/Profile 3", "label": "Customer A", "desktop": 2 },
      { "group": "Archive", "children": [ { "profile": "Beta/Profile 1", "jumpList": false } ] }
    ]},
    { "separator": true },
    { "profile": "Stable/Default", "desktop": "Private", "desktopMode": "switchTo" }
  ]
}
```

- **`profile`**: `Channel/Folder`, e.g. `Stable/Default` or `Beta/Profile 4`. The folder stays the same when a profile is renamed. **Settings › Window diagnostics** or `--dump-windows` show the references.
- **`label`**: Display name in the menu. Defaults to the Edge profile name.
- **`desktop`**: Number (starting at 1) or name of the virtual desktop.
- **`desktopMode`**: What happens if a window is already open:
  - `switchTo`: switch to the desktop the window is on
  - `bringHere`: move the window to the current desktop
  - `moveToAssigned`: move the window to the assigned `desktop` and switch there. This is the default when `desktop` is set.

  New windows are always moved to the assigned `desktop`.
- **`jumpList`**: `false` hides the profile in the Jump List.

### Jump List notes

- Jump Lists have no submenus. Nested groups therefore appear as a category “Group › Subgroup”.
- With the `categories` style, Windows shows only about 10 entries per list. In addition, **Settings › Personalization › Start › “Show recently opened items in Start, Jump Lists, and File Explorer”** must be enabled.
- `"jumpListStyle": "tasks"` shows all profiles ungrouped in the “Tasks” section. This style is not affected by either limitation.

## Virtual desktops

Windows has no public API to move other programs' windows between desktops. Therefore [VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor) is used:

- File: `lib/VirtualDesktopAccessor.dll`, release `2024-12-16-windows11` for 24H2
- SHA256: `8740c572a1c000e3b87ffeb1e4c397eae9af3bd4a2abdc3bcffacab4493f8ff5`
- Copied next to the exe automatically during build.

A major Windows update may require a newer version of the DLL. The tray menu shows the status under **Settings**.

## Tests

```powershell
dotnet run --project tests/EdgeProfileManager.Tests            # all tests
dotnet run --project tests/EdgeProfileManager.Tests -- Config  # matching tests only
```

The test project has no NuGet dependencies and comes with a minimal test runner of its own.

## Structure

- `src/EdgeProfileManager.Core`: UI-free logic
  - profile discovery
  - window lookup via AppUserModelID (`MSEdge`, `MSEdge.UserData.Profile1`, …)
  - launcher, configuration, virtual desktops, profile management
- `src/EdgeProfileManager`: WPF/WinForms tray app with tray menu, Jump List, management window, single instance, autostart and Start menu shortcut

## Contributing

Bug reports, ideas and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Changes per version are listed in the [CHANGELOG](CHANGELOG.md).

## License

[Apache License 2.0](LICENSE), Copyright 2026 Brassel IT. Notices for bundled third-party components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
