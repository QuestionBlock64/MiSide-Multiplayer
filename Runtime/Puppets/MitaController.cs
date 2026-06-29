using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Receives MitaState broadcasts from the authority player and smoothly
    /// applies them to the local World/Mita object.
    ///
    /// Authority rule: only apply state when the sender's ID is
    /// lexicographically smaller than the local player's ID. This creates a
    /// consistent, deterministic host without explicit election.
    /// </summary>
    public sealed class MitaController
    {
        // ── Config ──────────────────────────────────────────────────────────────
        private string localPlayerId;

        // ── Mita reference ──────────────────────────────────────────────────────
        private Transform mitaRoot;
        private Animator  mitaAnimator;
        private float     nextFindTime;
        private string    lastSceneName;
        private const float FindInterval = 4f;

        // ── Interpolation ───────────────────────────────────────────────────────
        private Vector3    targetPosition;
        private Quaternion targetRotation  = Quaternion.identity;
        private Vector3    smoothVelocity;
        private bool       hasState;
        private const float SmoothTime      = 0.12f;
        private const float RotationSpeed   = 12f;
        private const float TeleportDist    = 6f;

        // ── Configure ───────────────────────────────────────────────────────────
        public void Configure(string playerId)
        {
            localPlayerId = playerId;
        }

        // ── Apply incoming Mita state ────────────────────────────────────────────
        public void OnRemoteMitaStateReceived(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId))
                return;

            // Ignore our own echo
            if (state.senderId == localPlayerId)
                return;

            // Authority check: only follow the player with the smallest ID.
            // If our ID is smaller, WE are the authority — ignore incoming state.
            if (!string.IsNullOrEmpty(localPlayerId) &&
                string.Compare(localPlayerId, state.senderId, StringComparison.Ordinal) < 0)
            {
                return; // We are the authority; our local Mita AI runs normally.
            }

            // Scene check
            if (!string.IsNullOrEmpty(state.sceneName) &&
                state.sceneName != SceneManager.GetActiveScene().name)
            {
                return; // Different scene; do nothing.
            }

            // Ensure we have a Mita reference
            if (mitaRoot == null)
                TryFindMita();

            if (mitaRoot == null)
            {
                DiagnosticLog.Warning(
                    "MitaController: received Mita state from '" + state.senderId +
                    "' but World/Mita not found locally.");
                return;
            }

            // Store target
            targetPosition = state.position.ToUnity();
            targetRotation = state.rotation.ToUnity();

            if (!hasState)
            {
                mitaRoot.SetPositionAndRotation(targetPosition, targetRotation);
                smoothVelocity = Vector3.zero;
                hasState = true;

                DiagnosticLog.Info(
                    "MitaController: authority is '" + state.senderId + "'. " +
                    "Applying Mita state from remote.");
            }

            // Apply animator parameters immediately
            if (mitaAnimator != null)
                ApplyAnimatorParams(state);
        }

        // ── Per-frame interpolation ──────────────────────────────────────────────
        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                mitaRoot      = null;
                mitaAnimator  = null;
                hasState      = false;
                nextFindTime  = 0f;
            }

            if (mitaRoot == null || !hasState)
                return;

            // Smooth position
            float dist = Vector3.Distance(mitaRoot.position, targetPosition);
            if (dist > TeleportDist)
            {
                mitaRoot.position = targetPosition;
                smoothVelocity    = Vector3.zero;
            }
            else
            {
                mitaRoot.position = Vector3.SmoothDamp(
                    mitaRoot.position, targetPosition, ref smoothVelocity, SmoothTime);
            }

            // Smooth rotation
            float rotT = 1f - Mathf.Exp(-RotationSpeed * Time.deltaTime);
            mitaRoot.rotation = Quaternion.Slerp(mitaRoot.rotation, targetRotation, rotT);
        }

        // ── Mita lookup ─────────────────────────────────────────────────────────
        private void TryFindMita()
        {
            if (Time.unscaledTime < nextFindTime)
                return;

            nextFindTime = Time.unscaledTime + FindInterval;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            lastSceneName = scene.name;
            GameObject[] roots = scene.GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                if (!string.Equals(roots[i].name, "World", StringComparison.OrdinalIgnoreCase))
                    continue;

                Transform mita = roots[i].transform.Find("Mita");
                if (mita != null)
                {
                    mitaRoot     = mita;
                    mitaAnimator = mita.GetComponentInChildren<Animator>(true);
                    DiagnosticLog.Info(
                        "MitaController: found World/Mita at '" +
                        LocalPlayerLocator.GetPath(mita) + "'.");
                    return;
                }
            }

            // Fallback
            GameObject fallback = GameObject.Find("Mita");
            if (fallback != null)
            {
                mitaRoot     = fallback.transform;
                mitaAnimator = fallback.GetComponentInChildren<Animator>(true);
                DiagnosticLog.Info(
                    "MitaController: found Mita (fallback) at '" +
                    LocalPlayerLocator.GetPath(mitaRoot) + "'.");
            }
        }

        // ── Apply animator params ────────────────────────────────────────────────
        private void ApplyAnimatorParams(MitaState state)
        {
            try
            {
                if (state.floatParameters != null)
                {
                    for (int i = 0; i < state.floatParameters.Length; i++)
                    {
                        int hash = Animator.StringToHash(state.floatParameters[i].name);
                        mitaAnimator.SetFloat(hash, state.floatParameters[i].value);
                    }
                }

                if (state.boolParameters != null)
                {
                    for (int i = 0; i < state.boolParameters.Length; i++)
                    {
                        int hash = Animator.StringToHash(state.boolParameters[i].name);
                        mitaAnimator.SetBool(hash, state.boolParameters[i].value);
                    }
                }

                if (!string.IsNullOrEmpty(state.currentAnimation))
                {
                    int hash = Animator.StringToHash(state.currentAnimation);
                    if (mitaAnimator.HasState(0, hash))
                        mitaAnimator.CrossFadeInFixedTime(hash, 0.1f);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaController.ApplyAnimatorParams failed: " + ex.Message);
            }
        }
    }
}
