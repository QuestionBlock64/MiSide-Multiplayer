using UnityEngine.SceneManagement;

namespace MiSideMultiplayer.API.Patches
{
    public static class SceneLoadedEvent
    {
        private static bool _registered;

        public static void RegisterEvent()
        {
            if (_registered) return;
            _registered = true;

            SceneManager.sceneLoaded += (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "SceneMenu")
            {
                MenuManager.Initialize();
            }
        }
    }
}