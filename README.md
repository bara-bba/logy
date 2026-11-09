# logy

A small floating Windows window that shows the commit graphs of several git repos side by side,
with one-click **Fetch**, **Pull** (fast-forward only) and **Push** per repo.

- Lists the repos directly under a root folder (default `%USERPROFILE%\git`, changeable in Settings).
- **+** on the right edge adds a repo panel; **x** closes it.
- Click a commit to see its full message and changed files.
- Accent colour (white by default) and tree colours (presets, Random, Custom) in Settings.
- Runs your own `git.exe`, so GitHub and CodeCommit (`git-remote-codecommit`) remotes work as in a terminal.
- Nothing runs in the background: closing the window quits logy.

Design: `docs/designs/2026-11-09-logy-v0.1.md`.

## Build

```
dotnet build logy.sln -c Release
dotnet test logy.sln -c Release --no-build
```

The exe is `src/logy/bin/Release/net8.0-windows10.0.19041.0/logy.exe` (needs the .NET 8 Desktop Runtime).

## Release

Push a tag `vX.Y.Z`. `.github/workflows/release.yml` builds, packs a Velopack installer and
publishes the GitHub Release `logy X.Y.Z` with `logy-win-Setup.exe` and `logy-win-Portable.zip`.
Installed copies check for updates once at startup.

## Icons

`python tools/make_icon.py` regenerates `src/logy/app.ico` and the previews in `design/`.
