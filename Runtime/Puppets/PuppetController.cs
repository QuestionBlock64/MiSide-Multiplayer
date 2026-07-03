using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetController
    {
        // ── Smoothing ──────────────────────────────────────────────────────────
        private const float PositionSmoothTime = 0.075f;
        private const float RotationLerpSpeed  = 18f;
        private const float HeadLerpSpeed      = 24f;
        private const float TeleportDistance   = 7f;

        // ── Identity ───────────────────────────────────────────────────────────
        private string     playerId;
        private string     displayName;
        private GameObject gameObjectRef;
        private Transform  transformRef;
        private Transform  visualRoot;

        // ── Animator ───────────────────────────────────────────────────────────
        // May live on visualRoot or on a sibling at the puppet-root level
        // (mirrors how PlayerMove.animPerson works in the original game).
        private Animator   animator;

        // ── HeadMirror (Person/HeadMirror confirmed by dump string literals) ──
        private Transform  headBone;
        private Quaternion targetHeadRotation = Quaternion.identity;
        private float      headPitchSmoothed;

        // ── Debug orb ──────────────────────────────────────────────────────────
        private Transform  debugOrbRoot;

        // ── Name tag ───────────────────────────────────────────────────────────
        private Transform  nameTagRoot;
        private TextMesh   nameTagMesh;

        // ── Fallback capsule body (when clone has 0 renderers) ─────────────────
        private Transform  devMarkerRoot;
        private bool       showFallbackMarker;

        // ── Snapshot ───────────────────────────────────────────────────────────
        private Vector3           targetPosition;
        private Quaternion        targetRotation  = Quaternion.identity;
        private Vector3           smoothVelocity;
        private RemotePlayerState latestState;
        private bool              hasSnapshot;

        // ── Animation ─────────────────────────────────────────────────────────
        // SIMPLIFIED: previously we manually mirrored the remote Animator's
        // exact state via Animator.Play(stateHash, 0, normalizedTime) every
        // tick, followed by animator.Update(0f) to force-apply it. That is a
        // LOT of moving parts (hash matching, drift thresholds, manual time
        // advancement) and was the prime suspect for animation freezing at
        // the first frame — if anything in that chain misfired silently
        // (e.g. Play() throwing after the first successful call, or drift
        // calculation stalling), animator.Update(0f) would keep re-applying
        // the SAME frame forever, since deltaTime=0 always means "don't
        // advance."
        //
        // Back to basics: we just feed the CONFIRMED parameters every tick
        // (Forward/Right/Walk/Run/Move + whatever synced bools/floats/ints
        // arrive) and let Unity's own Animator component update itself
        // completely normally, the same way it does for the local player.
        // We never call animator.Update() ourselves during Tick() — Unity's
        // engine loop already calls it automatically for every active,
        // enabled Animator every frame. The authored state machine (idle/
        // walk/run/sit transitions) does the rest, exactly like it does for
        // the real player.

        // ── Coordinate log throttle ────────────────────────────────────────────
        private float nextCoordLogTime;
        private const float CoordLogInterval = 3f;

        // ── Confirmed MiSide animator parameters (stringliteral.json) ──────────
        // "SpeedForward","InertionRight","HeadMove","MouseSpeed","JumpStop",
        // "BedSit","KickSit","OtherAnimationType","OtherAnimationHold",
        // "animstop","facelayer" never appeared in the dump — removed.
        private static readonly int HashForward = Animator.StringToHash("Forward");
        private static readonly int HashRight   = Animator.StringToHash("Right");
        private static readonly int HashWalk    = Animator.StringToHash("Walk");
        private static readonly int HashRun     = Animator.StringToHash("Run");
        private static readonly int HashMove    = Animator.StringToHash("Move");

        public GameObject GameObject { get { return gameObjectRef; } }

        // ── Construction ───────────────────────────────────────────────────────
        public PuppetController(GameObject root)
        {
            gameObjectRef = root;
            transformRef  = root.transform;
        }

        // ── Bind ───────────────────────────────────────────────────────────────
        public void Bind(string remotePlayerId, string remoteDisplayName,
                         Transform clonedVisualRoot, bool enableFallbackMarker)
        {
            playerId           = remotePlayerId;
            displayName        = remoteDisplayName;
            visualRoot         = clonedVisualRoot;
            showFallbackMarker = enableFallbackMarker;

            // ── Animator ──────────────────────────────────────────────────────
            animator = FindAnimator(visualRoot, transformRef);
            LogAnimatorStatus();

            // ── Head bone ─────────────────────────────────────────────────────
            // Target the real Armature bone directly. HeadMirror is deliberately
            // NOT used here — it's a SkinnedMeshRenderer (the mirror-reflection
            // head mesh); its own Transform plays no part in how the mesh
            // deforms, so rotating it is a visual no-op. Only a bone inside the
            // bones[] array that the renderer actually reads has any effect.
            headBone = visualRoot != null ? FindHeadBone(visualRoot) : null;
            if (headBone != null)
                DiagnosticLog.Info(
                    "Head bone bound for '" + playerId + "': " +
                    LocalPlayerLocator.GetPath(headBone));
            else if (visualRoot != null)
                DiagnosticLog.Warning(
                    "No head bone found for '" + playerId + "' — head will not track look direction.");

            // ── DEV orb (always visible above puppet) ──────────────────────────
            EnsureDebugOrb();

            // ── Name tag ──────────────────────────────────────────────────────
            EnsureNameTag();

            // ── Fallback capsule ──────────────────────────────────────────────
            if (showFallbackMarker)
                EnsureDevMarker();

            // ── Animator init ──────────────────────────────────────────────────
            if (animator != null)
            {
                // AlwaysAnimate so the puppet updates even when off-screen.
                // applyRootMotion = false so the puppet doesn't drift from
                // the network-driven position.
                animator.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode      = AnimatorUpdateMode.Normal;
                animator.applyRootMotion = false;
                try { animator.Rebind(); }   catch (Exception) { }
                try { animator.Update(0f); } catch (Exception) { }
            }
        }

        // ── Visibility ─────────────────────────────────────────────────────────
        public void SetVisible(bool visible)
        {
            SetActive(visualRoot,    visible);
            SetActive(debugOrbRoot,  visible);
            SetActive(nameTagRoot,   visible);
            SetActive(devMarkerRoot, visible);
        }

        // ── Snapshot ───────────────────────────────────────────────────────────
        public void ApplySnapshot(RemotePlayerState state)
        {
            if (state == null) return;
            latestState        = state;
            targetPosition      = state.position.ToUnity();
            targetRotation      = Norm(state.rotation.ToUnity());
            targetHeadRotation  = Norm(state.headRotation.ToUnity());

            if (!hasSnapshot)
            {
                transformRef.SetPositionAndRotation(targetPosition, targetRotation);
                smoothVelocity = Vector3.zero;
                hasSnapshot    = true;
                // Head bone pose isn't applied here — it needs the Animator to
                // have run at least once first (see LateTick/ApplyHeadRotation).
            }
        }

        // ── Per-frame tick ──────────────────────────────────────────────────────
        public void Tick()
        {
            if (!hasSnapshot) return;

            // ── Body position ──────────────────────────────────────────────────
            float dist = Vector3.Distance(transformRef.position, targetPosition);
            if (dist > TeleportDistance)
            {
                transformRef.position = targetPosition;
                smoothVelocity        = Vector3.zero;
            }
            else
            {
                transformRef.position = Vector3.SmoothDamp(
                    transformRef.position, targetPosition,
                    ref smoothVelocity, PositionSmoothTime);
            }

            // ── Body rotation ──────────────────────────────────────────────────
            float rotT = 1f - Mathf.Exp(-RotationLerpSpeed * Time.deltaTime);
            transformRef.rotation = Quaternion.Slerp(
                transformRef.rotation, targetRotation, rotT);

            // ── Animator ──────────────────────────────────────────────────────
            if (animator != null && latestState != null)
                UpdateAnimator();

            // NOTE: head bone rotation is NOT applied here. Tick() runs during
            // Update(), which happens BEFORE the Animator evaluates this frame's
            // pose — any bone rotation written here gets silently overwritten
            // the moment the Animator runs, later the same frame. It must be
            // written in LateTick() (after LateUpdate, once the Animator has
            // already posed every bone) to actually stick. This was the root
            // cause of the head never visibly moving.

            // ── Name tag billboard ─────────────────────────────────────────────
            UpdateNameTagFacing();

            // ── Periodic coordinate log ────────────────────────────────────────
            if (Time.unscaledTime >= nextCoordLogTime)
            {
                nextCoordLogTime = Time.unscaledTime + CoordLogInterval;
                LogCoordinates();
            }
        }

        public void LateTick()
        {
            ApplyHeadRotation(Time.deltaTime);
            UpdateNameTagFacing();
        }

        // Overrides ONLY the head bone's local PITCH (X axis) after the Animator
        // has posed the skeleton this frame, so idle head-sway (Y/Z) authored by
        // the animator survives while up/down look direction comes from the
        // remote player's camera.
        //
        // IMPORTANT: this targets headBone — the actual Armature bone
        // (Person/Armature/.../Head) — never HeadMirror. HeadMirror is a
        // SkinnedMeshRenderer; its own Transform is not read by the skinning
        // system at all (mesh deformation comes entirely from the bones[]
        // array), so rotating HeadMirror itself is visually a no-op. This is
        // also why HeadPlayer/FixHead must never be rotated here — they are
        // camera-rig / IK-target transforms with no skinning role either.
        private void ApplyHeadRotation(float deltaTime)
        {
            if (headBone == null || !hasSnapshot) return;

            Quaternion relativeToBody = Quaternion.Inverse(transformRef.rotation) * targetHeadRotation;
            float pitch = relativeToBody.eulerAngles.x;
            if (pitch > 180f) pitch -= 360f;
            pitch = Mathf.Clamp(pitch, -75f, 75f);

            float headT = 1f - Mathf.Exp(-HeadLerpSpeed * deltaTime);
            headPitchSmoothed = Mathf.Lerp(headPitchSmoothed, pitch, headT);

            Vector3 cur = headBone.localRotation.eulerAngles;
            headBone.localRotation = Quaternion.Euler(headPitchSmoothed, cur.y, cur.z);
        }

        // ── Animator update ─────────────────────────────────────────────────────
        private void UpdateAnimator()
        {
            // Drive the confirmed locomotion parameters from velocity.
            SetF(HashForward, latestState.speed);
            SetF(HashRight,   latestState.lateralSpeed);
            SetB(HashWalk,    latestState.speed > 0.05f);
            SetB(HashRun,     latestState.speed > 3.2f);
            SetB(HashMove,    latestState.speed > 0.05f);

            // Apply synced parameters from the remote state (this is where
            // "Sit" and anything else the remote's own animator reports comes
            // through — see LocalPlayerSampler.FillKnownAnimatorParams).
            if (latestState.floatParameters != null)
                for (int i = 0; i < latestState.floatParameters.Length; i++)
                    SetF(Animator.StringToHash(latestState.floatParameters[i].name),
                         latestState.floatParameters[i].value);

            if (latestState.boolParameters != null)
                for (int i = 0; i < latestState.boolParameters.Length; i++)
                    SetB(Animator.StringToHash(latestState.boolParameters[i].name),
                         latestState.boolParameters[i].value);

            if (latestState.intParameters != null)
                for (int i = 0; i < latestState.intParameters.Length; i++)
                    SetI(Animator.StringToHash(latestState.intParameters[i].name),
                         latestState.intParameters[i].value);

            // Layer weights
            if (latestState.blendWeights != null)
            {
                int n = Mathf.Min(latestState.blendWeights.Length, animator.layerCount);
                for (int i = 0; i < n; i++)
                {
                    try { animator.SetLayerWeight(i, latestState.blendWeights[i]); }
                    catch (Exception) { }
                }
            }

            // NOTE: no manual animator.Play()/animator.Update() call here.
            // Unity calls Update() on every active, enabled Animator
            // automatically every engine frame — exactly like it does for the
            // real player's own Animator. The parameters set above drive the
            // authored state machine's own transitions (idle → walk → run,
            // etc.) completely normally.
        }

        private void SetF(int h, float v) { try { animator.SetFloat(h, v); }   catch(Exception){} }
        private void SetB(int h, bool  v) { try { animator.SetBool(h, v); }    catch(Exception){} }
        private void SetI(int h, int   v) { try { animator.SetInteger(h, v); } catch(Exception){} }

        // ── Debug orb ──────────────────────────────────────────────────────────
        private void EnsureDebugOrb()
        {
            if (debugOrbRoot != null) return;
            GameObject r = new GameObject("DEV_Player2Orb_" + playerId);
            r.transform.SetParent(transformRef, false);
            r.transform.localPosition = new Vector3(0f, 2.4f, 0f);
            r.transform.localScale    = Vector3.one;
            debugOrbRoot = r.transform;
            MakeOrbSphere(r.transform, "OuterOrb",
                new Color(0f, 0.85f, 1f, 0.92f), new Vector3(0.32f, 0.32f, 0.32f));
            MakeOrbSphere(r.transform, "InnerCore",
                new Color(0.75f, 0.97f, 1f, 1f),  new Vector3(0.14f, 0.14f, 0.14f));
            DiagnosticLog.Info("Debug orb created for '" + playerId + "'.");
        }

        private static void MakeOrbSphere(Transform parent, string label,
                                           Color color, Vector3 scale)
        {
            GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = label;
            s.transform.SetParent(parent, false);
            s.transform.localPosition = Vector3.zero;
            s.transform.localScale    = scale;
            s.layer = 0;
            Collider c = s.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
            Renderer rend = s.GetComponent<Renderer>();
            if (rend != null)
            {
                Material m = DevMat(color);
                if (m != null) rend.sharedMaterial = m;
                rend.enabled = true;
            }
        }

        // ── Name tag ───────────────────────────────────────────────────────────
        private void EnsureNameTag()
        {
            if (nameTagRoot != null) return;
            string label = string.IsNullOrEmpty(displayName) ? playerId : displayName;
            GameObject tag = new GameObject("NameTag_" + playerId);
            tag.transform.SetParent(transformRef, false);
            tag.transform.localPosition = new Vector3(0f, 2.95f, 0f);
            tag.transform.localScale    = Vector3.one;
            nameTagRoot = tag.transform;
            try
            {
                nameTagMesh               = tag.AddComponent<TextMesh>();
                nameTagMesh.text          = label;
                nameTagMesh.fontSize      = 56;
                nameTagMesh.characterSize = 0.048f;
                nameTagMesh.anchor        = TextAnchor.MiddleCenter;
                nameTagMesh.alignment     = TextAlignment.Center;
                nameTagMesh.color         = Color.white;
                DiagnosticLog.Info(
                    "Name tag '" + label + "' created for '" + playerId + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "TextMesh unavailable — name tag for '" + playerId +
                    "' logged only: " + ex.Message);
                nameTagMesh = null;
            }
        }

        private void UpdateNameTagFacing()
        {
            if (nameTagRoot == null || Camera.main == null) return;
            nameTagRoot.rotation =
                Camera.main.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        }

        // ── Fallback marker ────────────────────────────────────────────────────
        private void EnsureDevMarker()
        {
            if (devMarkerRoot != null) return;
            GameObject mr = new GameObject("DEV_Marker_" + playerId);
            mr.transform.SetParent(transformRef, false);
            mr.transform.localPosition = new Vector3(0f, 1.85f, 0f);
            devMarkerRoot = mr.transform;

            GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cap.name = "Body"; cap.transform.SetParent(devMarkerRoot, false);
            cap.transform.localScale = new Vector3(0.35f, 0.7f, 0.35f); cap.layer = 0;
            Collider cc = cap.GetComponent<Collider>();
            if (cc != null) UnityEngine.Object.Destroy(cc);
            Renderer cr = cap.GetComponent<Renderer>();
            if (cr != null)
            {
                Material m = DevMat(new Color(0f, 1f, 0.35f, 0.9f));
                if (m != null) cr.sharedMaterial = m;
                cr.enabled = true;
            }

            GameObject hd = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hd.name = "Head"; hd.transform.SetParent(devMarkerRoot, false);
            hd.transform.localPosition = new Vector3(0f, 0.82f, 0f);
            hd.transform.localScale    = new Vector3(0.42f, 0.42f, 0.42f); hd.layer = 0;
            Collider hc = hd.GetComponent<Collider>();
            if (hc != null) UnityEngine.Object.Destroy(hc);
            Renderer hr = hd.GetComponent<Renderer>();
            if (hr != null)
            {
                Material m = DevMat(new Color(1f, 0.1f, 0.85f, 0.95f));
                if (m != null) hr.sharedMaterial = m;
                hr.enabled = true;
            }

            DiagnosticLog.Warning(
                "Fallback capsule marker created for '" + playerId +
                "' (visual clone had 0 renderers).");
        }

        // ── Coordinate logger ──────────────────────────────────────────────────
        private void LogCoordinates()
        {
            Vector3 p = transformRef.position;
            DiagnosticLog.Info(
                "Player2 [" + playerId + "]" +
                "  x=" + p.x.ToString("F3") +
                "  y=" + p.y.ToString("F3") +
                "  z=" + p.z.ToString("F3") +
                (latestState != null
                    ? "  scene=" + latestState.sceneName +
                      "  spd="   + latestState.speed.ToString("F2") +
                      "  stateHash=" + latestState.animatorStateHash
                    : ""));
        }

        // ── Lookup helpers ─────────────────────────────────────────────────────
        private static Animator FindAnimator(Transform visualRoot, Transform puppetRoot)
        {
            if (visualRoot != null)
            {
                Animator a = visualRoot.GetComponentInChildren<Animator>(true);
                if (a != null) return a;
            }
            if (puppetRoot != null)
            {
                for (int i = 0; i < puppetRoot.childCount; i++)
                {
                    Transform ch = puppetRoot.GetChild(i);
                    if (ch == null || ch == visualRoot) continue;
                    Animator a = ch.GetComponentInChildren<Animator>(true);
                    if (a != null) return a;
                }
            }
            return null;
        }

        // Dump rig path: Person/Armature/Hips/Spine/Chest/Neck2/Neck1/Head
        private static readonly string[] HeadBonePaths =
        {
            "Person/Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck/Head",
        };
        private static readonly string[] HeadBoneNames =
            { "Head", "head", "Neck1", "Neck", "HeadBone" };

        private static Transform FindHeadBone(Transform root)
        {
            if (root == null) return null;
            for (int i = 0; i < HeadBonePaths.Length; i++)
            {
                Transform t = root.Find(HeadBonePaths[i]);
                if (t != null) return t;
            }
            for (int i = 0; i < HeadBoneNames.Length; i++)
            {
                Transform t = FindByNameRecursive(root, HeadBoneNames[i]);
                if (t != null) return t;
            }
            return null;
        }

        private static Transform FindByNameRecursive(Transform root, string targetName)
        {
            if (root == null) return null;
            if (string.Equals(root.name, targetName,
                              StringComparison.OrdinalIgnoreCase)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindByNameRecursive(root.GetChild(i), targetName);
                if (r != null) return r;
            }
            return null;
        }

        private void LogAnimatorStatus()
        {
            if (animator != null)
                DiagnosticLog.Info(
                    "Animator for '" + playerId + "' at " +
                    LocalPlayerLocator.GetPath(animator.transform) +
                    "  ctrl=" + (animator.runtimeAnimatorController != null
                        ? animator.runtimeAnimatorController.name : "NULL"));
            else
                DiagnosticLog.Warning(
                    "No Animator for puppet '" + playerId +
                    "'. Animation won't play. Check Person/model(Clone) has an Animator.");
        }

        // ── Statics ────────────────────────────────────────────────────────────
        private static Material DevMat(Color color)
        {
            Shader sh = Shader.Find("Standard")
                     ?? Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Sprites/Default");
            if (sh == null) return null;
            Material m = new Material(sh);
            m.color = color;
            return m;
        }

        private static void SetActive(Transform t, bool active)
        {
            if (t != null && t.gameObject.activeSelf != active)
                t.gameObject.SetActive(active);
        }

        private static Quaternion Norm(Quaternion q)
            => (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
               ? Quaternion.identity : q;
    }
}
