# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Contribution guidelines, code of conduct, security policy, issue and pull request templates.
- CI workflow that builds and tests every pull request.
- Dependabot updates for NuGet packages and GitHub Actions.

### Changed

- Release notes are taken from this changelog; a release fails if its version has no section here.
- The release workflow uses the `vpk` version that matches the Velopack package.
- Release workflow actions updated to versions running on Node.js 24.

## [0.1.0] - 2026-10-29

First public release.

### Added

- Tray menu with all Edge profiles of all channels (Stable, Beta, Dev, Canary), grouped as configured; clicking a profile switches to its window or starts it.
- Taskbar button and Jump List via the Start menu shortcut.
- Virtual desktop assignment per profile (via VirtualDesktopAccessor).
- Menu board to manage profiles and the tray menu: groups as columns, drag and drop, detail pane for Edge profile and menu entry.
- Profile management: create, rename, avatar, color, transfer to another channel (copy or verified move), backup, restore, delete.
- Setup and automatic updates via Velopack and GitHub Releases.

[Unreleased]: https://github.com/brasselit/EdgeProfileManager/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/brasselit/EdgeProfileManager/releases/tag/v0.1.0
