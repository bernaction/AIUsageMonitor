# AI Usage Monitor

A lightweight Windows desktop gadget that brings AI service usage limits into a single, glanceable view.

> [!NOTE]
> This project is in an early stage. Codex usage is read from its local signed-in session, and Claude usage is read from a protected Claude Web `sessionKey`. Gemini remains marked as not connected and does not display fabricated metrics.

## Features

- Transparent, frameless, draggable WPF window.
- Optional always-on-top mode.
- Separate 5-hour session and weekly usage indicators.
- Codex reserve usage and reset-credit information.
- Automatic Codex refresh using the local signed-in session.
- Automatic Claude refresh through a protected Claude Web session.
- Fixed provider-aware refresh intervals: five minutes for Codex and one minute for Claude.
- Configurable provider visibility and ordering.
- Resizable layout with scrolling when needed.
- Built-in About view with project, author, license, and version information.
- Automatic update availability checks through the latest stable GitHub Release.

Preferences are stored in `%APPDATA%\AIUsageMonitor\settings.json`.

### Connect Claude Web

1. Open `https://claude.ai/settings/usage` and sign in.
2. Open the browser developer tools (`F12`), then go to **Application** > **Cookies** > `https://claude.ai`.
3. Copy only the value of the `sessionKey` cookie. It begins with `sk-ant-`.
4. In AI Usage Monitor, open **Settings**, paste the value under **Claude Web connection**, and select **Save & connect**.

The value is checked before it is saved. AI Usage Monitor never displays the saved value again.

## Screenshots

AI Usage Monitor keeps usage windows visible at a glance, including session, weekly, reserve, and reset information when provided by the connected service.

### Main dashboard

![AI Usage Monitor dashboard](docs/screenshots/main.png)

### Settings

Choose which services are displayed, drag to change their order, and connect Claude Web securely.

![AI Usage Monitor settings](docs/screenshots/config.png)

## Requirements

- Windows 10 or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.
- A signed-in Codex session and/or a Claude Web session for live usage data.

## Run locally

```powershell
git clone https://github.com/bernaction/AIUsageMonitor.git
cd AIUsageMonitor
dotnet restore
dotnet run
```

## Privacy and authentication

- The app reads `~/.codex/auth.json` only to query usage limits for the account already signed in on the device.
- Paste only the `sessionKey` value from the `claude.ai` browser cookies in Settings. The app validates it before saving it in Windows Credential Manager.
- Credentials remain in memory only while requests are made and are never written to the regular settings file.
- Prompts, responses, and Codex session content are not read.
- Usage data is requested from the Codex/ChatGPT endpoint and the browser-facing Claude Web endpoint. These integrations may stop working if their endpoints change.
- The app checks the public GitHub Releases API for newer stable versions. No GitHub credentials are used.

Never commit `auth.json`, access tokens, or other credentials. See [SECURITY.md](SECURITY.md) for reporting security issues.

## Project structure

```text
Models/                 Usage and settings data models
Services/               Provider integration and settings persistence
App.xaml                WPF application resources
MainWindow.xaml         Window layout and styles
MainWindow.xaml.cs      UI behavior and provider presentation
AIUsageMonitor.csproj   .NET project configuration
```

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md), open an issue for significant changes, and submit improvements through pull requests.

## Roadmap

- Define the packaging, download, signature verification, and rollback strategy before enabling automatic updates.
- Add automated parsing tests for the Claude Web usage responses.
- Add a live Gemini integration with an unavailable-state fallback.
- Extract provider cards into reusable components.
- Persist window position, size, and theme.
- Add a system tray icon and optional Windows startup.
- Add automated tests for settings persistence and the remaining provider responses.

## License

Licensed under the [MIT License](LICENSE).
