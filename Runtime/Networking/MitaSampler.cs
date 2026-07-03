using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Finds World/Mita in the active scene, samples her transform and animator
    /// state, and broadcasts it via RpcDispatcher at a fixed rate.
    ///
    /// Authority: every connected player broadcasts Mita state. MitaController
    /// applies incoming state only from the player with the lexicographically
    /// smallest ID — so there's one consistent authority without explicit
    /// host election.
    /// </summary>
    public sealed class MitaSampler
    {
        // ── Config ─────────────────────────────────────────────────────────────
        private RpcDispatcher rpcDispatcher;
        private string localPlayerId;
        private float sendRate = 10f;       // Hz, lower than player rate

        // ── Runtime ────────────────────────────────────────────────────────────
        private Transform mitaRoot;
        private Animator  mitaAnimator;
        private float     nextSendTime;
        private float     nextFindTime;
        private string    lastSceneName;
        private int       tick;
        private const float FindInterval = 4f;

        // Known Mita sub-paths to try (World-relative)
        private static readonly string[] MitaSearchPaths =
        {
            "Mita",                // World/Mita  (Image 2 confirms this)
            "General/Mita",
            "Mita/MitaPerson Mita",
            "MitaPerson Mita",
        };

        // ── Configure ──────────────────────────────────────────────────────────
        public void Configure(RpcDispatcher dispatcher, string playerId, float rate)
        {
            rpcDispatcher = dispatcher;
            localPlayerId = playerId;
            sendRate      = Mathf.Max(1f, rate);
        }

        // ── Tick ───────────────────────────────────────────────────────────────
        public void Tick()
        {
            if (rpcDispatcher == null)
                return;

            // Re-search on scene change
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName  = sceneName;
                mitaRoot       = null;
                mitaAnimator   = null;
                nextFindTime   = 0f;
                DiagnosticLog.Info("MitaSampler: scene changed to '" + sceneName + "', will re-search for Mita.");
            }

            // Periodic search
            if (mitaRoot == null && Time.unscaledTime >= nextFindTime)
            {
                nextFindTime = Time.unscaledTime + FindInterval;
                TryFindMita();
            }

            if (mitaRoot == null)
                return;

            // Send at configured rate
            if (Time.unscaledTime < nextSendTime)
                return;

            nextSendTime = Time.unscaledTime + 1f / sendRate;

            try
            {
                MitaState state = SampleMita();
                rpcDispatcher.SendMitaState(state);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSampler.Tick failed: " + ex.Message);
            }
        }

        // ── Find Mita ──────────────────────────────────────────────────────────
        private void TryFindMita()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            GameObject[] roots = scene.GetRootGameObjects();

            // Primary: under the World root
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

            // Fallback: GameObject.Find (active only)
            GameObject mitaGO = GameObject.Find("Mita");
            if (mitaGO != null)
            {
                BindMita(mitaGO.transform);
                return;
            }

            DiagnosticLog.Warning(
                "MitaSampler: Mita not found in scene '" + lastSceneName +
                "'. Will retry in " + FindInterval + "s.");
        }

        private void BindMita(Transform mita)
        {
            mitaRoot     = mita;
            mitaAnimator = mita.GetComponentInChildren<Animator>(true);
            DiagnosticLog.Info(
                "Mita bound at '" + LocalPlayerLocator.GetPath(mita) + "'" +
                (mitaAnimator != null ? " (animator found)" : " (no animator)"));
        }

        // ── Sample ─────────────────────────────────────────────────────────────
        private MitaState SampleMita()
        {
            MitaState state  = new MitaState();
            state.senderId   = localPlayerId;
            state.sceneName  = lastSceneName;
            state.position   = NetVector3.FromUnity(mitaRoot.position);
            state.rotation   = NetQuaternion.FromUnity(mitaRoot.rotation);
            state.tick       = tick++;

            if (mitaAnimator != null)
            {
                try
                {
                    // animator.parameters returns Il2CppReferenceArray<AnimatorControllerParameter>
                    // which TypeLoadExceptions because AnimatorControllerParameter is a struct
                    // and violates the Il2CppReferenceArray<T> reference-type constraint.
                    // Use parameterCount + GetParameter(int) instead — confirmed present
                    // in the IL2CPP dump (not stripped), unlike the .parameters property.
                    List<AnimatorFloatParam> floats = new List<AnimatorFloatParam>();
                    List<AnimatorBoolParam>  bools  = new List<AnimatorBoolParam>();
                    int paramCount = mitaAnimator.parameterCount;

                    for (int i = 0; i < paramCount; i++)
                    {
                        AnimatorControllerParameter p = mitaAnimator.GetParameter(i);
                        if (p == null) continue;
                        int hash = Animator.StringToHash(p.name);
                        if (p.type == AnimatorControllerParameterType.Float)
                        {
                            AnimatorFloatParam fp = new AnimatorFloatParam();
                            fp.name = p.name; fp.value = mitaAnimator.GetFloat(hash);
                            floats.Add(fp);
                        }
                        else if (p.type == AnimatorControllerParameterType.Bool)
                        {
                            AnimatorBoolParam bp = new AnimatorBoolParam();
                            bp.name = p.name; bp.value = mitaAnimator.GetBool(hash);
                            bools.Add(bp);
                        }
                    }

                    state.floatParameters = floats.ToArray();
                    state.boolParameters  = bools.ToArray();

                    AnimatorStateInfo info = mitaAnimator.GetCurrentAnimatorStateInfo(0);
                    AnimationClip[] clips;
                    if (mitaAnimator.runtimeAnimatorController != null)
                        clips = mitaAnimator.runtimeAnimatorController.animationClips;
                    else
                        clips = new AnimationClip[0];
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (clips[i] != null && info.IsName(clips[i].name))
                        {
                            state.currentAnimation = clips[i].name;
                            break;
                        }
                    }
                }
                catch (Exception) { }
            }

            return state;
        }
    }
}
