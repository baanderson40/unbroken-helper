# Unbroken Helper

Unbroken Helper is an experimental Dalamud development plugin for inspecting and automating research around the Crucible of the Unbroken.

## Project Layout

- `UnbrokenHelper/Plugin.cs`: plugin entry point, route state machine, debug scanners, and automation.
- `UnbrokenHelper/Windows/`: debug window UI.
- `UnbrokenHelper/Data/BoardRoutes.json`: board and stage route catalog, including inactive route data and discovered LGB collision positions.

## Build

Build the x64 Debug configuration with the Dalamud SDK installed:

```text
dotnet build UnbrokenHelper/UnbrokenHelper.csproj --configuration Debug -p:Platform=x64
```

The project currently expects the Dalamud development SDK and ECommons package to be available through the configured .NET environment.

## Status

This is development-stage software. Board 2 B is the active route. Board 2 A data is preserved in the route catalog but is not currently selected by the automation.
