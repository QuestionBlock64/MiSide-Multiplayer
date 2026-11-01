using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using MiSideMultiplayer.API.Events;
using MiSideMultiplayer.API.Factories;
using HarmonyLib;
using System.Reflection;
using System.Collections.ObjectModel;
using MiSideMultiplayer.Runtime.Lobby;

namespace MiSideMultiplayer.API
{
    public static class QBLib
    {
        // ── Menu access ────────────────────────────────────────────────────
        public static MenuManager Menu => MenuManager.Instance;
        public static GameMenu FindMenu(string name) => MenuManager.Instance?.Find(name);
        public static GameMenu GetModMenu(string modName) => SettingsMenuManager.GetModMenu(modName);

        // ── Events ─────────────────────────────────────────────────────────
        public static void OnInitialized(System.Action listener)
            => InitializedEvent.AddEventListener(listener);

        // ── Factories ──────────────────────────────────────────────────────
        public static MenuFactory NewMenu() => new MenuFactory();
        public static MenuOptionFactory NewOption() => new MenuOptionFactory();

        /// <summary>Read-only multiplayer session state for compatible mods.</summary>
        public static class Session
        {
            public static SessionController Current => SessionController.Active;
            public static bool IsInSession => Current != null;
            public static bool HasStarted => Current != null && Current.SessionStarted;
            public static bool IsHost => Current != null && Current.IsHost;
            public static string LocalPlayerId => Current != null ? Current.LocalPlayerId : string.Empty;
            public static int PlayerCount => Current != null ? Current.Players.Count : 0;
            public static IReadOnlyDictionary<string, SessionPlayer> Players =>
                Current != null ? Current.Players : EmptyPlayers;

            private static readonly IReadOnlyDictionary<string, SessionPlayer> EmptyPlayers =
                new ReadOnlyDictionary<string, SessionPlayer>(
                    new Dictionary<string, SessionPlayer>(StringComparer.Ordinal));
        }

        /// <summary>Relay state exposed to compatible mods and overlays.</summary>
        public static class Network
        {
            public static bool IsConnected => MiSideMultiplayerPlugin.IsRelayConnected();
            public static bool IsLowConnection => MiSideMultiplayerPlugin.IsLowConnection();
            public static RpcDispatcher Dispatcher => MiSideMultiplayerPlugin.GetRpcDispatcher();
        }

        /// <summary>MiSide-native hint notifications shared by mods.</summary>
        public static class Notifications
        {
            /// <summary>Queues a notification for the built-in HintScreen UI.</summary>
            public static bool Show(string text, float durationSeconds = 5f,
                                    string deduplicationKey = null, float cooldownSeconds = 0f)
                => MiSideMultiplayer.API.Notifications.NotificationManager.Enqueue(
                    text, durationSeconds, deduplicationKey, cooldownSeconds);

            /// <summary>Shows the standard exclusive-minigame busy notice.</summary>
            public static bool ShowMinigameInProgress()
                => Show("A player is already in this minigame.", 4f,
                        "qblib.minigame-in-progress", 2f);

            /// <summary>Shows who currently owns an exclusive minigame.</summary>
            public static bool ShowMinigameInProgress(string playerName, string minigameId)
            {
                string name = string.IsNullOrWhiteSpace(playerName)
                    ? "A player"
                    : playerName.Trim();
                string id = string.IsNullOrWhiteSpace(minigameId)
                    ? "unknown"
                    : minigameId.Trim();
                return Show(name + " is in this minigame.", 4f,
                    "qblib.minigame-in-progress." + id + "." + name, 2f);
            }

            /// <summary>Shows the standard relay-quality warning.</summary>
            public static bool ShowLowConnection()
                => MiSideMultiplayer.API.Notifications.NotificationManager.EnqueuePersistent(
                    "Low connection", "qblib.low-connection");

            /// <summary>Clears the relay-quality warning after the connection recovers.</summary>
            public static void HideLowConnection()
                => MiSideMultiplayer.API.Notifications.NotificationManager.ClearPersistent(
                    "qblib.low-connection");

            /// <summary>Shows a notice when another participant leaves.</summary>
            public static bool ShowPlayerLeft(string playerName)
            {
                string name = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
                return Show(name + " left.", 4f, "qblib.player-left." + name, 1f);
            }

            /// <summary>Registers a periodic notification provider. Dispose the
            /// returned handle to stop future notices.</summary>
            public static IDisposable RegisterPeriodic(
                Func<string> textFactory, float intervalSeconds,
                string deduplicationKey = null, float durationSeconds = 5f,
                float cooldownSeconds = 0f, bool showImmediately = false)
                => MiSideMultiplayer.API.Notifications.NotificationManager.RegisterPeriodic(
                    textFactory, intervalSeconds, deduplicationKey,
                    durationSeconds, cooldownSeconds, showImmediately);
        }

        /// <summary>Harmony patch helpers for QBLib consumers.</summary>
        public static class Patches
        {
            public static Harmony Create(string ownerId)
                => MiSideMultiplayer.API.Patches.QBLibPatches.Create(ownerId);

            public static Harmony ApplyAll(string ownerId, Assembly patchAssembly)
                => MiSideMultiplayer.API.Patches.QBLibPatches.ApplyAll(ownerId, patchAssembly);

            public static Harmony ApplyAll(string ownerId, Type patchType)
                => MiSideMultiplayer.API.Patches.QBLibPatches.ApplyAll(ownerId, patchType);

            public static bool Remove(string ownerId)
                => MiSideMultiplayer.API.Patches.QBLibPatches.Remove(ownerId);
        }

        // ── IL2CPP field access ────────────────────────────────────────────
        //
        // HarmonyX's AccessTools.Field cannot see private IL2CPP fields on
        // Il2CppInterop proxy types. This nested class resolves the native
        // FieldInfo* by name, caches its byte offset, and reads/writes by
        // raw IntPtr arithmetic.
        //
        // Use:  QBLib.Fields.ReadRef<Transform>(instance, "lookObject")
        //       QBLib.Fields.WriteRef(instance, "lookObject", someTransform)
        public static class Fields
        {
            private sealed class FieldHandle
            {
                public IntPtr Klass;
                public IntPtr Field;
                public int    Offset;
            }

            private static readonly Dictionary<string, FieldHandle> _cache =
                new Dictionary<string, FieldHandle>();
            private static readonly object _lock = new object();

            /// <summary>Resolve a field handle by instance. Caches per (class, name).</summary>
            private static FieldHandle Resolve(Il2CppSystem.Object instance, string fieldName)
            {
                if (instance == null || string.IsNullOrEmpty(fieldName)) return null;

                IntPtr klassPtr;
                try { klassPtr = IL2CPP.il2cpp_object_get_class(instance.Pointer); }
                catch { return null; }
                if (klassPtr == IntPtr.Zero) return null;

                string key = klassPtr.ToString() + "::" + fieldName;

                lock (_lock)
                {
                    FieldHandle cached;
                    if (_cache.TryGetValue(key, out cached)) return cached;
                }

                FieldHandle handle = null;
                try
                {
                    IntPtr fieldPtr = IL2CPP.il2cpp_class_get_field_from_name(klassPtr, fieldName);
                    if (fieldPtr != IntPtr.Zero)
                    {
                        int offset = (int)IL2CPP.il2cpp_field_get_offset(fieldPtr);
                        handle = new FieldHandle { Klass = klassPtr, Field = fieldPtr, Offset = offset };
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("QBLib.Fields: resolve failed for " +
                                          fieldName + ": " + ex.Message);
                }

                lock (_lock) { _cache[key] = handle; }
                return handle;
            }

            /// <summary>True if the given instance's class has this field.</summary>
            public static bool Exists(Il2CppSystem.Object instance, string fieldName)
            {
                return Resolve(instance, fieldName) != null;
            }

            /// <summary>Read a reference-typed field (Transform, Component, string, etc.).</summary>
            public static T ReadRef<T>(Il2CppSystem.Object instance, string fieldName)
                where T : Il2CppSystem.Object
            {
                if (instance == null) return null;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return null;

                try
                {
                    IntPtr objPtr = instance.Pointer;
                    if (objPtr == IntPtr.Zero) return null;
                    IntPtr slot = IntPtr.Add(objPtr, h.Offset);
                    IntPtr stored = System.Runtime.InteropServices.Marshal.ReadIntPtr(slot);
                    if (stored == IntPtr.Zero) return null;
                    return (T)Activator.CreateInstance(typeof(T), stored);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("QBLib.Fields.ReadRef '" + fieldName +
                                          "' failed: " + ex.Message);
                    return null;
                }
            }

            /// <summary>Write a reference-typed field.</summary>
            public static bool WriteRef(Il2CppSystem.Object instance, string fieldName,
                                        Il2CppSystem.Object value)
            {
                if (instance == null) return false;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return false;

                try
                {
                    IntPtr objPtr = instance.Pointer;
                    if (objPtr == IntPtr.Zero) return false;
                    IntPtr slot = IntPtr.Add(objPtr, h.Offset);
                    IntPtr valuePtr = value != null ? value.Pointer : IntPtr.Zero;
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(slot, valuePtr);
                    return true;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("QBLib.Fields.WriteRef '" + fieldName +
                                          "' failed: " + ex.Message);
                    return false;
                }
            }

            /// <summary>Read a bool field.</summary>
            public static bool ReadBool(Il2CppSystem.Object instance, string fieldName,
                                        bool fallback = false)
            {
                if (instance == null) return fallback;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return fallback;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    return System.Runtime.InteropServices.Marshal.ReadByte(slot) != 0;
                }
                catch { return fallback; }
            }

            /// <summary>Write a bool field.</summary>
            public static bool WriteBool(Il2CppSystem.Object instance, string fieldName, bool value)
            {
                if (instance == null) return false;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return false;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    System.Runtime.InteropServices.Marshal.WriteByte(slot, (byte)(value ? 1 : 0));
                    return true;
                }
                catch { return false; }
            }

            /// <summary>Read a float field.</summary>
            public static float ReadFloat(Il2CppSystem.Object instance, string fieldName,
                                          float fallback = 0f)
            {
                if (instance == null) return fallback;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return fallback;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    byte[] buf = new byte[4];
                    System.Runtime.InteropServices.Marshal.Copy(slot, buf, 0, 4);
                    return BitConverter.ToSingle(buf, 0);
                }
                catch { return fallback; }
            }

            /// <summary>Write a float field.</summary>
            public static bool WriteFloat(Il2CppSystem.Object instance, string fieldName, float value)
            {
                if (instance == null) return false;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return false;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    byte[] buf = BitConverter.GetBytes(value);
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, slot, 4);
                    return true;
                }
                catch { return false; }
            }

            /// <summary>Read a Vector3 field.</summary>
            public static Vector3 ReadVector3(Il2CppSystem.Object instance, string fieldName,
                                              Vector3 fallback = default(Vector3))
            {
                if (instance == null) return fallback;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return fallback;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    byte[] buf = new byte[12];
                    System.Runtime.InteropServices.Marshal.Copy(slot, buf, 0, 12);
                    return new Vector3(
                        BitConverter.ToSingle(buf, 0),
                        BitConverter.ToSingle(buf, 4),
                        BitConverter.ToSingle(buf, 8));
                }
                catch { return fallback; }
            }

            /// <summary>Write a Vector3 field.</summary>
            public static bool WriteVector3(Il2CppSystem.Object instance, string fieldName,
                                            Vector3 value)
            {
                if (instance == null) return false;
                FieldHandle h = Resolve(instance, fieldName);
                if (h == null) return false;
                try
                {
                    IntPtr slot = IntPtr.Add(instance.Pointer, h.Offset);
                    byte[] buf = new byte[12];
                    Buffer.BlockCopy(BitConverter.GetBytes(value.x), 0, buf, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(value.y), 0, buf, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(value.z), 0, buf, 8, 4);
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, slot, 12);
                    return true;
                }
                catch { return false; }
            }
        }

        // ── Credits ────────────────────────────────────────────────────────
        public static class Credits
        {
            public static GameMenu BuildMenu(
                string menuName,
                string menuTitle,
                GameMenu backTarget,
                System.Collections.Generic.List<MiSideMultiplayer.API.Credits.CreditEntry> entries,
                MiSideMultiplayer.API.Credits.CreditsStyle style = null)
            {
                return MiSideMultiplayer.API.Credits.QBLibCredits.BuildMenu(
                    menuName, menuTitle, backTarget, entries, style);
            }

            public static UnityEngine.RectTransform BuildSection(
                UnityEngine.RectTransform parent,
                System.Collections.Generic.List<MiSideMultiplayer.API.Credits.CreditEntry> entries,
                MiSideMultiplayer.API.Credits.CreditsStyle style = null)
            {
                return MiSideMultiplayer.API.Credits.QBLibCredits.BuildSection(parent, entries, style);
            }
        }
    }
}
