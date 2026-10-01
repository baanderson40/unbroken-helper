# Unbroken Helper

## Build

- Requires .NET 10 SDK, Dalamud API 15 development files, Windows/x64 runtime dependencies, and ECommons.
- Focused build: `dotnet build UnbrokenHelper/UnbrokenHelper.csproj --configuration Debug -p:Platform=x64`.
- This repository has one production project and no tests, CI, lint/format command, or code generator.
- Preserve `UnbrokenHelper/packages.lock.json`; do not edit `bin/`, `obj/`, `.vs/`, or generated outputs.

## Architecture

- `UnbrokenHelper/Plugin.cs` is the large Dalamud entrypoint and central automation/state machine.
- `Windows/` contains main and debug UI; `Data/` contains route/combat models and runtime JSON assets.
- `Services/DalamudPluginToggleHelper.cs` is the reflected Dalamud plugin-lifecycle boundary.
- Runtime integrations include ECommons, vnavmesh, BossMod/BossModReborn, and RotationSolver Reborn IPC.
- `Data/BoardRoutes.json`, `Data/FamiliarCombat.json`, and `Data/UnbrokenBossModPreset.json` are source assets copied to output, not generated files. Route and familiar loaders require `schemaVersion == 1`.

This is experimental automation. Board routes, addon callbacks, IPC ownership, item consumption, and battle-state changes require live FFXIV/Dalamud validation; no automated suite covers them.
