using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    public sealed class HopCleanup : MonoBehaviour
    {
        private bool _armed;

        public static void Attach(GameObject go)
        {
            if (go == null) return;
            var c = go.AddComponent<HopCleanup>();
            c._armed = true;
        }

        private void LateUpdate()
        {
            if (_armed)
            {
                _armed = false;
                Destroy(gameObject);
            }
        }
    }
}