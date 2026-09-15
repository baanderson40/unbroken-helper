# Unbroken Helper

Unbroken Helper is an experimental Dalamud development plugin for inspecting and automating research around the Crucible of the Unbroken.

## Project Layout

- `UnbrokenHelper/Plugin.cs`: plugin entry point, route state machine, debug scanners, and automation.
- `UnbrokenHelper/Windows/`: debug window UI.
- `UnbrokenHelper/Data/BoardRoutes.json`: board and move route catalog, including inactive route data and discovered LGB collision positions.

## Build

Build the x64 Debug configuration with the Dalamud SDK installed:

```text
dotnet build UnbrokenHelper/UnbrokenHelper.csproj --configuration Debug -p:Platform=x64
```

The project currently expects the Dalamud development SDK and ECommons package to be available through the configured .NET environment.

## Status

This is development software. Board 2 B and the selectable Board 3A/3B routes are available to the automation. Board 2 A and Board 3 random/alternate branches are preserved in the route catalog but are not selected by the automation. First Master's Board data, collision positions, roster configuration, and Random-outcome handling are present, and its route is enabled for continued live verification. Board 3 and First Master's Board require their own configured familiar rosters and route-specific battle-move rosters. Temporal Sand is dispatched after board-exit transition settling; Thief's Eye and Merchant's Eye are dispatched after move load, before target selection and combat providers. Category 2 support, attack, and recovery items are dispatched automatically during combat or recovery; item consumption is verified from the HUD slot with up to three attempts per item ID. Live-game verification of addon callback behavior is still required.
