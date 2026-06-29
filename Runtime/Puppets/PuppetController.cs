using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetController
    {
        // ── Smoothing ──────────────────────────────────────────────────────────
        private const float PositionSmoothTime = 0.075f;
        private const float RotationLerpSpeed  = 18f;
        private const float HeadLerpSpeed      = 22f;
        private const float TeleportDistance   = 7f;

        // ── Identity ───────────────────────────────────────────────────────────
        private string     playerId;
        private string     displayName;
        private GameObject gameObjectRef;
        private Transform  transformRef;
        private Transform  visualRoot;
        private Animator   animator;

        // ── Head rotation ──────────────────────────────────────────────────────
        // headBone: the actual bone inside the Armature hierarchy that gets
        // rotated in LateTick() AFTER the animator has run.  This is the only
        // reliable way to override animator-driven bone transforms.
        //
        // Why not HeadMirror?  HeadMirror is a SkinnedMeshRenderer whose
        // vertices deform from the BONES (Armature), not from rotating the
        // HeadMirror transform itself.  Rotating the transform only moves the
        // mesh root — it does not change head direction for the viewer.
        //
        // Why not HeadPlayer?  LookAtIK (which reads HeadPlayer) is removed by
        // SanitizeVisualClone.  Without the IK running, HeadPlayer's rotation
        // has no effect on the actual head bones.
        //
        // Solution: find the real head bone via FindHeadBone(), then in
        // LateTick() override its local X (pitch) AFTER the animator updates.
        private Transform  headBone;
        private Quaternion targetHeadRotation = Quaternion.identity;
        private float      headPitchSmoothed;  // degrees, smoothed in LateTick

        // ── DEV orb ────────────────────────────────────────────────────────────
        private Transform  debugOrbRoot;

        // ── Name tag ───────────────────────────────────────────────────────────
        private Transform  nameTagRoot;
        private TextMesh   nameTagMesh;

        // ── Fallback capsule ───────────────────────────────────────────────────
        private Transform  devMarkerRoot;
        private bool       showFallbackMarker;

        // ── Snapshot ───────────────────────────────────────────────────────────
        private Vector3           targetPosition;
        private Quaternion        targetRotation = Quaternion.identity;
        private Vector3           smoothVelocity;
        private RemotePlayerState latestState;
        private bool              hasSnapshot;
        private string            lastAction;

        // ── Coordinate log throttle ────────────────────────────────────────────
        private float nextCoordLogTime;
        private const float CoordLogInterval = 3f;

        // ── MiSide animator parameter hashes (from IL2CPP dump string literals) ─
        private static readonly int HashSpeedForward  = Animator.StringToHash("SpeedForward");
        private static readonly int HashInertionRight = Animator.StringToHash("InertionRight");
        private static readonly int HashHeadMove      = Animator.StringToHash("HeadMove");
        private static readonly int HashMouseSpeed    = Animator.StringToHash("MouseSpeed");
        private static readonly int HashForward       = Animator.StringToHash("Forward");
        private static readonly int HashWalk          = Animator.StringToHash("Walk");
        private static readonly int HashRun           = Animator.StringToHash("Run");
        private static readonly int HashIdle          = Animator.StringToHash("Idle");
        private static readonly int HashSit           = Animator.StringToHash("Sit");
        private static readonly int HashJump          = Animator.StringToHash("Jump");
        private static readonly int HashJumpStop      = Animator.StringToHash("JumpStop");
        private static readonly int HashBedSit        = Animator.StringToHash("BedSit");
        private static readonly int HashKickSit       = Animator.StringToHash("KickSit");
        private static readonly int HashOtherAnimType = Animator.StringToHash("OtherAnimationType");
        private static readonly int HashOtherAnimHold = Animator.StringToHash("OtherAnimationHold");
        private static readonly int HashAnimStop      = Animator.StringToHash("animstop");
        private static readonly int HashFaceLayer     = Animator.StringToHash("facelayer");

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

            if (animator != null)
            {
                try { animator.applyRootMotion = false; } catch (Exception) { }

                // NOTE: Rebind() is intentionally NOT called.
                // In IL2CPP context, Rebind() resets the animator to an unbound
                // state and requires a manual Play() call to restart.  Omitting
                // it lets the freshly-instantiated clone start in its entry state
                // (idle) automatically, which is what we want.

                // Force one evaluation at time 0 to set initial pose.
                try { animator.Update(0f); } catch (Exception) { }
            }

            // ── Head bone ─────────────────────────────────────────────────────
            // Find the real Armature head bone so LateTick() can override its
            // local pitch AFTER the animator has run each frame.
            headBone = FindHeadBone(visualRoot);
            if (headBone != null)
                DiagnosticLog.Info(
                    "Head bone for '" + playerId + "': " +
                    LocalPlayerLocator.GetPath(headBone));
            else
                DiagnosticLog.Warning(
                    "No head bone found in clone for '" + playerId +
                    "'. Head rotation will not be applied.");

            // ── Debug orb ─────────────────────────────────────────────────────
            EnsureDebugOrb();

            // ── Name tag ──────────────────────────────────────────────────────
            EnsureNameTag();

            // ── Fallback capsule ──────────────────────────────────────────────
            if (showFallbackMarker)
                EnsureDevMarker();

            DiagnosticLog.Info("Puppet bind complete for '" + playerId + "'.");
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
            targetPosition     = state.position.ToUnity();
            targetRotation     = Norm(state.rotation.ToUnity());
            targetHeadRotation = Norm(state.headRotation.ToUnity());

            if (!hasSnapshot)
            {
                transformRef.SetPositionAndRotation(targetPosition, targetRotation);
                smoothVelocity = Vector3.zero;
                hasSnapshot    = true;

                DiagnosticLog.Info(
                    "First snapshot for '" + playerId + "'" +
                    "  pos=(" + targetPosition.x.ToString("F2") + ", " +
                               targetPosition.y.ToString("F2") + ", " +
                               targetPosition.z.ToString("F2") + ")" +
                    "  scene=" + (state.sceneName ?? "?"));
            }
        }

        // ── Update tick (called from Update) ───────────────────────────────────
        public void Tick()
        {
            if (!hasSnapshot) return;

            // Body position
            float dist = Vector3.Distance(transformRef.position, targetPosition);
            if (dist > TeleportDistance)
            {
                transformRef.position = targetPosition;
                smoothVelocity        = Vector3.zero;
                DiagnosticLog.Info(
                    "Puppet '" + playerId + "' teleported (" + dist.ToString("F1") + "u).");
            }
            else
            {
                transformRef.position = Vector3.SmoothDamp(
                    transformRef.position, targetPosition,
                    ref smoothVelocity, PositionSmoothTime);
            }

            // Body rotation
            float rotT = 1f - Mathf.Exp(-RotationLerpSpeed * Time.deltaTime);
            transformRef.rotation =
                Quaternion.Slerp(transformRef.rotation, targetRotation, rotT);

            // Animator parameters
            UpdateAnimator();

            // Name tag billboard
            UpdateNameTagFacing();

            // Coordinate log
            if (Time.unscaledTime >= nextCoordLogTime)
            {
                nextCoordLogTime = Time.unscaledTime + CoordLogInterval;
                LogCoordinates();
            }
        }

        // ── LateUpdate tick (called from LateUpdate, AFTER animator) ──────────
        // The Animator updates bones during the physics/animation pass that
        // precedes LateUpdate (for AnimatorUpdateMode.Normal).  Overriding bone
        // rotations here therefore persists for the frame instead of being
        // immediately overwritten by the next animator evaluation.
        public void LateTick()
        {
            if (!hasSnapshot || headBone == null) return;

            // Extract the body-relative pitch (up/down angle) from the received
            // head rotation.  targetHeadRotation is HeadPlayer's WORLD rotation
            // (sampled from the remote player's HeadPlayer sibling), which
            // encodes both the body's yaw AND the camera's pitch.
            // Removing the body yaw leaves us with just the local pitch.
            Quaternion relativeHead = Quaternion.Inverse(transformRef.rotation) * targetHeadRotation;
            float pitch = relativeHead.eulerAngles.x;
            if (pitch > 180f) pitch -= 360f;  // normalise to [-180, 180]
            pitch = Mathf.Clamp(pitch, -80f, 80f);

            // Smooth the pitch so jerky network updates don't snap the head
            float headT = 1f - Mathf.Exp(-HeadLerpSpeed * Time.deltaTime);
            headPitchSmoothed = Mathf.Lerp(headPitchSmoothed, pitch, headT);

            // Apply ONLY the X (pitch) axis; preserve Y and Z that the animator
            // baked in (keeps idle head-sway / look-around animations intact).
            Vector3 cur = headBone.localRotation.eulerAngles;
            headBone.localRotation = Quaternion.Euler(headPitchSmoothed, cur.y, cur.z);
        }

        // ── Animator ───────────────────────────────────────────────────────────
        private void UpdateAnimator()
        {
            if (animator == null || latestState == null) return;

            // Drive known MiSide parameters from speed/lateralSpeed
            SetF(HashSpeedForward,  latestState.speed);
            SetF(HashInertionRight, latestState.lateralSpeed);
            SetF(HashForward,       latestState.speed);

            // Always keep animstop = false on the puppet.
            // The local player may be in a cutscene (animstop=true) while the
            // puppet should still animate normally.
            SetB(HashAnimStop, false);

            // Apply all synced params received from the remote player
            if (latestState.floatParameters != null)
                for (int i = 0; i < latestState.floatParameters.Length; i++)
                    SetF(Animator.StringToHash(latestState.floatParameters[i].name),
                         latestState.floatParameters[i].value);

            if (latestState.boolParameters != null)
                for (int i = 0; i < latestState.boolParameters.Length; i++)
                {
                    string pname = latestState.boolParameters[i].name;
                    // Never let the remote animstop override our forced-false above
                    if (string.Equals(pname, "animstop", StringComparison.OrdinalIgnoreCase))
                        continue;
                    SetB(Animator.StringToHash(pname), latestState.boolParameters[i].value);
                }

            if (latestState.intParameters != null)
                for (int i = 0; i < latestState.intParameters.Length; i++)
                    SetI(Animator.StringToHash(latestState.intParameters[i].name),
                         latestState.intParameters[i].value);

            // Layer weights
            if (latestState.blendWeights != null)
            {
                int n = Mathf.Min(latestState.blendWeights.Length, animator.layerCount);
                for (int i = 1; i < n; i++)   // skip layer 0 — its weight is always 1
                    try { animator.SetLayerWeight(i, latestState.blendWeights[i]); }
                    catch (Exception) { }
            }

            // Cross-fade to named action state
            if (!string.IsNullOrEmpty(latestState.action) && latestState.action != lastAction)
            {
                int h = Animator.StringToHash(latestState.action);
                try
                {
                    if (animator.HasState(0, h))
                    {
                        animator.CrossFadeInFixedTime(h, 0.08f);
                        lastAction = latestState.action;
                    }
                }
                catch (Exception) { }
            }
        }

        private void SetF(int h, float v) { try { animator.SetFloat(h, v);   } catch (Exception) { } }
        private void SetB(int h, bool  v) { try { animator.SetBool(h, v);    } catch (Exception) { } }
        private void SetI(int h, int   v) { try { animator.SetInteger(h, v); } catch (Exception) { } }

        // ── Head bone search ───────────────────────────────────────────────────
        // Known rig paths from IL2CPP dump + bone name fallback.
        // When cloning Player (not just Person), paths must start below the
        // Person child, so we prepend "Person/" variants too.
        private static readonly string[] HeadBonePaths =
        {
            "Person/Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Person/Armature/Hips/Spine/Chest/Neck1/Head",
            "Person/Armature/Hips/Spine/Chest/Neck/Head",
            "Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck/Head",
        };

        private static readonly string[] HeadBoneNames =
        {
            "Head", "head", "Neck1", "Neck", "HeadBone", "Bip001 Head",
        };

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
                Transform t = FindByName(root, HeadBoneNames[i]);
                if (t != null) return t;
            }
            return null;
        }

        // ── Animator search ────────────────────────────────────────────────────
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
                    Transform child = puppetRoot.GetChild(i);
                    if (child == null || child == visualRoot) continue;
                    Animator a = child.GetComponentInChildren<Animator>(true);
                    if (a != null) return a;
                }
            }
            return null;
        }

        private static Transform FindByName(Transform root, string targetName)
        {
            if (root == null) return null;
            if (string.Equals(root.name, targetName, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindByName(root.GetChild(i), targetName);
                if (r != null) return r;
            }
            return null;
        }

        // ── Debug orb ──────────────────────────────────────────────────────────
        private void EnsureDebugOrb()
        {
            if (debugOrbRoot != null) return;

            GameObject root = new GameObject("DEV_Player2Orb_" + playerId);
            root.transform.SetParent(transformRef, false);
            root.transform.localPosition = new Vector3(0f, 2.4f, 0f);
            debugOrbRoot = root.transform;

            MakeOrbSphere(root.transform, "OuterOrb",
                new Color(0f, 0.85f, 1f, 0.92f), new Vector3(0.32f, 0.32f, 0.32f));
            MakeOrbSphere(root.transform, "InnerCore",
                new Color(0.75f, 0.97f, 1f, 1f), new Vector3(0.14f, 0.14f, 0.14f));

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
            Renderer r = s.GetComponent<Renderer>();
            if (r != null) { r.sharedMaterial = DevMat(color); r.enabled = true; }
        }

        // ── Name tag ───────────────────────────────────────────────────────────
        private void EnsureNameTag()
        {
            if (nameTagRoot != null) return;

            string label = string.IsNullOrEmpty(displayName) ? playerId : displayName;

            GameObject tagObj = new GameObject("NameTag_" + playerId);
            tagObj.transform.SetParent(transformRef, false);
            tagObj.transform.localPosition = new Vector3(0f, 2.95f, 0f);
            nameTagRoot = tagObj.transform;

            try
            {
                nameTagMesh               = tagObj.AddComponent<TextMesh>();
                nameTagMesh.text          = label;
                nameTagMesh.fontSize      = 56;
                nameTagMesh.characterSize = 0.048f;
                nameTagMesh.anchor        = TextAnchor.MiddleCenter;
                nameTagMesh.alignment     = TextAlignment.Center;
                nameTagMesh.color         = Color.white;
                DiagnosticLog.Info("Name tag: '" + playerId + "' → \"" + label + "\".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "TextMesh unavailable for '" + label + "': " + ex.Message);
                nameTagMesh = null;
            }
        }

        private void UpdateNameTagFacing()
        {
            if (nameTagRoot == null || Camera.main == null) return;
            nameTagRoot.rotation =
                Camera.main.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
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
                      "  spd=" + latestState.speed.ToString("F2")
                    : ""));
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
            Collider cc = cap.GetComponent<Collider>(); if (cc != null) UnityEngine.Object.Destroy(cc);
            Renderer cr = cap.GetComponent<Renderer>(); if (cr != null)
            { cr.sharedMaterial = DevMat(new Color(0f,1f,0.35f,0.9f)); cr.enabled = true; }

            GameObject hd = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hd.name = "Head"; hd.transform.SetParent(devMarkerRoot, false);
            hd.transform.localPosition = new Vector3(0f, 0.82f, 0f);
            hd.transform.localScale    = new Vector3(0.42f, 0.42f, 0.42f); hd.layer = 0;
            Collider hc = hd.GetComponent<Collider>(); if (hc != null) UnityEngine.Object.Destroy(hc);
            Renderer hr = hd.GetComponent<Renderer>(); if (hr != null)
            { hr.sharedMaterial = DevMat(new Color(1f,0.1f,0.85f,0.95f)); hr.enabled = true; }

            DiagnosticLog.Warning("Fallback marker created for '" + playerId + "'.");
        }

        private void LogAnimatorStatus()
        {
            if (animator != null)
                DiagnosticLog.Info(
                    "Animator for '" + playerId + "' at: " +
                    LocalPlayerLocator.GetPath(animator.transform) +
                    "  ctrl=" + (animator.runtimeAnimatorController != null
                        ? animator.runtimeAnimatorController.name : "NULL"));
            else
                DiagnosticLog.Warning(
                    "No Animator for puppet '" + playerId + "'.");
        }

        // ── Static helpers ─────────────────────────────────────────────────────
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
        {
            return (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
                   ? Quaternion.identity : q;
        }
    }
}
