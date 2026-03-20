# Peak — Dynamic Island for Windows

![Peak](Peak.png)

A macOS/iOS-style Dynamic Island for Windows, built as a hobby project.

## Features

- **Dynamic Island** — collapsible notch at the top of your screen with Collapsed, Peek, and Expanded states
- **Widgets** — Clock, Weather, Media Controls, System Monitor, Network Stats, Calendar, Timer, Quick Access
- **Configurable Collapsed State** — 3 slots (Left, Center, Right) with selectable widgets
- **Audio Visualizer** — real-time music visualization as a separate circle next to the notch
- **Windows Notifications** — toast notifications peek through the island
- **Auto-Update** — checks GitHub Releases for new versions
- **Installer** — Inno Setup based installer with autostart support

## Tech Stack

- **WPF** / **.NET 8** / **C#**
- WASAPI (NAudio) for audio capture
- Windows Runtime APIs for media & notifications

## AI Disclaimer

This is a personal hobby project. AI (Claude) was used to assist with smaller implementation tasks during development.

## License

This project is not licensed for redistribution. All rights reserved.
