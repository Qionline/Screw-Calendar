# Screw Calendar

> Keep every day in order.

English · [简体中文](README.zh-CN.md)

[![Build](https://github.com/Qionline/Screw-Calendar/actions/workflows/build.yml/badge.svg)](https://github.com/Qionline/Screw-Calendar/actions/workflows/build.yml)
[![License: GPL v3 or later](https://img.shields.io/badge/License-GPL--3.0--or--later-blue.svg)](LICENSE)

Screw Calendar is a Windows 10/11 desktop calendar and local Markdown todo tool. It keeps dates, holidays, and todos on the desktop, with always-on-top mode, tray controls, multiple visual styles, and local data backups.

## Preview

### Calendar Mode

Use the calendar as a desktop widget to keep dates, solar terms, holidays, and daily plans within reach.

![Calendar mode preview](docs/images/readme/calendar-overview.png)

### Styles and Themes

Choose from Classic, Minimal, and Compact styles, combined with light, dark, and multiple accent themes.

![Styles and themes preview](docs/images/readme/themes-and-modes.png)

### Todo Mode

Persistent todos and date-based Markdown todos can be shown alongside the calendar, keeping everyday plans close at hand.

![Todo mode preview](docs/images/readme/todo-mode.png)

## Philosophy

Screw Calendar is not intended to be a full project-management suite. It keeps dates, holidays, and everyday todos visible on the desktop while keeping the interaction simple.

- **Local-first**: Calendar and todo content stays on your computer without accounts or cloud sync.
- **Low distraction**: Check and edit what you need, then let the desktop breathe when you do not.
- **Made for Windows**: Tray controls, always-on-top mode, startup options, and multi-monitor placement recovery.
- **Recoverable**: Automatic backups, damaged-file recovery, and JSON import/export.

Your calendar content stays local. Network access is only used to retrieve public holiday data, with cached and built-in fallbacks available when offline.

## Core Features

### Calendar

- Monday-to-Sunday seven-column calendar layout with 5–10 visible date rows.
- Month navigation and a quick return to today.
- Today highlighting, weekend distinction, Chinese solar terms, public holidays, and adjusted workdays.
- Background holiday updates with caching and offline fallbacks.

### Markdown Todos

- Persistent todos and date-based Markdown todos.
- A visual block editor with headings, lists, and task lists.
- Paste or drop images and archive them in local storage.
- JSON import and export for backup and migration.

### Desktop Experience

- Classic, Minimal, and Compact visual styles.
- Light / dark mode, multiple accent themes, and adjustable opacity.
- Always-on-top mode or placement below ordinary application windows.
- System tray controls, startup option, and position/size locking.
- Multi-monitor placement recovery and single-instance execution.
- Simplified Chinese and English interfaces.

### Local Data

- Calendar and todo content is not uploaded or synchronized to the cloud.
- Automatic main-file backups and recovery from a valid backup when needed.
- Images are archived in the local `assets` directory, so deleting the original image does not remove images already saved in todos.

## Download and Run

Download the ZIP archive from [GitHub Releases](https://github.com/Qionline/Screw-Calendar/releases), extract it, and run `ScrewCalendar.exe`. The accompanying `.sha256` file is optional and can be used to verify the download integrity and confirm that the archive matches the published file.

Current releases support Windows 10/11 x64 and do not require a separate .NET Runtime installation.

User data is stored under `%LocalAppData%\ScrewCalendar`, so replacing the application folder during an upgrade does not remove existing data. Windows may show a security warning the first time an unsigned open-source application is run; make sure the file came from GitHub Releases.

## Data and Privacy

Screw Calendar does not synchronize or upload calendar content. It retrieves public holiday JSON from [`holiday-cn`](https://github.com/NateScarlet/holiday-cn) when checking the current and following year. Successful downloads are cached for 24 hours, and cached or built-in data is used when the network is unavailable.

Data is stored under:

```text
%LocalAppData%\ScrewCalendar\
├─ data\calendar.json
├─ data\calendar.json.bak
├─ data\assets\
├─ data\holiday-cache\
└─ logs\app.log
```

JSON exports do not include images in `assets`. To migrate todos with images to another computer, copy the `assets` folder as well. See the [user guide](docs/USER_GUIDE.md) for the complete procedure.

## Documentation

- [User guide](docs/USER_GUIDE.md): Downloading, first launch, calendar, todos, data migration, and daily usage.
- [Development guide](docs/DEVELOPMENT.md): Project structure, data rules, UI constraints, and coding conventions.
- [Release guide](docs/RELEASING.md): Versioning, tags, builds, and GitHub Releases.
- [Contributing guide](CONTRIBUTING.md): Changes, tests, and merge requirements.
- [Security policy](SECURITY.md): How to report security issues.
- [Third-party notices](THIRD_PARTY_NOTICES.md): Dependencies, data sources, and visual assets.

## Build from Source

Install the .NET 8 SDK at or above the baseline version in [global.json](global.json), then run these commands from the repository root:

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config --locked-mode
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
dotnet format .\ScrewCalendar.sln --verify-no-changes --no-restore
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
```

See the [development guide](docs/DEVELOPMENT.md) and [release guide](docs/RELEASING.md) for the full architecture, coding conventions, and release workflow.

## Contributing

Before submitting changes, read the [contributing guide](CONTRIBUTING.md), [code of conduct](CODE_OF_CONDUCT.md), and [UI style contract](UI_STYLE_CONTRACT.md). Please report security issues privately according to the [security policy](SECURITY.md).

## License

Copyright © 2026 Qionline.

This project is licensed under the [GNU General Public License v3.0 or later](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party software, data, and visual asset notices.
