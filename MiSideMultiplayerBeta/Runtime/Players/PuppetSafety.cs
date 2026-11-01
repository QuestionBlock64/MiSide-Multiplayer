using UnityEngine;

namespace MiSideMultiplayer
{
    public static class PuppetSafety
    {
        public static void ValidateVisualOnly(GameObject puppetRoot)
        {
            if (puppetRoot == null)
                return;

            WarnIfPresent<Camera>(puppetRoot);
            WarnIfPresent<AudioListener>(puppetRoot);
            WarnIfPresent<AudioSource>(puppetRoot);
            WarnIfPresent<Rigidbody>(puppetRoot);
            WarnIfPresent<Rigidbody2D>(puppetRoot);
            WarnIfPresent<Collider>(puppetRoot);
            WarnIfPresent<Collider2D>(puppetRoot);
            WarnIfPresent<CharacterController>(puppetRoot);
        }

        private static void WarnIfPresent<T>(GameObject root) where T : Component
        {
            T component = root.GetComponentInChildren<T>(true);
            if (component != null)
            {
                Debug.LogWarning(
                    "[MiSideMultiplayer] Puppet contains forbidden component " +
                    typeof(T).Name +
                    " at " +
                    GetPath(component.transform));
            }
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }
    }
}
