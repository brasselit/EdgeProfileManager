# Contributing

Thanks for your interest in Edge Profile Manager! Bug reports, ideas and pull requests are welcome.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). Please report security issues privately as described in [SECURITY.md](SECURITY.md), not as public issues.

## Issues

- **Bugs:** Use the bug report template. Include Windows, Edge and tool version, and the relevant part of the log (tray › **Settings › Open log**).
- **Ideas and larger changes:** Open an issue first, so we can agree on the approach before you invest time.
- **Questions:** Use [Discussions](https://github.com/brasselit/EdgeProfileManager/discussions).

## Development

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet run --project tests/EdgeProfileManager.Tests
.\src\EdgeProfileManager\bin\Debug\net10.0-windows\EdgeProfileManager.exe
```

A debug build points the Start menu shortcut to itself. Reinstall or start the installed version afterwards to switch back.

## Branches and pull requests

We use [GitHub Flow](https://docs.github.com/get-started/using-github/github-flow): `main` is always releasable, all changes go through pull requests.

1. Create a short-lived branch from `main`, named by type: `feature/…`, `fix/…`, `docs/…` or `chore/…`.
2. Keep each pull request focused on one topic.
3. Make sure the CI check (build and tests) passes.
4. Pull requests are merged with **squash merge**, so the pull request title becomes the commit message. Write it in English, in the imperative ("Add …", "Fix …").

## Guidelines

- **Language:** Code, comments and documentation are in English (US spelling). UI texts live in resource files (see [Translations](#translations)), never as literals in code or XAML.
- **Style:** Match the surrounding code: naming, comment density and idioms. Keep UI logic out of `EdgeProfileManager.Core`.
- **Tests:** Add or adjust tests for logic in `EdgeProfileManager.Core`. The test project uses a small built-in runner without NuGet packages.
- **Dependencies:** Discuss new NuGet packages in an issue first.
- **Documentation:** The [user manual](https://github.com/brasselit/EdgeProfileManager/wiki) lives in the wiki. When behavior, UI texts or configuration change, mention the affected wiki pages in the pull request; the wiki is updated when the change is merged. The [README](README.md) only covers overview, installation and development.
- **Changelog:** Add an entry under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for every user-visible change (sections *Added*, *Changed*, *Fixed*, *Removed*). Purely internal changes need no entry.

## Translations

User-visible texts are stored in `.resx` resource files; a strongly typed class is generated for each during the build:

| File | Class | Content |
|---|---|---|
| `src/EdgeProfileManager.Core/Resources/Strings.resx` | `EdgeProfileManager.Core.Resources.Strings` | Messages of the core logic (errors, progress, menu titles) |
| `src/EdgeProfileManager/Resources/Strings.resx` | `EdgeProfileManager.Resources.Strings` | Tray menu, notifications, dialogs, Jump List, command line |
| `src/EdgeProfileManager/Resources/BoardStrings.resx` | `EdgeProfileManager.Resources.BoardStrings` | Menu board window |

The neutral file (`Strings.resx`) holds the English texts; translations live next to it as `Strings.<culture>.resx` (for example `Strings.de.resx`).

- **New text:** Add the key to the neutral file and to every translation. Use a prefix for the area (`Tray_…`, `Error_…`) and composite placeholders (`{0}`) for values; format with `string.Format(CultureInfo.CurrentCulture, …)`.
- **New language:**
  1. Copy each neutral `.resx` to `<name>.<culture>.resx` and translate the values.
  2. Add the language to `Localization.Languages` in `src/EdgeProfileManager/Localization.cs` and its culture to `Cultures` in `tests/EdgeProfileManager.Tests/LocalizationTests.cs`.
- Texts that only go to the log file and the `--dump-windows` diagnostics stay in English.
- `LocalizationTests` fails if a translation misses a key or uses different placeholders.

## Releases (maintainers)

1. In a pull request, rename `## [Unreleased]` in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, add a new empty `## [Unreleased]` above it and update the links at the bottom.
2. After merging, tag the merge commit and push the tag:

   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

3. The [release workflow](.github/workflows/release.yml) builds the setup and update packages and publishes the GitHub release with the changelog section as release notes.

Versions follow [Semantic Versioning](https://semver.org/). While the version is 0.x, the minor version is raised for new features and the patch version for fixes.

## License

Contributions are licensed under the [Apache License 2.0](LICENSE), like the project itself (see section 5 of the license).
