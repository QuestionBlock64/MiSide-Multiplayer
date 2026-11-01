using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Creates remote puppets by cloning the local player's Player hierarchy,
    /// exactly like the MelonLoader HelperFunctions.CreateKiri().
    /// </summary>
    public sealed class PuppetFactory
    {
        private readonly Transform puppetParent;

        public PuppetFactory(Transform parent)
        {
            puppetParent = parent;
        }

        public void SetVisualRootCandidates(string[] candidates) { }

        public PuppetController Create(string playerId, string displayName,
                                       Transform localPlayerRoot,
                                       string    remoteCustomModelName = null)
        {
            if (localPlayerRoot == null)
            {
                DiagnosticLog.Error("Cannot create puppet for '" + playerId + "': localPlayerRoot is null.");
                return null;
            }

            Transform playerRoot = ResolvePlayerRoot(localPlayerRoot);
            if (playerRoot == null)
            {
                DiagnosticLog.Error("Cannot create puppet for '" + playerId + "': could not find Player root.");
                return null;
            }

            // ── Clone Player ─────────────────────────────────────────────────
            GameObject clone = UnityEngine.Object.Instantiate(playerRoot.gameObject);
            clone.name = "RemotePuppet_" + Sanitize(playerId);
            clone.transform.SetParent(puppetParent, false);

            // ── Enable ALL SkinnedMeshRenderers on the clone ─────────────────
            SkinnedMeshRenderer[] allRenderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                if (allRenderers[i] != null)
                {
                    allRenderers[i].enabled = true;
                    allRenderers[i].gameObject.layer = 0;
                    allRenderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    allRenderers[i].updateWhenOffscreen = true;
                }
            }

            // Also enable MeshRenderers
            MeshRenderer[] meshRenderers = clone.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                if (meshRenderers[i] != null)
                {
                    meshRenderers[i].enabled = true;
                    meshRenderers[i].gameObject.layer = 0;
                }
            }

            // ── Disable cameras ──────────────────────────────────────────────
            DisableCameraAtPath(clone, "HeadPlayer/MainCamera");
            DisableCameraAtPath(clone, "HeadPlayer/MainCamera/CameraPersons");
            DisableCameraAtPath(clone, "FixHead/MainCamera");
            DisableCameraAtPath(clone, "FixHead/MainCamera/CameraPersons");

            // ── Disable AudioListeners ───────────────────────────────────────
            DisableAudioListenerAtPath(clone, "HeadPlayer/MainCamera");
            DisableAudioListenerAtPath(clone, "FixHead/MainCamera");

            // ── Disable PlayerMove ───────────────────────────────────────────
            DisableComponentByName(clone, null, "PlayerMove");

            // ── Freeze Animator ──────────────────────────────────────────────
            Animator anim = clone.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.speed = 0f;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.applyRootMotion = false;
            }

            PuppetController controller = new PuppetController(clone);
            controller.Bind(playerId, displayName, clone.transform, false);

            int renderers = CountRenderers(clone.transform);
            DiagnosticLog.Info(
                "Puppet ready: '" + playerId + "' (" + displayName + ") " +
                renderers + " renderer(s) ALL enabled.");

            return controller;
        }

        private static Transform ResolvePlayerRoot(Transform t)
        {
            if (t == null) return null;
            if (t.name == "Player") return t;
            if (t.name == "Person") return t.parent != null && t.parent.name == "Player" ? t.parent : t;
            Transform player = t.Find("Player");
            if (player != null) return player;
            Transform current = t;
            while (current != null)
            {
                if (current.name == "Player") return current;
                current = current.parent;
            }
            return t;
        }

        private static void DisableCameraAtPath(GameObject obj, string path)
        {
            Transform t = obj.transform.Find(path);
            if (t != null) { Camera c = t.GetComponent<Camera>(); if (c != null) c.enabled = false; }
        }

        private static void DisableAudioListenerAtPath(GameObject obj, string path)
        {
            Transform t = obj.transform.Find(path);
            if (t != null) { AudioListener a = t.GetComponent<AudioListener>(); if (a != null) a.enabled = false; }
        }

        private static void DisableComponentByName(GameObject obj, string path, string typeName)
        {
            Component comp;
            if (!string.IsNullOrEmpty(path))
            {
                Transform t = obj.transform.Find(path);
                comp = t != null ? t.GetComponent(typeName) : null;
            }
            else
            {
                comp = obj.GetComponent(typeName);
            }
            MonoBehaviour mb = comp as MonoBehaviour;
            if (mb != null) mb.enabled = false;
        }

        private static int CountRenderers(Transform root)
        {
            if (root == null) return 0;
            SkinnedMeshRenderer[] s = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            MeshRenderer[] m = root.GetComponentsInChildren<MeshRenderer>(true);
            return (s != null ? s.Length : 0) + (m != null ? m.Length : 0);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unknown";
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                    chars[i] = '_';
            return new string(chars);
        }
    }
}