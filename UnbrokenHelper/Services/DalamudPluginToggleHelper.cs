using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace UnbrokenHelper.Services;

// Dalamud's public plugin API does not expose profile-aware unload/reload operations.
// Keep this reflection isolated because it depends on Dalamud internals.
internal sealed class DalamudPluginToggleHelper
{
    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    public DalamudPluginToggleHelper(IDalamudPluginInterface pluginInterface, IPlayerState playerState, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.playerState = playerState;
        this.log = log;
    }

    public bool TryFindLoadedBossMod(out string? internalName, out string? failureReason)
    {
        internalName = null;
        failureReason = null;
        foreach (var candidate in new[] { "BossModReborn", "BossMod" })
        {
            if (!TryGetInstalledPluginEntry(candidate, out var plugin, logIfMissing: false) || plugin == null)
            {
                continue;
            }

            if (GetFoP(plugin, "IsLoaded") is bool loaded && loaded)
            {
                internalName = candidate;
                return true;
            }
        }

        failureReason = "No loaded BossMod or BossModReborn plugin was found.";
        return false;
    }

    public bool TryCycle(string internalName, out Task? operationTask, out string? failureReason)
    {
        operationTask = null;
        failureReason = null;
        try
        {
            if (!TryGetInstalledPluginEntry(internalName, out var plugin, logIfMissing: true) || plugin == null)
            {
                failureReason = $"{internalName} is not installed.";
                return false;
            }

            if (!TryBuildContext(plugin, internalName, out var context, out failureReason))
            {
                return false;
            }

            if (context.BlockedReason != null)
            {
                failureReason = context.BlockedReason;
                return false;
            }

            if (!context.IsLoaded)
            {
                failureReason = $"{internalName} is not loaded.";
                return false;
            }

            operationTask = CycleAsync(context, internalName);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Failed to cycle {internalName}.";
            log.Debug($"[BossModToggle] {failureReason} {ex}");
            return false;
        }
    }

    private async Task CycleAsync(PluginToggleContext context, string internalName)
    {
        log.Debug($"[BossModToggle] Cycling {internalName} via profile-aware reflected toggle.");
        await InvokeUnload(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await WaitForLoadedState(context.LocalPlugin, expected: false, internalName).ConfigureAwait(false);
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, enabled: false).ConfigureAwait(false);
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, enabled: true).ConfigureAwait(false);
        await InvokeLoad(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await WaitForLoadedState(context.LocalPlugin, expected: true, internalName).ConfigureAwait(false);
        log.Debug($"[BossModToggle] Finished cycling {internalName}.");
    }

    private static async Task WaitForLoadedState(object plugin, bool expected, string internalName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (GetFoP(plugin, "IsLoaded") is bool loaded && loaded == expected)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {internalName} loaded={expected}.");
    }

    private sealed record PluginToggleContext(
        object LocalPlugin,
        Type LocalPluginType,
        Guid WorkingPluginId,
        bool IsLoaded,
        object? ApplicableProfile,
        string? BlockedReason);

    private bool TryBuildContext(object plugin, string internalName, out PluginToggleContext context, out string? failureReason)
    {
        context = null!;
        failureReason = null;
        var type = plugin.GetType();
        var loaded = GetFoP(plugin, "IsLoaded") is bool isLoaded && isLoaded;
        if (GetFoP(plugin, "EffectiveWorkingPluginId") is not Guid id || id == Guid.Empty)
        {
            failureReason = $"Could not resolve {internalName}'s plugin id.";
            return false;
        }

        var manager = GetDalamudService("Dalamud.Plugin.Internal.Profiles.ProfileManager");
        if (manager == null || GetFoP(manager, "Profiles") is not IEnumerable profiles)
        {
            failureReason = "Could not read Dalamud plugin profiles.";
            return false;
        }

        var matching = new List<object>();
        foreach (var profile in profiles)
        {
            if (profile == null || !TryInvokeBool(profile, "WantsPlugin", [typeof(Guid)], [id], out var wants))
            {
                if (profile != null)
                {
                    failureReason = "Could not inspect Dalamud plugin profile assignments.";
                }

                return false;
            }

            if (wants)
            {
                matching.Add(profile);
            }
        }

        var defaultProfile = GetFoP(manager, "DefaultProfile");
        if (!TryInvokeBool(manager, "IsInDefaultProfile", [typeof(Guid)], [id], out var inDefault))
        {
            failureReason = "Could not determine the default plugin profile state.";
            return false;
        }

        object? applicable = matching.Count == 0
            ? defaultProfile
            : inDefault
                ? defaultProfile
                : matching.Count == 1
                    ? matching[0]
                    : null;
        var blocked = matching.Count > 1 ? $"{internalName} is assigned to multiple plugin profiles." : null;
        if (blocked == null && applicable == null)
        {
            failureReason = $"Could not find an applicable plugin profile for {internalName}.";
            return false;
        }

        if (blocked == null && applicable != null && GetFoP(applicable, "IsDefaultProfile") is bool isDefault && !isDefault)
        {
            var profileName = GetFoP<string>(applicable, "Name") ?? "unknown";
            if (GetFoP(applicable, "IsEnabled") is bool profileEnabled && !profileEnabled)
            {
                blocked = $"{internalName} belongs to disabled plugin profile '{profileName}'.";
            }
            else if (!TryInvokeBool(applicable, "CheckWantsActiveFromGameState", [typeof(ulong)], [playerState.ContentId], out var active))
            {
                failureReason = $"Could not determine whether plugin profile '{profileName}' is active.";
                return false;
            }
            else if (!active)
            {
                blocked = $"{internalName} belongs to inactive plugin profile '{profileName}'.";
            }
        }

        context = new PluginToggleContext(plugin, type, id, loaded, applicable, blocked);
        return true;
    }

    private Task InvokeProfileUpdate(object profile, Guid id, string internalName, bool enabled)
    {
        var method = profile.GetType().GetMethod(
            "AddOrUpdateAsync",
            AllFlags,
            binder: null,
            types: [typeof(Guid), typeof(string), typeof(bool), typeof(bool)],
            modifiers: null);
        if (method?.Invoke(profile, [id, internalName, enabled, false]) is Task task)
        {
            return task;
        }

        throw new InvalidOperationException($"Could not update {internalName}'s plugin profile state.");
    }

    private static Task InvokeLoad(object plugin, Type pluginType, string internalName)
    {
        var method = pluginType.GetMethod(
                         "LoadAsync",
                         AllFlags,
                         binder: null,
                         types: [typeof(PluginLoadReason), typeof(bool), typeof(CancellationToken)],
                         modifiers: null)
                     ?? pluginType.GetMethod(
                         "LoadAsync",
                         AllFlags,
                         binder: null,
                         types: [typeof(PluginLoadReason), typeof(bool)],
                         modifiers: null);
        if (method == null)
        {
            throw new InvalidOperationException($"Could not load {internalName}.");
        }

        object?[] args = method.GetParameters().Length == 3
            ? [PluginLoadReason.Installer, false, CancellationToken.None]
            : [PluginLoadReason.Installer, false];
        return method.Invoke(plugin, args) as Task
            ?? throw new InvalidOperationException($"Could not load {internalName}.");
    }

    private static Task InvokeUnload(object plugin, Type pluginType, string internalName)
    {
        var modeType = pluginInterfaceAssembly(plugin).GetType(
                           "Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode",
                           throwOnError: true)
                       ?? throw new InvalidOperationException("Could not resolve plugin disposal mode.");
        var method = pluginType.GetMethod(
                         "UnloadAsync",
                         AllFlags,
                         binder: null,
                         types: [modeType],
                         modifiers: null)
                     ?? throw new InvalidOperationException($"Could not unload {internalName}.");
        var mode = Enum.Parse(modeType, "WaitBeforeDispose");
        return method.Invoke(plugin, [mode]) as Task
            ?? throw new InvalidOperationException($"Could not unload {internalName}.");
    }

    private static Assembly pluginInterfaceAssembly(object plugin)
        => plugin.GetType().Assembly;

    private bool TryGetInstalledPluginEntry(string internalName, out object? pluginEntry, bool logIfMissing)
    {
        pluginEntry = null;
        var pluginManager = GetDalamudService("Dalamud.Plugin.Internal.PluginManager");
        if (pluginManager == null)
        {
            log.Debug("[BossModToggle] Could not resolve Dalamud plugin manager.");
            return false;
        }

        var installedPluginsValue = GetFoP(pluginManager, "InstalledPlugins");
        if (installedPluginsValue is not IEnumerable installedPlugins)
        {
            log.Debug("[BossModToggle] Could not read Dalamud installed plugins.");
            return false;
        }

        object? firstMatch = null;
        foreach (var plugin in installedPlugins)
        {
            if (plugin != null && string.Equals(GetFoP<string>(plugin, "InternalName"), internalName, StringComparison.OrdinalIgnoreCase))
            {
                firstMatch ??= plugin;
                if (GetFoP(plugin, "IsLoaded") is bool loaded && loaded)
                {
                    pluginEntry = plugin;
                    return true;
                }
            }
        }

        if (logIfMissing)
        {
            log.Debug($"[BossModToggle] Plugin entry {internalName} was not found.");
        }

        pluginEntry = firstMatch;
        return pluginEntry != null;
    }

    private object? GetDalamudService(string serviceName)
    {
        try
        {
            var assembly = pluginInterface.GetType().Assembly;
            var serviceType = assembly.GetType("Dalamud.Service`1", throwOnError: true);
            var targetType = assembly.GetType(serviceName, throwOnError: true);
            return serviceType?.MakeGenericType(targetType!)
                .GetMethod("Get", AllFlags)
                ?.Invoke(null, Array.Empty<object>());
        }
        catch (Exception ex)
        {
            log.Debug($"[BossModToggle] Could not resolve Dalamud service {serviceName}: {ex.Message}");
            return null;
        }
    }

    private static T? GetFoP<T>(object obj, string name)
        => (T?)GetFoP(obj, name);

    private static object? GetFoP(object obj, string name)
    {
        var type = obj.GetType();
        while (type != null)
        {
            var field = type.GetField(name, AllFlags);
            if (field != null)
            {
                return field.GetValue(obj);
            }

            var property = type.GetProperty(name, AllFlags);
            if (property != null)
            {
                return property.GetValue(obj);
            }

            type = type.BaseType;
        }

        return null;
    }

    private static bool TryInvokeBool(object target, string name, Type[] parameterTypes, object?[] args, out bool result)
    {
        result = false;
        var method = target.GetType().GetMethod(name, AllFlags, binder: null, types: parameterTypes, modifiers: null);
        if (method?.Invoke(target, args) is not bool value)
        {
            return false;
        }

        result = value;
        return true;
    }
}
