using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public static class MitaSceneCache
    {
        private static string _cachedScene = "";
        private static float _nextRefreshTime = 0f;
        private const float RefreshInterval = 5f;

        private static readonly List<Transform> _mitaTransforms = new List<Transform>();
        private static readonly List<MonoBehaviour> _allBehaviours = new List<MonoBehaviour>();

        public static void Invalidate()
        {
            _cachedScene = "";
            _nextRefreshTime = 0f;
            _mitaTransforms.Clear();
            _allBehaviours.Clear();
            MitaMultiPlayerGaze.ClearPathCache();
        }

        public static IList<Transform> MitaTransforms
        {
            get { RefreshIfStale(); return _mitaTransforms; }
        }

        public static IList<MonoBehaviour> AllBehaviours
        {
            get { RefreshIfStale(); return _allBehaviours; }
        }

        private static void RefreshIfStale()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _cachedScene ||
                Time.unscaledTime >= _nextRefreshTime ||
                _mitaTransforms.Count == 0)
            {
                Refresh(scene);
            }
        }

        private static void Refresh(string scene)
        {
            _cachedScene = scene;
            _nextRefreshTime = Time.unscaledTime + RefreshInterval;
            _mitaTransforms.Clear();
            _allBehaviours.Clear();

            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());

                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i].TryCast<GameObject>();
                    if (go == null) continue;
                    if (!go.scene.IsValid() || !go.scene.isLoaded) continue;

                    if (go.GetComponent("MitaPerson") != null && go.activeInHierarchy)
                        _mitaTransforms.Add(go.transform);
                }

                UnityEngine.Object[] mbs = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());

                for (int i = 0; i < mbs.Length; i++)
                {
                    MonoBehaviour mb = mbs[i].TryCast<MonoBehaviour>();
                    if (mb == null) continue;
                    if (!mb.gameObject.scene.IsValid() || !mb.gameObject.scene.isLoaded) continue;
                    _allBehaviours.Add(mb);
                }
            }
            catch { }
        }
    }
}