using System;
using MiSideMultiplayer.API;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Safety net for players who fall out of the loaded world — through a
    /// floor hole, into a deactivated room's void, off a scripted teleport,
    /// or because a scene didn't finish loading. Detects sustained downward
    /// drift below a threshold and teleports the local player back to the
    /// nearest world's spawn point (or a fallback position derived from the
    /// world root).
    /// </summary>
    public static class PlayerFallGuard
    {
        private const float FallThresholdY = -15f;
        private const float FallDwellSeconds = 1.5f;
        private const float RescueCooldownSeconds = 3f;
        private const float MinSafeSpawnY = -5f;

        private static bool _initialized;

        private static float _belowThresholdSince = -1f;
        private static float _lastRescueTime = -100f;
        private static Vector3 _lastKnownGoodPosition;
        private static bool _hasLastKnownGood;
        private static string _lastSceneName;

        public static void Initialize()
        {
            _initialized = true;
            _belowThresholdSince = -1f;
            _lastRescueTime = -100f;
            _hasLastKnownGood = false;
            _lastSceneName = null;
        }

        public static void Tick()
        {
            if (!_initialized) return;

            try
            {
                string scene = SceneManager.GetActiveScene().name;
                if (scene != _lastSceneName)
                {
                    _lastSceneName = scene;
                    _belowThresholdSince = -1f;
                    _hasLastKnownGood = false;
                }

                Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
                if (localPlayer == null)
                {
                    _belowThresholdSince = -1f;
                    return;
                }

                Vector3 pos = localPlayer.position;
                float now = Time.unscaledTime;

                if (pos.y >= FallThresholdY)
                {
                    _belowThresholdSince = -1f;
                    _lastKnownGoodPosition = pos;
                    _hasLastKnownGood = true;
                    return;
                }

                if (_belowThresholdSince < 0f)
                {
                    _belowThresholdSince = now;
                    return;
                }

                if (now - _belowThresholdSince < FallDwellSeconds) return;
                if (now - _lastRescueTime < RescueCooldownSeconds) return;

                if (TryRescue(localPlayer, pos))
                {
                    _lastRescueTime = now;
                    _belowThresholdSince = -1f;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("PlayerFallGuard.Tick failed: " + ex.Message);
            }
        }

        private static bool TryRescue(Transform localPlayer, Vector3 currentPos)
        {
            Vector3 spawn;
            if (TryGetNearestWorldSpawn(currentPos, out spawn) &&
                spawn.y >= MinSafeSpawnY)
            {
                ApplyRespawn(localPlayer, spawn, "World.positionSpawn");
                return true;
            }

            GameObject worldRoot = GameObject.Find("World");
            if (worldRoot != null)
            {
                Vector3 p = worldRoot.transform.position + Vector3.up * 2f;
                if (p.y >= MinSafeSpawnY)
                {
                    ApplyRespawn(localPlayer, p, "World root + 2m");
                    return true;
                }
            }

            if (_hasLastKnownGood && _lastKnownGoodPosition.y >= MinSafeSpawnY)
            {
                ApplyRespawn(localPlayer, _lastKnownGoodPosition, "last known good");
                return true;
            }

            DiagnosticLog.Warning(
                "PlayerFallGuard: player is falling at y=" + currentPos.y.ToString("F1") +
                " but no safe respawn point could be resolved. Not teleporting.");
            return false;
        }

        private static void ApplyRespawn(Transform localPlayer, Vector3 target, string source)
        {
            try
            {
                localPlayer.position = target;

                Rigidbody rb = localPlayer.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Vector3 v = rb.velocity;
                    rb.velocity = new Vector3(v.x, 0f, v.z);
                }

                CharacterController cc = localPlayer.GetComponent<CharacterController>();
                if (cc != null)
                {
                    bool wasEnabled = cc.enabled;
                    if (wasEnabled) cc.enabled = false;
                    localPlayer.position = target;
                    if (wasEnabled) cc.enabled = true;
                }

                DiagnosticLog.Info(
                    "PlayerFallGuard: rescued local player from the void → " +
                    target.ToString("F2") + " (via " + source + ")");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("PlayerFallGuard.ApplyRespawn failed: " + ex.Message);
            }
        }

        private static bool TryGetNearestWorldSpawn(Vector3 fromPos, out Vector3 spawn)
        {
            spawn = Vector3.zero;
            bool found = false;
            float bestDist = float.MaxValue;

            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i].TryCast<GameObject>();
                    if (go == null) continue;
                    if (!go.scene.IsValid() || !go.scene.isLoaded) continue;
                    if (!go.activeInHierarchy) continue;

                    Component w = go.GetComponent("World");
                    if (w == null) continue;

                    Vector3 sp = TryReadPositionSpawn(w);
                    if (sp == Vector3.zero) continue;

                    float d = Vector3.Distance(fromPos, sp);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        spawn = sp;
                        found = true;
                    }
                }
            }
            catch { }

            return found;
        }

        private static Vector3 TryReadPositionSpawn(Component world)
        {
            try
            {
                return QBLib.Fields.ReadVector3(world, "positionSpawn", Vector3.zero);
            }
            catch
            {
                return Vector3.zero;
            }
        }
    }
}