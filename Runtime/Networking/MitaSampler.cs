using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class MitaSampler
    {
        private RpcDispatcher rpcDispatcher;
        private string localPlayerId;
        private float sendRate = 1f / 60f;

        private Transform mitaRoot;
        private Transform mitaPerson;
        private float     nextSendTime;
        private float     nextFindTime;
        private string    lastSceneName;
        private const float FindInterval = 4f;
        private bool loggedMitaSearch;

        private static readonly string[] MitaSearchPaths =
        {
            "Mita",
            "General/Mita",
            "Mita/MitaPerson Mita",
            "MitaPerson Mita",
        };

        public void Configure(RpcDispatcher dispatcher, string playerId, float rate)
        {
            rpcDispatcher = dispatcher;
            localPlayerId = playerId;
            sendRate      = Mathf.Max(1f, rate);
        }

        public void Tick()
        {
            if (rpcDispatcher == null)
                return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                mitaRoot      = null;
                mitaPerson    = null;
                nextFindTime  = 0f;
                loggedMitaSearch = false;
            }

            if (mitaRoot == null && Time.unscaledTime >= nextFindTime)
            {
                nextFindTime = Time.unscaledTime + FindInterval;
                TryFindMita();
            }

            if (mitaRoot == null || mitaPerson == null)
                return;

            if (Time.unscaledTime < nextSendTime)
                return;
            nextSendTime = Time.unscaledTime + 1f / sendRate;

            try
            {
                MitaState state = new MitaState();
                state.senderId  = localPlayerId;
                state.sceneName = lastSceneName;
                state.position  = NetVector3.FromUnity(mitaRoot.position);
                state.rotation  = NetQuaternion.FromUnity(mitaRoot.rotation);
                state.Bones     = SampleBones(mitaPerson);
                rpcDispatcher.SendMitaState(state);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSampler.Tick failed: " + ex.Message);
            }
        }

        private void TryFindMita()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;

            if (!loggedMitaSearch)
            {
                loggedMitaSearch = true;
                // Dump all root objects to find Mita
                GameObject[] allRoots = scene.GetRootGameObjects();
                DiagnosticLog.Info("=== Mita search: " + allRoots.Length + " root objects in scene '" + scene.name + "' ===");
                for (int i = 0; i < allRoots.Length; i++)
                {
                    DiagnosticLog.Info("  Root[" + i + "]: " + allRoots[i].name + " (active=" + allRoots[i].activeSelf + ")");
                }

                // Also dump World children if World exists
                for (int i = 0; i < allRoots.Length; i++)
                {
                    if (allRoots[i].name == "World")
                    {
                        DiagnosticLog.Info("=== World children (" + allRoots[i].transform.childCount + ") ===");
                        for (int j = 0; j < allRoots[i].transform.childCount; j++)
                        {
                            Transform child = allRoots[i].transform.GetChild(j);
                            DiagnosticLog.Info("  World/" + child.name + " (active=" + child.gameObject.activeSelf + ")");
                            // Dump Mita's children if found
                            if (child.name.IndexOf("Mita", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                DumpHierarchy(child, "    ");
                            }
                        }
                    }
                }

                // Direct GameObject.Find
                GameObject directFind = GameObject.Find("Mita");
                DiagnosticLog.Info("  GameObject.Find(\"Mita\"): " + (directFind != null ? "FOUND (active=" + directFind.activeSelf + ")" : "NULL"));
                if (directFind != null)
                {
                    DumpHierarchy(directFind.transform, "    ");
                }
            }

            GameObject[] roots = scene.GetRootGameObjects();

            for (int r = 0; r < roots.Length; r++)
            {
                if (!string.Equals(roots[r].name, "World", StringComparison.OrdinalIgnoreCase))
                    continue;

                Transform worldRoot = roots[r].transform;
                for (int s = 0; s < MitaSearchPaths.Length; s++)
                {
                    Transform mita = worldRoot.Find(MitaSearchPaths[s]);
                    if (mita != null)
                    {
                        BindMita(mita);
                        return;
                    }
                }
            }

            GameObject mitaGO = GameObject.Find("Mita");
            if (mitaGO != null)
            {
                BindMita(mitaGO.transform);
                return;
            }
        }

        private void DumpHierarchy(Transform t, string indent)
        {
            DiagnosticLog.Info(indent + t.name + " (active=" + t.gameObject.activeSelf + ", children=" + t.childCount + ")");
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), indent + "  ");
        }

        private void BindMita(Transform mita)
        {
            mitaRoot   = mita;
            mitaPerson = FindPersonInChildren(mita);
            DiagnosticLog.Info(
                "MitaSampler: bound at '" + LocalPlayerLocator.GetPath(mita) + "'" +
                (mitaPerson != null ? " (Person found)" : " (Person NOT found - dumping hierarchy)"));
            if (mitaPerson == null)
            {
                DiagnosticLog.Info("=== Mita hierarchy dump ===");
                DumpHierarchy(mita, "  ");
            }
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

        private static Dictionary<string, BoneTransformData> SampleBones(Transform personRoot)
        {
            if (personRoot == null) return null;
            var result = new Dictionary<string, BoneTransformData>(BoneSyncData.Count);
            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                Transform bone = personRoot.Find(path);
                if (bone == null) continue;
                BoneTransformData data = new BoneTransformData();
                data.PosX = bone.localPosition.x;
                data.PosY = bone.localPosition.y;
                data.PosZ = bone.localPosition.z;
                data.RotX = bone.localRotation.x;
                data.RotY = bone.localRotation.y;
                data.RotZ = bone.localRotation.z;
                data.RotW = bone.localRotation.w;
                result[path] = data;
            }
            return result;
        }
    }
}