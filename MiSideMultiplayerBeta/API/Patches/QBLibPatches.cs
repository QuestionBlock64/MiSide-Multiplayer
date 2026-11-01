using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MiSideMultiplayer.API.Patches
{
    /// <summary>Harmony helpers for QBLib consumers, following the same
    /// PatchAll lifecycle used by SpeedrunMod.</summary>
    public static class QBLibPatches
    {
        private static readonly Dictionary<string, Harmony> Owners =
            new Dictionary<string, Harmony>(StringComparer.Ordinal);

        public static Harmony Create(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("A stable Harmony owner id is required.", nameof(ownerId));

            lock (Owners)
            {
                Harmony harmony;
                if (!Owners.TryGetValue(ownerId, out harmony))
                {
                    harmony = new Harmony(ownerId);
                    Owners.Add(ownerId, harmony);
                }
                return harmony;
            }
        }

        public static Harmony ApplyAll(string ownerId, Assembly patchAssembly)
        {
            if (patchAssembly == null) throw new ArgumentNullException(nameof(patchAssembly));
            Harmony harmony = Create(ownerId);
            harmony.PatchAll(patchAssembly);
            return harmony;
        }

        public static Harmony ApplyAll(string ownerId, Type patchType)
        {
            if (patchType == null) throw new ArgumentNullException(nameof(patchType));
            Harmony harmony = Create(ownerId);
            harmony.PatchAll(patchType);
            return harmony;
        }

        public static bool Remove(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return false;
            Harmony harmony;
            lock (Owners)
            {
                if (!Owners.TryGetValue(ownerId, out harmony)) return false;
                Owners.Remove(ownerId);
            }
            harmony.UnpatchSelf();
            return true;
        }
    }
}
