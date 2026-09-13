using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class MitaController
    {
        private sealed class Entry
        {
            public Component MitaPerson;
            public Transform MitaRoot;
            public Transform PersonRoot;
            public NavMeshAgent Nma;
            public Animator Animator;
            public MethodInfo MagnetToTargetMethod;
            public MethodInfo MagnetOffMethod;
            public FieldInfo MagnetTargetField;
            public GameObject MagnetProxy;
            public GameObject GO;
            public bool HasState;
            public bool WarnedMissing;
            public bool AiSuppressed;

            public struct Snapshot
            {
                public float HostTime;
                public Vector3 Position;
                public Quaternion Rotation;
            }
            public readonly List<Snapshot> Buffer = new List<Snapshot>();
        }

        private static MitaController _active;

        private string localPlayerId;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private string lastSceneName;

        private const float InterpolationDelay = 0.10f;
        private const float MaxBufferAge = 1.0f;
        private const int   MaxBufferSize = 32;

        public void Configure(string playerId)
        {
            localPlayerId = playerId;
            MitaHost.Configure(playerId, MitaPlayerOverride.ConfiguredHostId);
            _active = this;
        }

        public static void ForceRescan()
        {
            MitaController c = _active;
            if (c == null) return;
            c._entries.Clear();
            c.lastSceneName = SceneManager.GetActiveScene().name;
            DiagnosticLog.Info("MitaController: ForceRescan complete.");
        }

        public void OnRemoteMitaStateReceived(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId)) return;
            if (state.senderId == localPlayerId) return;

            if (!MitaHost.IsHost && state.senderId == MitaHost.HostPlayerId)
                ApplyRemoteState(state);
        }

        private void ApplyRemoteState(MitaState state)
        {
            if (string.IsNullOrEmpty(state.mitaPath))
            {
                DiagnosticLog.Warning("MitaController: state with no mitaPath, ignoring.");
                return;
            }

            if (state.events != null && state.events.Length > 0)
            {
                for (int i = 0; i < state.events.Length; i++)
                {
                    MitaEvent ev = state.events[i];
                    if (ev == null) continue;
                    if (ev.mitaPath == state.mitaPath)
                        MitaEventSync.ApplyEvent(ev);
                }
            }

            if (!string.IsNullOrEmpty(state.sceneName) &&
                state.sceneName != SceneManager.GetActiveScene().name)
                return;

            Entry e = GetOrCreateEntry(state.mitaPath);
            if (e == null) return;

            if (!e.MitaPerson.gameObject.activeInHierarchy)
                e.MitaPerson.gameObject.SetActive(true);

            SuppressLocalAI(e);

            if (!e.HasState)
            {
                e.MitaRoot.position = state.position.ToUnity();
                e.MitaRoot.rotation = state.rotation.ToUnity();
                e.HasState = true;
                DiagnosticLog.Info("MitaController: syncing '" + state.mitaPath +
                                   "' from host '" + state.senderId + "'.");
            }

            e.Buffer.Add(new Entry.Snapshot
            {
                HostTime = state.hostSendTime,
                Position = state.position.ToUnity(),
                Rotation = state.rotation.ToUnity()
            });

            float minHostTime = state.hostSendTime - MaxBufferAge;
            while (e.Buffer.Count > 0 && e.Buffer[0].HostTime < minHostTime)
                e.Buffer.RemoveAt(0);
            while (e.Buffer.Count > MaxBufferSize)
                e.Buffer.RemoveAt(0);

            if (e.Nma != null && e.Nma.enabled)
                e.Nma.enabled = false;

            MitaGazeState.SetRemote(state.mitaPath, state.gazeTargetPlayerId);

            if (e.MagnetToTargetMethod != null && e.MagnetOffMethod != null && e.MagnetTargetField != null)
            {
                try
                {
                    Transform currentMagnet = e.MagnetTargetField.GetValue(e.MitaPerson) as Transform;

                    if (state.magnetActive)
                    {
                        if (e.MagnetProxy == null)
                        {
                            e.MagnetProxy = new GameObject("MitaMagnetProxy_" +
                                state.mitaPath.GetHashCode().ToString("X8"));
                            e.MagnetProxy.hideFlags = HideFlags.HideAndDontSave;
                        }
                        e.MagnetProxy.transform.position = state.magnetTargetPos.ToUnity();
                        e.MagnetProxy.transform.rotation = state.magnetTargetRot.ToUnity();

                        if (currentMagnet == null || currentMagnet.position != e.MagnetProxy.transform.position)
                            e.MagnetToTargetMethod.Invoke(e.MitaPerson, new object[] { e.MagnetProxy.transform });
                    }
                    else
                    {
                        if (currentMagnet != null)
                            e.MagnetOffMethod.Invoke(e.MitaPerson, null);
                        if (e.MagnetProxy != null)
                        {
                            UnityEngine.Object.Destroy(e.MagnetProxy);
                            e.MagnetProxy = null;
                        }
                    }
                }
                catch { }
            }

            if (state.Bones != null && state.Bones.Count > 0 && e.PersonRoot != null)
                ApplyBones(e.PersonRoot, state.Bones);
        }

        private void SuppressLocalAI(Entry e)
        {
            if (e == null || e.MitaPerson == null) return;
            if (e.AiSuppressed) return;

            try
            {
                if (e.Nma != null)
                    e.Nma.enabled = false;

                if (e.Animator != null)
                    e.Animator.speed = 0f;

                e.AiSuppressed = true;
                DiagnosticLog.Info("MitaController: suppressed local AI on '" + e.GO.name + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaController.SuppressLocalAI failed: " + ex.Message);
            }
        }

        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                foreach (var kv in _entries)
                {
                    Entry e = kv.Value;
                    if (e != null && e.MagnetProxy != null)
                    {
                        UnityEngine.Object.Destroy(e.MagnetProxy);
                        e.MagnetProxy = null;
                    }
                }
                _entries.Clear();
            }

            if (MitaHost.IsHost) return;

            foreach (var kv in _entries)
            {
                Entry e = kv.Value;
                if (e == null || e.MitaPerson == null) continue;
                if (!e.HasState || e.Buffer.Count == 0) continue;

                float newestHostTime = e.Buffer[e.Buffer.Count - 1].HostTime;
                float renderTime = newestHostTime - InterpolationDelay;

                int olderIdx = -1;
                int newerIdx = -1;
                for (int i = 0; i < e.Buffer.Count - 1; i++)
                {
                    if (e.Buffer[i].HostTime <= renderTime &&
                        renderTime <= e.Buffer[i + 1].HostTime)
                    {
                        olderIdx = i;
                        newerIdx = i + 1;
                        break;
                    }
                }

                if (olderIdx >= 0)
                {
                    Entry.Snapshot a = e.Buffer[olderIdx];
                    Entry.Snapshot b = e.Buffer[newerIdx];
                    float span = b.HostTime - a.HostTime;
                    float t = span > 0.0001f ? (renderTime - a.HostTime) / span : 0f;

                    e.MitaRoot.position = Vector3.Lerp(a.Position, b.Position, t);
                    e.MitaRoot.rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                }
                else
                {
                    Entry.Snapshot newest = e.Buffer[e.Buffer.Count - 1];
                    e.MitaRoot.position = Vector3.Lerp(
                        e.MitaRoot.position, newest.Position, Time.deltaTime * 20f);
                    e.MitaRoot.rotation = Quaternion.Slerp(
                        e.MitaRoot.rotation, newest.Rotation, Time.deltaTime * 20f);
                }
            }
        }

        private Entry GetOrCreateEntry(string path)
        {
            Entry e;
            if (_entries.TryGetValue(path, out e) && e != null && e.MitaPerson != null)
                return e;

            GameObject go = FindByPath(path);
            if (go == null)
            {
                if (e == null || !e.WarnedMissing)
                {
                    DiagnosticLog.Warning("MitaController: could not resolve mitaPath '" + path + "' locally.");
                    if (e == null) e = new Entry();
                    e.WarnedMissing = true;
                    _entries[path] = e;
                }
                return null;
            }

            Component comp = go.GetComponent("MitaPerson");
            if (comp == null)
            {
                DiagnosticLog.Warning("MitaController: object at '" + path + "' has no MitaPerson.");
                return null;
            }

            Entry fresh = new Entry();
            fresh.GO = go;
            fresh.MitaPerson = comp;
            fresh.MitaRoot = go.transform;
            fresh.PersonRoot = FindPersonInChildren(go.transform);
            fresh.Nma = comp.GetComponent<NavMeshAgent>();
            fresh.Animator = comp.GetComponent<Animator>();
            if (fresh.Animator == null) fresh.Animator = go.GetComponentInChildren<Animator>();

            Type t = comp.GetType();
            fresh.MagnetTargetField = t.GetField("magnetTarget", BindingFlags.NonPublic | BindingFlags.Instance);
            fresh.MagnetToTargetMethod = t.GetMethod("MagnetToTarget");
            fresh.MagnetOffMethod = t.GetMethod("MagnetOff");

            _entries[path] = fresh;
            DiagnosticLog.Info("MitaController: bound to '" + go.name + "' at '" + path + "'.");
            return fresh;
        }

        private static Transform FindPersonInChildren(Transform root)
        {
            if (root == null) return null;
            if (root.name == "Person") return root;
            Transform person = root.Find("Person");
            if (person != null) return person;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform result = FindPersonInChildren(root.GetChild(i));
                if (result != null) return result;
            }
            return null;
        }

        private void ApplyBones(Transform personRoot, Dictionary<string, BoneTransformData> boneData)
        {
            if (personRoot == null || boneData == null) return;

            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                Transform bone = personRoot.Find(path);
                if (bone == null) continue;
                BoneTransformData data;
                if (!boneData.TryGetValue(path, out data)) continue;
                bone.localPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
                bone.localRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
            }
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r] == null || roots[r].name != parts[0]) continue;
                Transform cur = roots[r].transform;
                for (int i = 1; i < parts.Length && cur != null; i++)
                    cur = cur.Find(parts[i]);
                if (cur != null) return cur.gameObject;
            }
            return null;
        }
    }
}