# ☀️ TaskHub Greeter

<div align="center">

**The wake, unlock, and sign-in companion app for [TaskHub](https://github.com/0-Name-Alex/TaskHub).**  
Welcomes you to your PC, presents your day at a glance, and gets out of your way.

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue?logo=windows&logoColor=white)](https://github.com/0-Name-Alex/TaskHubGreeter)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![Language](https://img.shields.io/badge/language-C%23%20%7C%20WPF-239120?logo=csharp&logoColor=white)](https://github.com/0-Name-Alex/TaskHubGreeter)
[![Tests](https://img.shields.io/badge/tests-46%20passed-success)](tests/)
[![Status](https://img.shields.io/badge/status-Active%20Development-orange)](#)
[![Companion](https://img.shields.io/badge/companion%20to-TaskHub-0ea5e9)](https://github.com/0-Name-Alex/TaskHub)

</div>

---

## 🌟 What is TaskHub Greeter?

TaskHub Greeter is a lightweight Windows companion app that greets you whenever you **sign in**, **unlock your laptop**, or **wake from sleep**. In a single elegant glance, it displays:
- ☀️ A time-based greeting & date
- 🚨 **Overdue tasks** that need attention immediately
- 📅 **Today's schedule** & next upcoming reminder time
- 🌤️ Live weather and daily quote
- ⚡ Quick-add task bar with full natural-language parsing

> 💡 **TaskHub is the source of truth.** Greeter stores zero data itself — everything communicates through TaskHub's local loopback REST API and WebSocket event bus with sub-millisecond response times.

---

## 🖼️ Interface Preview

```
┌────────────────────────────────────────────────────────┐
│  Good morning, Dhayananth!                  ⟳  ⚙  ✕   │
│  Monday, 23 November · 🌤️ 28°C Chennai                │
│  ┃ 3 due today · 1 overdue · next reminder at 10:30    │
├────────────────────────────────────────────────────────┤
│  ⚠️ OVERDUE                                            │
│  ☐ Renew server certificates       !!    Yesterday     │
│                                                        │
│  ☀️ TODAY                                              │
│  ☐ Pay rent #home                  !!!   09:00         │
│  ☐ Standup meeting #work                 09:30         │
│  ☐ Call the bank @Finance                10:30         │
│                                                        │
│  ＋ Quick Add: Pay rent tomorrow 9am #home !high       │
├────────────────────────────────────────────────────────┤
│  ✅ Done: Morning workout                  ↶ Undo (Ctrl+Z)│
└────────────────────────────────────────────────────────┘
```

---

## ✨ Features

- 🚀 **Smart Auto-Show Triggers**: Automatically appears on Windows sign-in, desktop unlock, and sleep resume (rate-limited to once every 30 mins, pauses if gaming or full-screen).
- ⚡ **Direct Actions**: Check tasks as done, snooze reminders, reschedule for tomorrow (`Ctrl+T`), or undo mistakes with `Ctrl+Z`.
- ⌨️ **Keyboard-First**: 
  - `Ctrl + Alt + G` — Global hotkey to toggle Greeter anywhere
  - `↑ / ↓` — Navigate tasks
  - `Space` — Toggle task complete / uncomplete
  - `Enter` — Submit quick add
  - `Esc` — Close immediately (or auto-closes after 20s of idle)
- 📡 **Real-time Sync**: Connects to TaskHub's WebSocket event stream (`/v1/events`) — changes made in TaskHub update in Greeter instantly.
- 🌤️ **Daily Extras**: Built-in Open-Meteo weather integration, iCal calendar meetings, and optional speech synthesis ("Read today aloud").

---

## 🛠️ Build and Run

### Requirements
- Windows 10 (1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`)
- Running instance of [TaskHub](https://github.com/0-Name-Alex/TaskHub)

### Commands

| Command | Action |
|---|---|
| `build.cmd` | Build, test, and launch the application |
| `.\build.ps1` | Build and run all unit tests |
| `.\build.ps1 -Publish` | Produce self-contained single-file `.exe` in `artifacts/` |
| `dotnet test` | Run the 46 integration and client tests |

The built binary is located at:
`src\TaskHubGreeter.App\bin\Debug\net10.0-windows\TaskHubGreeter.exe`

---

## ⚙️ Configuration

Settings are saved in `%APPDATA%\TaskHubGreeter\settings.json`. Changes apply immediately on save:

```json
{
  "name": "Dhayananth",
  "autoShow": {
    "signIn": true,
    "unlock": true,
    "resume": true,
    "returnAfterIdleMinutes": 15,
    "quietWhenFullScreen": true
  },
  "minMinutesBetweenAutoShows": 30,
  "hotkey": "Ctrl+Alt+G",
  "idleCloseSeconds": 20,
  "startWithWindows": true,
  "weather": {
    "enabled": true,
    "city": "Chennai",
    "unit": "celsius"
  }
}
```

---

## 📁 Architecture

```
TaskHubGreeter/
├── 📁 src/
│   ├── TaskHubGreeter.Client/     # net10.0 — REST API client, WebSocket stream & discovery
│   └── TaskHubGreeter.App/        # net10.0-windows — WPF UI, Win32 session presence, Tray
└── 📁 tests/
    └── TaskHubGreeter.Client.Tests/ # 46 automated unit & integration tests
```

---

## 📄 License

Open-source under the MIT license. Companion project to [TaskHub](https://github.com/0-Name-Alex/TaskHub).
