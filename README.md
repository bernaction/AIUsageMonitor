# AI Usage Gadget

A lightweight Windows desktop gadget that brings AI service usage limits into a single, glanceable view.

> [!NOTE]
> This project is an early-stage prototype. Codex usage is read from the local signed-in session; Claude and Gemini currently display sample data.

## Features

- Transparent, frameless, draggable WPF window.
- Optional always-on-top mode.
- Separate 5-hour session and weekly usage indicators.
- Codex reserve usage and reset-credit information.
- Automatic Codex refresh using the local signed-in session.
- Configurable provider visibility, ordering, and refresh interval.
- Resizable layout with scrolling when needed.

Preferences are stored in `%APPDATA%\AIUsageGadget\settings.json`.

## Requirements

- Windows 10 or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.
- A signed-in Codex session for live Codex usage data.

## Run locally

```powershell
git clone https://github.com/bernaction/AIUsageGadget.git
cd AIUsageGadget
dotnet restore
dotnet run
```

## Privacy and authentication

- The app reads `~/.codex/auth.json` only to query usage limits for the account already signed in on the device.
- The access token remains in memory for the request and is never stored by this app.
- Prompts, responses, and Codex session content are not read.
- Usage data is requested from an internal Codex/ChatGPT endpoint. This integration may stop working if that endpoint changes.

Never commit `auth.json`, access tokens, or other credentials. See [SECURITY.md](SECURITY.md) for reporting security issues.

## Project structure

```text
Models/                 Usage and settings data models
Services/               Provider integration and settings persistence
App.xaml                WPF application resources
MainWindow.xaml         Window layout and styles
MainWindow.xaml.cs      UI behavior and provider presentation
AIUsageGadget.csproj    .NET project configuration
```

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md), open an issue for significant changes, and submit improvements through pull requests.

## Roadmap

- Extract provider cards into reusable components.
- Add live Claude and Gemini integrations with unavailable-state fallbacks.
- Persist window position, size, and theme.
- Add a system tray icon and optional Windows startup.
- Add automated tests for response parsing and settings persistence.

## License

Licensed under the [MIT License](LICENSE).
