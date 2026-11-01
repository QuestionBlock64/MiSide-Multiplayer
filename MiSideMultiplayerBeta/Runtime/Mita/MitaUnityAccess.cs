using MiSideMultiplayer.API;
using UnityEngine;
using UnityEngine.AI;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Resolves the game-owned Mita navigation agent through MitaPerson's
    /// native field, with hierarchy lookup as a fallback.
    /// </summary>
    internal static class MitaUnityAccess
    {
        internal static NavMeshAgent ResolveAgent(Component mitaPerson, GameObject root)
        {
            if (mitaPerson != null)
            {
                try
                {
                    NavMeshAgent agent = QBLib.Fields.ReadRef<NavMeshAgent>(mitaPerson, "nma");
                    if (agent != null) return agent;
                }
                catch { }
            }

            if (root == null) return null;
            NavMeshAgent fallback = root.GetComponent<NavMeshAgent>();
            if (fallback != null) return fallback;
            return root.GetComponentInChildren<NavMeshAgent>();
        }
    }
}
