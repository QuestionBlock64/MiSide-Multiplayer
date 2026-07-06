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
        // Forward/Right are real float parameters (PlayerMove.animForward/
        // animRight). Walk/Run/Move are NOT independently-settable bool
        // parameters — see the comment in UpdateAnimator for why they were
        // removed from here.
        private static readonly int HashForward = Animator.StringToHash("Forward");
        private static readonly int HashRight   = Animator.StringToHash("Right");

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
            // EnsureDebugOrb();

            // ── Name tag ──────────────────────────────────────────────────────
            EnsureNameTag();

            // ── Fallback capsule ──────────────────────────────────────────────
            if (showFallbackMarker)
                EnsureDevMarker();

            // ── Animator init ──────────────────────────────────────────────────
            if (animator != null)
            {
                // Diagnostic: state IMMEDIATELY as found, before we touch anything.
                // If this is ALREADY hash:0 here, the clone itself came out of
                // Instantiate()/Sanitize in a broken state (e.g. the Animator
                // destroy+restore safety net in VisualCloneUtility producing an
                // incompletely-initialised replacement) — a different bug than
                // anything we do below. If it's valid HERE but hash:0 by the
                // time Tick() logs it later, our own setup below is the culprit.
                LogAnimatorStateProbe("as-found (pre-setup)");

                animator.enabled         = true;   // defensive — should already be true
                animator.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode      = AnimatorUpdateMode.Normal;
                animator.applyRootMotion = false;

                // NOTE: Rebind() intentionally NOT called. Rebind() exists for
                // avatar-SWAP scenarios (replacing the skeleton/avatar an
                // Animator is already bound to) — Object.Instantiate() already
                // clones and binds the Animator+Avatar+Controller correctly as
                // part of a normal clone, with no extra step required. Calling
                // Rebind() here was a "just in case" precaution that may have
                // been leaving the state machine un-entered in IL2CPP specifically.
                try { animator.Update(0f); } catch (Exception) { }

                LogAnimatorStateProbe("after-setup (post Update(0f))");
                RunParameterSentinelTest();
            }
        }

        // Definitive test: can SetFloat/GetFloat even round-trip a value on
        // THIS specific cloned Animator instance at all? An obvious value
        // (12345) that could never occur naturally makes the result
        // unambiguous. Tests both the int-hash overload (what UpdateAnimator
        // actually uses every tick) and the string overload directly, to
        // rule out any possibility of a hash mismatch between where we set
        // vs where we read (both should be impossible given HashForward is a
        // single static field, but this removes all doubt either way).
        private void RunParameterSentinelTest()
        {
            const float sentinel = 12345f;
            try
            {
                animator.SetFloat(HashForward, sentinel);
                float viaHash = animator.GetFloat(HashForward);

                animator.SetFloat("Forward", sentinel + 1f);
                float viaString = animator.GetFloat("Forward");

                DiagnosticLog.Info(
                    "  [sentinel test] '" + playerId + "'" +
                    "  set " + sentinel + " via hash → read back " + viaHash.ToString("F1") +
                    "  |  set " + (sentinel + 1f) + " via string \"Forward\" → read back " + viaString.ToString("F1") +
                    "  " + (Mathf.Approximately(viaHash, sentinel)
                            ? "ROUND-TRIP OK (SetFloat/GetFloat work — bug must be elsewhere, e.g. something else overwriting it between UpdateAnimator and the log read)"
                            : "ROUND-TRIP FAILED (SetFloat has NO EFFECT on this parameter for this Animator instance — 'Forward' is being silently rejected, most likely because the clone's actual assigned controller doesn't define a parameter with this name/hash at all, despite appearing to elsewhere)"));

                // Reset to a neutral value so the sentinel doesn't linger and
                // confuse the very next real UpdateAnimator() call this frame.
                animator.SetFloat(HashForward, 0f);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "  [sentinel test] threw for '" + playerId + "': " + ex.Message);
            }
        }

        // One-off diagnostic snapshot of the Animator's actual current state.
        // hash:0 + t=0.00 means the state machine has never been entered at
        // all (no controller, or genuinely never started) — NOT "stuck on idle."
        private void LogAnimatorStateProbe(string label)
        {
            try
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                DiagnosticLog.Info(
                    "  [animator probe: " + label + "] '" + playerId + "'" +
                    "  enabled=" + animator.enabled +
                    "  activeInHierarchy=" + animator.gameObject.activeInHierarchy +
                    "  ctrl=" + (animator.runtimeAnimatorController != null
                                 ? animator.runtimeAnimatorController.name : "NULL") +
                    "  avatar=" + (animator.avatar != null
                                 ? (animator.avatar.name + " valid=" + animator.avatar.isValid + " human=" + animator.avatar.isHuman)
                                 : "NULL") +
                    "  stateHash=" + info.shortNameHash +
                    "  normalizedTime=" + info.normalizedTime.ToString("F3"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "  [animator probe: " + label + "] failed for '" + playerId + "': " + ex.Message);
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

            // NOTE: no manual animator.Play()/state-hash mirroring here anymore.
            // That mechanism existed to work around the Animator never holding
            // a valid state at all — which turned out to be caused by
            // Animator.Rebind() in Bind(), not by anything missing here. With
            // Rebind() removed and FindBestAnimator now correctly reading
            // Person's real, controlled animator (previously it could lock
            // onto an uncontrolled custom-model sibling and read Forward as
            // permanently 0), the Animator holds a valid state on its own and
            // MiSide's locomotion is very likely one continuous blend-tree
            // state driven purely by Forward/Right — it needs correct
            // parameter VALUES, not discrete state-switching calls from us.
            // UpdateAnimator() feeds those every tick; that should be
            // sufficient on its own now that both prerequisites are fixed.
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
            // Seed Forward/Right from our own (deadzoned) computed velocity as a
            // fallback in case sync data is briefly missing. These are REAL,
            // confirmed float parameters (PlayerMove.animForward/animRight) —
            // the synced floatParameters loop below overwrites them with the
            // authoritative live values read directly off the remote's own
            // animator, which is what should actually drive playback.
            SetF(HashForward, latestState.speed);
            SetF(HashRight,   latestState.lateralSpeed);

            // Deliberately NOT setting Walk/Run/Move bools here anymore.
            // "Walk"/"Run"/"Sit"/"Idle" (stringliteral.json) are almost
            // certainly animation STATE names reached via blend-tree
            // thresholds on Forward/Right inside the controller graph, not
            // independently settable bool parameters — PlayerMove's own
            // fields (canRun/needRun/animationRun/animationFast) are internal
            // C# script state, not 1:1 animator parameters. Since there is no
            // real "Walk" parameter, nothing in the synced boolParameters list
            // could ever correct a guessed value here, so a naive
            // speed > 0.05f threshold was the ONLY thing driving puppet
            // locomotion — and since our velocity is derived by
            // differentiating position (noisy) rather than read from clean
            // keyboard input (exact zero when idle), it was almost always
            // "a little bit walking." Feeding the real, correctly-synced
            // Forward/Right into the SAME authored graph the real player uses
            // lets it decide idle vs walk vs run exactly the way it already
            // does for the real player — no re-derivation needed.

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

            string liveInfo = "";
            if (animator != null)
            {
                float liveForward = 0f;
                string stateName = "?";
                try { liveForward = animator.GetFloat(HashForward); } catch (Exception) { }
                try
                {
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                    stateName = "hash:" + info.shortNameHash + " t=" + info.normalizedTime.ToString("F2");
                }
                catch (Exception) { }
                liveInfo = "  liveForward=" + liveForward.ToString("F3") + "  animState=" + stateName;
            }

            // Raw received parameters — exactly what the SENDER reported, before
            // anything on our side interprets/applies it. If "Forward" never
            // appears here at all, the problem is on the sending side (either
            // discovery isn't finding a real "Forward" param on the sender's
            // animator, or it belongs to a DIFFERENT animator than the one being
            // sampled — MiSide has at least two: PlayerMove.animPerson (main
            // body) and PlayerMove.animArmsFace (arm/face overlay) — the
            // PlayerArmsHead.animForward/animRight fields may belong to the
            // LATTER, not the body locomotion animator we're driving here).
            string recvInfo = "  recv[";
            if (latestState != null && latestState.floatParameters != null && latestState.floatParameters.Length > 0)
            {
                for (int i = 0; i < latestState.floatParameters.Length; i++)
                {
                    if (i > 0) recvInfo += ",";
                    recvInfo += latestState.floatParameters[i].name + "=" +
                                latestState.floatParameters[i].value.ToString("F2");
                }
            }
            else
            {
                recvInfo += "EMPTY";
            }
            recvInfo += "]";

            DiagnosticLog.Info(
                "Player2 [" + playerId + "]" +
                "  x=" + p.x.ToString("F3") +
                "  y=" + p.y.ToString("F3") +
                "  z=" + p.z.ToString("F3") +
                (latestState != null
                    ? "  scene=" + latestState.sceneName +
                      "  syncedSpd=" + latestState.speed.ToString("F2")
                    : "") +
                liveInfo + recvInfo);
        }

        // ── Lookup helpers ─────────────────────────────────────────────────────
        private static Animator FindAnimator(Transform visualRoot, Transform puppetRoot)
        {
            // PREFER Person's own animator explicitly, by name — same fix as
            // LocalPlayerSampler.FindBestAnimator on the sender side. A plain
            // GetComponentInChildren<Animator>(true) search across the WHOLE
            // cloned hierarchy can return ANY Animator it finds first — if the
            // sender also has a custom model loaded on themselves, the clone
            // contains BOTH Person's real, controlled locomotion animator AND
            // a "model(Clone)" sibling's animator, and which one comes back
            // depends on sibling order, not correctness. Person is where the
            // base game's real locomotion parameters (Forward/Right) live.
            if (visualRoot != null)
            {
                Transform personNode = visualRoot.name == "Person"
                                        ? visualRoot
                                        : visualRoot.Find("Person");
                if (personNode != null)
                {
                    Animator personAnim = personNode.GetComponentInChildren<Animator>(true);
                    if (personAnim != null && personAnim.runtimeAnimatorController != null)
                        return personAnim;
                }
            }

            // Fallback: generic search (custom-model-only clones have no
            // "Person" node at all — whatever Animator model(Clone) has, if
            // any, is the only option there; known separately broken/deferred).
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
