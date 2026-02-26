# FrontlineMap

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for [Nuclear Option](https://store.steampowered.com/app/2150800/Nuclear_Option/) that visualizes the frontline between factions on the in-game map.

![Nuclear Option](https://img.shields.io/badge/Nuclear_Option-mod-blue)
![BepInEx](https://img.shields.io/badge/BepInEx-5.x-green)
![.NET](https://img.shields.io/badge/.NET-4.7.2-purple)

## Features

- **Influence-based frontline calculation** — Uses unit positions and types to compute an influence map, then renders the frontline where faction influence meets
- **Conflict intensity visualization** — Solid lines where fighting is intense, dashed lines where the frontline is weak
- **Frontline shift indicators** — Shows which faction is advancing with colored chevron markers
- **Territory tinting** — Subtle color overlay showing faction-controlled areas
- **Minimap support** — Overlay follows map zoom, pan, and works on both full map and minimap
- **Toggle with F9** — Press F9 to show/hide the overlay

## Installation

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) for Nuclear Option
2. Download `FrontlineMap.dll` from [Releases](../../releases)
3. Place `FrontlineMap.dll` in `BepInEx/plugins/`
4. Launch the game

## Configuration

After first launch, edit `BepInEx/config/com.yuulf.frontlinemap.cfg`:

| Setting | Default | Description |
|---|---|---|
| GridResolution | 400 | Influence grid resolution (64-512) |
| UpdateInterval | 5 | Seconds between recalculation |
| InfluenceRadius | 12000 | How far units project influence (meters) |
| TerritoryAlpha | 0.012 | Territory overlay opacity |
| ToggleKey | F9 | Key to toggle overlay |

### Unit Weights

| Unit Type | Weight | Description |
|---|---|---|
| Airbase | 10 | Strongest influence anchor |
| Ship | 5 | Naval presence |
| Vehicle | 3 | Ground forces |
| Aircraft | 0.5 | Minor, mobile influence |

## How It Works

The mod computes a 2D influence grid covering the entire map. Each active unit stamps influence with quadratic distance falloff. The frontline is rendered where the influence sign changes (zero-crossing). Gradient magnitude determines line style, and tick-over-tick comparison reveals frontline movement direction.

## Building

Requires references to Nuclear Option's managed DLLs:
- `Assembly-CSharp.dll`
- `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.UI.dll`, `UnityEngine.InputLegacyModule.dll`
- `BepInEx.dll`, `0Harmony.dll`
- `Mirage.dll`

```bash
dotnet build -c Release
```

Output: `bin/Release/net472/FrontlineMap.dll`

## License

MIT License
