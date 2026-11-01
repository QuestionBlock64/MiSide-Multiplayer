# QBLib APIs

QBLib is included in the `MiSideMultiplayer.dll` plugin. A plugin that uses
these APIs should declare a BepInEx dependency on `MS_Multiplayer` and reference
that assembly at compile time.

## Notifications

Notifications clone MiSide's built-in `GameController/Interface/HintScreen`
prefab, so they use the game's own visual style and hide animation. Calls are
queued and created on the Unity main thread. The UI is resolved lazily, so a
notification requested during a scene transition can appear once gameplay UI
is available (up to 15 seconds later).

```csharp
QBLib.Notifications.Show("Run started", durationSeconds: 4f);
QBLib.Notifications.Show(
    "Low connection", durationSeconds: 5f,
    deduplicationKey: "speedrun.low-connection", cooldownSeconds: 30f);
```

Convenience notifications are available for shared multiplayer states:

```csharp
QBLib.Notifications.ShowMinigameInProgress();
QBLib.Notifications.ShowLowConnection();

// For recurring notices, keep and dispose the registration when the feature ends.
IDisposable refreshNotice = QBLib.Notifications.RegisterPeriodic(
    () => "Refresh rate: " + Screen.currentResolution.refreshRate + " Hz",
    intervalSeconds: 300f, deduplicationKey: "my-mod.refresh-rate",
    cooldownSeconds: 150f);
```

`Show` returns false for empty text or if the request could not be queued. A
repeated key is coalesced while queued or while its notification is visible.
`RegisterPeriodic` evaluates its text provider on the Unity main thread and
returns an `IDisposable` registration handle.

## Harmony patches

QBLib exposes helpers for the same assembly and patch-class `PatchAll` flow
used by SpeedrunMod. Give every mod a stable, unique owner ID:

```csharp
QBLib.Patches.ApplyAll("my.speedrun.plugin", typeof(MyGamePatches));
// Or install all [HarmonyPatch] classes in an assembly:
QBLib.Patches.ApplyAll("my.speedrun.plugin", typeof(MyPlugin).Assembly);

// Remove only this mod's patches when unloading:
QBLib.Patches.Remove("my.speedrun.plugin");
```

QBLib does not bundle a second patching library: it uses BepInEx's HarmonyX
runtime, already used by MiSide and SpeedrunMod.

## Multiplayer state

Mods that need to distinguish solo practice from a shared run can poll the
read-only session and relay state:

```csharp
if (QBLib.Session.HasStarted)
{
    bool host = QBLib.Session.IsHost;
    int participants = QBLib.Session.PlayerCount;
}

bool connected = QBLib.Network.IsConnected;
bool connectionWarning = QBLib.Network.IsLowConnection;
```

The connection warning is based on server keepalive freshness: the relay sends
one every two seconds, and QBLib marks the connection low after 5.5 seconds
without one. It indicates delayed or missing relay heartbeats rather than a
measured round-trip-time value.
