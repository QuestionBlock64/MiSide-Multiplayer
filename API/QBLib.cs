using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using MiSideMultiplayer.API.Events;
using MiSideMultiplayer.API.Factories;

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
    }
}