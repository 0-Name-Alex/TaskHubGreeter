# TaskHubGreeter — AI Agent Guide

Lightweight wake, unlock, and Windows sign-in companion application for [**TaskHub**](https://github.com/0-Name-Alex/TaskHub) built on **.NET 10 / C# / WPF**.

## 1. Architecture & Integration Contract
- **`TaskHubGreeter.Client`** (`net10.0`): Connects to TaskHub's local loopback HTTP REST API and WebSocket event stream (`/v1/events`) on `127.0.0.1` using auto-discovery. **TaskHub is the single source of truth**—Greeter does not own a task database.
- **`TaskHubGreeter.App`** (`net10.0-windows`): WPF overlay UI (`GreeterWindow`, `GreeterViewModel`, `GreeterController`), Windows session presence monitor (`Presence.cs`, detects sign-in/unlock/sleep resume/idle return + fullscreen gaming suppression), hotkey `Ctrl+Alt+G`, weather/daily extras (`ExtrasService.cs`), and settings (`%APPDATA%\TaskHubGreeter\settings.json`).
- **`TaskHubGreeter.Client.Tests`** (`tests/`): Automated unit & integration tests (46 tests).

## 2. Build, Test & Publish Commands
- **Build + test**: `.\build.ps1` (or `build.cmd` — automatically kills running `TaskHubGreeter.exe` so the binary isn't locked).
- **Run tests only**: `dotnet test TaskHubGreeter.slnx -c Release`
- **Build, test & launch**: `.\build.ps1 -Run`
- **Publish self-contained single-file**: `.\build.ps1 -Publish` -> `artifacts/TaskHubGreeter/TaskHubGreeter.exe`

## 3. Rules & Gotchas
- Keep `TaskHubGreeter.Client` dependency-free from WPF/Win32 so tests run cleanly on `net10.0`.
- When modifying API contracts or WebSocket `/v1/events` parsing, verify compatibility against `D:\Projects\TaskHub\src\TaskHub.Abstractions` and `TaskHub.Api`.
- Keep auto-show rate-limiting (`minMinutesBetweenAutoShows`) and fullscreen suppression intact to avoid interrupting games or presentations.
