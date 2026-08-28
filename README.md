# CPU Racer

A standalone Windows desktop game that clones the look of Task Manager's Performance → CPU tab, then turns the live CPU-usage line into a physics-based driving track. A real Box2D rigid-body car drives along your machine's actual CPU% in real time, jumping and flipping over the peaks and dips as they happen.

Inspired by [CPURacer](https://github.com/CS-LX/CPURacer), which overlays a car directly on Task Manager. This is a from-scratch, standalone reimplementation built because on higher-core-count machines idle CPU is often too flat for that overlay concept to be interesting on its own — so a smoothed random-walk noise layer (scaled by a difficulty curve) is mixed into the real CPU trace to keep the track varied and drivable even at idle.

## How it works

- The live "% Processor Time" counter is sampled every 100ms and converted into track height.
- A Box2D.NetStandard physics world simulates a real two-wheeled car: rigid chassis, two motorized wheels on revolute joints, gravity, friction, and collision against the CPU-trace ground.
- The track is built as chained one-sided edge segments (ghost-vertex linked) so the car doesn't catch or launch off segment seams when driving over slopes.
- The camera scrolls forward continuously, like the real Task Manager graph — falling too far behind ends the run, so standing still or reversing isn't a way to stall.

## Controls

| Key | Action |
|---|---|
| Up / W | Throttle up (lever, holds its setting) |
| Down / S | Throttle down |
| Left / A | Rotate car counter-clockwise |
| Right / D | Rotate car clockwise |
| Space | Jump (1s cooldown) / Restart after game over |
| R | Recover after flipping |

## Scoring

Score combines distance traveled and coins collected along the track (coins bias toward spawning on local peaks). Runs are recorded to a local leaderboard at `%AppData%\CpuRacer\leaderboard.json`.

## Requirements

- Windows
- .NET 10 SDK

## Running

```
dotnet run --project src/CpuRacer.App
```

## Stack

- WPF (.NET 10) for the UI shell and rendering
- [Box2D.NetStandard](https://www.nuget.org/packages/Box2D.NetStandard) for 2D rigid-body physics
- `System.Diagnostics.PerformanceCounter` for live CPU sampling
