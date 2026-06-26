using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetController
    {
        // ── Smoothing constants ──────────────────────────────────────────────────
        private float positionSmoothTime = 0.075f;
        private float rotationLerpSpeed  = 18f;
        private float teleportDistance   = 7f;

        // ── Identity / hierarchy ─────────────────────────────────────────────────
        private string    playerId;
        private GameObject gameObject;
        private Transform  transform;
        private Transform  visualRoot;
        private Animator   animator;

        // ── Debug orb (always created) ───────────────────────────────────────────
        private Transform  debugOrbRoot;

        // ── Fallback marker (capsule, created only when visual clone is empty) ───
        private Transform  devMarkerRoot;
        private bool       showFallbackMarker;

        // ── Snapshot state ───────────────────────────────────────────────────────
        private Vector3           targetPosition;
        private Quaternion        targetRotation  = Quaternion.identity;
        private Vector3           smoothVelocity;
        private RemotePlayerState latestState;
        private bool              hasSnapshot;
        private string            lastAction;

        // ── Coordinate log throttle ──────────────────────────────────────────────
        private float nextCoordLogTime;
        private const float CoordLogInterval = 3f;   // log P2 position every 3 s

        // ── Animator hashes ──────────────────────────────────────────────────────
        private static readonly int LowerSpeedHash    = Animator.StringToHash("speed");
        private static readonly int UpperSpeedHash    = Animator.StringToHash("Speed");
        private static readonly int LowerGroundedHash = Animator.StringToHash("isGrounded");
        private static readonly int UpperGroundedHash = Animator.StringToHash("IsGrounded");

        public GameObject GameObject { get { return gameObject; } }

        // ── Construction ─────────────────────────────────────────────────────────
        public PuppetController(GameObject root)
        {
            gameObject = root;
            transform  = root.transform;
        }

        // ── Bind ─────────────────────────────────────────────────────────────────
        public void Bind(string remotePlayerId, Transform clonedVisualRoot, bool enableFallbackMarker)
        {
            playerId           = remotePlayerId;
            visualRoot         = clonedVisualRoot;
            showFallbackMarker = enableFallbackMarker;
            animator           = visualRoot != null
                                 ? visualRoot.GetComponentInChildren<Animator>(true)
                                 : null;

            // Always create the debug orb so we always know where P2 is.
            EnsureDebugOrb();

            // Additionally create the capsule/sphere body only when
            // the visual clone has no renderers.
            if (showFallbackMarker)
                EnsureDevMarker();

            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.Rebind();
                animator.Update(0f);
            }
        }

        // ── Visibility ───────────────────────────────────────────────────────────
        public void SetVisible(bool isVisible)
        {
            if (visualRoot != null && visualRoot.gameObject.activeSelf != isVisible)
                visualRoot.gameObject.SetActive(isVisible);

            if (debugOrbRoot != null && debugOrbRoot.gameObject.activeSelf != isVisible)
                debugOrbRoot.gameObject.SetActive(isVisible);

            if (devMarkerRoot != null && devMarkerRoot.gameObject.activeSelf != isVisible)
                devMarkerRoot.gameObject.SetActive(isVisible);
        }

        // ── Snapshot ─────────────────────────────────────────────────────────────
        public void ApplySnapshot(RemotePlayerState state)
        {
            if (state == null)
                return;

            latestState    = state;
            targetPosition = state.position.ToUnity();
            targetRotation = NormalizeRotation(state.rotation.ToUnity());

            if (!hasSnapshot)
            {
                transform.SetPositionAndRotation(targetPosition, targetRotation);
                smoothVelocity = Vector3.zero;
                hasSnapshot    = true;
            }
        }

        // ── Per-frame tick ───────────────────────────────────────────────────────
        public void Tick()
        {
            if (!hasSnapshot)
                return;

            // ── Position smoothing ───────────────────────────────────────────────
            float distance = Vector3.Distance(transform.position, targetPosition);
            if (distance > teleportDistance)
            {
                transform.position = targetPosition;
                smoothVelocity     = Vector3.zero;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position,
                    targetPosition,
                    ref smoothVelocity,
                    positionSmoothTime);
            }

            // ── Rotation smoothing ───────────────────────────────────────────────
            float rotT = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotT);

            UpdateAnimator();
            TickDevMarker();

            // ── Periodic BepInEx coordinate log for Player 2 ────────────────────
            if (Time.unscaledTime >= nextCoordLogTime)
            {
                nextCoordLogTime = Time.unscaledTime + CoordLogInterval;
                LogPlayer2Coordinates();
            }
        }

        // ── Debug orb (always on) ────────────────────────────────────────────────
        private void EnsureDebugOrb()
        {
            if (debugOrbRoot != null)
                return;

            GameObject orbRoot = new GameObject("DEV_Player2Orb_" + playerId);
            orbRoot.transform.SetParent(transform, false);
            orbRoot.transform.localPosition = new Vector3(0f, 2.4f, 0f);
            orbRoot.transform.localRotation = Quaternion.identity;
            orbRoot.transform.localScale    = Vector3.one;
            debugOrbRoot = orbRoot.transform;

            // Outer orb sphere — cyan/blue
            CreateOrbSphere(orbRoot.transform, "Player2_OuterOrb",
                new Color(0f, 0.85f, 1f, 0.92f), new Vector3(0.32f, 0.32f, 0.32f));

            // Inner core — bright white-blue ping
            CreateOrbSphere(orbRoot.transform, "Player2_InnerCore",
                new Color(0.7f, 0.95f, 1f, 1f), new Vector3(0.14f, 0.14f, 0.14f));

            DiagnosticLog.Info(
                "Player2 debug orb created for remote player '" + playerId + "'. " +
                "It is always visible above their puppet.");
        }

        private static void CreateOrbSphere(Transform parent, string objName, Color color, Vector3 scale)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = objName;
            sphere.transform.SetParent(parent, false);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localRotation = Quaternion.identity;
            sphere.transform.localScale    = scale;
            sphere.layer = 0;

            Collider col = sphere.GetComponent<Collider>();
            if (col != null)
                UnityEngine.Object.Destroy(col);

            Renderer rend = sphere.GetComponent<Renderer>();
            if (rend != null)
            {
                Material mat = CreateDevMaterial(color);
                if (mat != null)
                    rend.sharedMaterial = mat;
                rend.enabled = true;
            }
        }

        // ── Coordinate logger ─────────────────────────────────────────────────────
        private void LogPlayer2Coordinates()
        {
            Vector3 pos = transform.position;
            DiagnosticLog.Info(
                "Player2 [" + playerId + "]" +
                "  x=" + pos.x.ToString("F3") +
                "  y=" + pos.y.ToString("F3") +
                "  z=" + pos.z.ToString("F3") +
                (latestState != null
                    ? "  scene=" + latestState.sceneName +
                      "  speed=" + latestState.speed.ToString("F2")
                    : ""));
        }

        // ── Fallback capsule marker (only when no visual clone renderers) ─────────
        private void EnsureDevMarker()
        {
            if (devMarkerRoot != null)
                return;

            GameObject markerRoot = new GameObject("DEV_RemotePlayerMarker_" + playerId);
            markerRoot.transform.SetParent(transform, false);
            markerRoot.transform.localPosition =
                new Vector3(GetStableHorizontalOffset(playerId), 1.85f, 0f);
            markerRoot.transform.localRotation = Quaternion.identity;
            markerRoot.transform.localScale    = Vector3.one;
            devMarkerRoot = markerRoot.transform;

            CreateDevBody(devMarkerRoot, "World");

            DiagnosticLog.Info(
                "Fallback world marker created for '" + playerId +
                "' because the puppet visual clone had no renderers.");
        }

        private void CreateDevBody(Transform parent, string prefix)
        {
            // Capsule (body)
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = prefix + "_VisibleProxyCapsule";
            capsule.transform.SetParent(parent, false);
            capsule.transform.localPosition = Vector3.zero;
            capsule.transform.localRotation = Quaternion.identity;
            capsule.transform.localScale    = new Vector3(0.35f, 0.7f, 0.35f);
            capsule.layer = 0;

            Collider col = capsule.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            Renderer rend = capsule.GetComponent<Renderer>();
            if (rend != null)
            {
                Material mat = CreateDevMaterial(new Color(0f, 1f, 0.35f, 0.9f));
                if (mat != null) rend.sharedMaterial = mat;
                rend.enabled = true;
            }

            // Sphere (head)
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = prefix + "_VisibleProxyHead";
            head.transform.SetParent(parent, false);
            head.transform.localPosition = new Vector3(0f, 0.82f, 0f);
            head.transform.localRotation = Quaternion.identity;
            head.transform.localScale    = new Vector3(0.42f, 0.42f, 0.42f);
            head.layer = 0;

            Collider headCol = head.GetComponent<Collider>();
            if (headCol != null) UnityEngine.Object.Destroy(headCol);

            Renderer headRend = head.GetComponent<Renderer>();
            if (headRend != null)
            {
                Material mat = CreateDevMaterial(new Color(1f, 0.1f, 0.85f, 0.95f));
                if (mat != null) headRend.sharedMaterial = mat;
                headRend.enabled = true;
            }
        }

        private static Material CreateDevMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            Material material = new Material(shader);
            material.color = color;
            return material;
        }

        private void TickDevMarker()
        {
            if (!showFallbackMarker || devMarkerRoot == null)
                return;

            devMarkerRoot.localPosition =
                new Vector3(GetStableHorizontalOffset(playerId), 1.85f, 0f);
        }

        private static float GetStableHorizontalOffset(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0.65f;

            int hash = 0;
            for (int i = 0; i < value.Length; i++)
                hash = (hash * 31) + value[i];

            int bucket = Math.Abs(hash) % 5;
            return -0.8f + bucket * 0.4f;
        }

        // ── Animator helpers ─────────────────────────────────────────────────────
        private void UpdateAnimator()
        {
            if (animator == null || latestState == null)
                return;

            SetFloatIfExists(LowerSpeedHash, latestState.speed);
            SetFloatIfExists(UpperSpeedHash, latestState.speed);
            SetBoolIfExists(LowerGroundedHash, latestState.isGrounded);
            SetBoolIfExists(UpperGroundedHash, latestState.isGrounded);

            ApplySyncedAnimatorParams(latestState);
            ApplyLayerWeights(latestState.blendWeights);
            ApplyAction(latestState.action);
        }

        private void ApplySyncedAnimatorParams(RemotePlayerState state)
        {
            if (state.floatParameters != null)
            {
                for (int i = 0; i < state.floatParameters.Length; i++)
                {
                    int hash = Animator.StringToHash(state.floatParameters[i].name);
                    SetFloatIfExists(hash, state.floatParameters[i].value);
                }
            }

            if (state.boolParameters != null)
            {
                for (int i = 0; i < state.boolParameters.Length; i++)
                {
                    int hash = Animator.StringToHash(state.boolParameters[i].name);
                    SetBoolIfExists(hash, state.boolParameters[i].value);
                }
            }

            if (state.intParameters != null)
            {
                for (int i = 0; i < state.intParameters.Length; i++)
                {
                    int hash = Animator.StringToHash(state.intParameters[i].name);
                    SetIntIfExists(hash, state.intParameters[i].value);
                }
            }
        }

        private void ApplyLayerWeights(float[] layerWeights)
        {
            if (layerWeights == null)
                return;

            int count = Mathf.Min(layerWeights.Length, animator.layerCount);
            for (int i = 0; i < count; i++)
                animator.SetLayerWeight(i, layerWeights[i]);
        }

        private void ApplyAction(string action)
        {
            if (string.IsNullOrEmpty(action) || action == lastAction)
                return;

            int stateHash = Animator.StringToHash(action);
            if (animator.HasState(0, stateHash))
            {
                animator.CrossFadeInFixedTime(stateHash, 0.08f);
                lastAction = action;
            }
        }

        private void SetFloatIfExists(int hash, float value)
        {
            try   { animator.SetFloat(hash, value); }
            catch (Exception) { }
        }

        private void SetBoolIfExists(int hash, bool value)
        {
            try   { animator.SetBool(hash, value); }
            catch (Exception) { }
        }

        private void SetIntIfExists(int hash, int value)
        {
            try   { animator.SetInteger(hash, value); }
            catch (Exception) { }
        }

        private static Quaternion NormalizeRotation(Quaternion rotation)
        {
            if (rotation.x == 0f && rotation.y == 0f &&
                rotation.z == 0f && rotation.w == 0f)
            {
                return Quaternion.identity;
            }

            return rotation;
        }
    }
}
