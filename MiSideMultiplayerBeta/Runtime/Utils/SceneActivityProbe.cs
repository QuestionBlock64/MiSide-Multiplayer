using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Change-driven, high-detail recorder for the StartSecret scene. The
    /// detailed stream goes to a dedicated file so observing an animated scene
    /// does not flood the BepInEx console or stall the game on every change.
    /// </summary>
    internal static class SceneActivityProbe
    {
        private const string TargetSceneName = "Scene 4 - StartSecret";
        // Full scene reflection is expensive in this 2k+ object scene. Direct
        // SetActive calls are traced synchronously; the periodic scan fills in
        // hierarchy/animation changes that do not pass through that API.
        private const float ScanIntervalSeconds = 0.5f;
        private const float TransformLogIntervalSeconds = 1.0f;
        private const float FlushIntervalSeconds = 0.5f;
        private const float PositionEpsilon = 0.001f;
        private const float RotationEpsilonDegrees = 0.1f;
        private const float ScalarEpsilon = 0.001f;

        private sealed class ComponentSnapshot
        {
            public string TypeName;
            public string Signature;
        }

        private sealed class ObjectSnapshot
        {
            public string Name;
            public string Path;
            public int ParentId;
            public int SiblingIndex;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public string Tag;
            public int Layer;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public float LastTransformLogTime;
            public readonly Dictionary<int, ComponentSnapshot> Components =
                new Dictionary<int, ComponentSnapshot>();
        }

        private static readonly Dictionary<int, ObjectSnapshot> Objects =
            new Dictionary<int, ObjectSnapshot>();
        private static readonly Dictionary<Type, FieldInfo[]> ScalarFields =
            new Dictionary<Type, FieldInfo[]>();
        private static readonly HashSet<int> SeenObjects = new HashSet<int>();
        private static readonly HashSet<int> SeenComponents = new HashSet<int>();
        private static readonly List<int> RemovedIds = new List<int>();
        private static readonly List<Transform> Stack = new List<Transform>(512);
        private static readonly StringBuilder Changes = new StringBuilder(16384);

        private static StreamWriter _writer;
        private static string _filePath;
        private static int _sceneHandle = -1;
        private static float _nextScanTime;
        private static float _nextFlushTime;
        private static float _nextSummaryTime;
        private static long _totalChanges;
        private static int _scanCount;

        internal sealed class ActiveCallTrace
        {
            internal int ObjectId;
            internal string Path;
            internal string Caller;
            internal bool BeforeSelf;
            internal bool BeforeHierarchy;
            internal bool Requested;
            internal bool Logged;
        }

        internal static void Tick()
        {
            Scene targetScene = FindLoadedTargetScene();
            if (!targetScene.IsValid())
            {
                Stop("target scene unloaded");
                return;
            }

            if (_writer == null || _sceneHandle != targetScene.handle)
            {
                Stop("target scene instance changed");
                Start(targetScene);
            }

            if (_writer == null || Time.unscaledTime < _nextScanTime) return;
            _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;
            Scan(targetScene);
        }

        internal static void Stop(string reason)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine("END utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                                  " reason=" + reason + " scans=" + _scanCount +
                                  " changes=" + _totalChanges);
                _writer.Flush();
                _writer.Dispose();
            }
            catch { }
            _writer = null;
            _sceneHandle = -1;
            DiagnosticLog.Info("[SCENE-PROBE] stopped: " + reason + " (" + _filePath + ").");
            Objects.Clear();
            Stack.Clear();
            SeenObjects.Clear();
            SeenComponents.Clear();
        }

        internal static ActiveCallTrace BeginSetActiveTrace(GameObject gameObject, bool requested)
        {
            try
            {
                if (_writer == null || gameObject == null || !IsTargetScene(gameObject)) return null;
                bool beforeSelf = gameObject.activeSelf;
                if (beforeSelf == requested) return null;

                int id = gameObject.GetInstanceID();
                ObjectSnapshot snapshot;
                string path = Objects.TryGetValue(id, out snapshot) && snapshot != null
                    ? snapshot.Path
                    : MitaBoneSync.GetNetworkPath(gameObject.transform);
                return new ActiveCallTrace
                {
                    ObjectId = id,
                    Path = path,
                    Caller = FindSetActiveCaller(),
                    BeforeSelf = beforeSelf,
                    BeforeHierarchy = gameObject.activeInHierarchy,
                    Requested = requested
                };
            }
            catch { return null; }
        }

        internal static void RecordSetActiveBlocked(ActiveCallTrace trace, string reason)
        {
            if (trace == null || trace.Logged) return;
            trace.Logged = true;
            WriteEvent("SET-ACTIVE path=" + trace.Path + " activeSelf=" + trace.BeforeSelf +
                       "->" + trace.BeforeSelf + " activeInHierarchy=" + trace.BeforeHierarchy +
                       "->" + trace.BeforeHierarchy + " requested=" + trace.Requested +
                       " result=BLOCKED reason=" + Quote(reason) + " caller=" + trace.Caller);
        }

        internal static void CompleteSetActiveTrace(ActiveCallTrace trace, GameObject gameObject)
        {
            if (trace == null || trace.Logged) return;
            try
            {
                bool afterSelf = gameObject != null && gameObject.activeSelf;
                bool afterHierarchy = gameObject != null && gameObject.activeInHierarchy;
                string result = afterSelf == trace.Requested ? "applied" : "no-change-or-blocked";
                WriteEvent("SET-ACTIVE path=" + trace.Path + " activeSelf=" + trace.BeforeSelf +
                           "->" + afterSelf + " activeInHierarchy=" + trace.BeforeHierarchy +
                           "->" + afterHierarchy + " requested=" + trace.Requested +
                           " result=" + result + " caller=" + trace.Caller);
            }
            catch (Exception ex)
            {
                WriteEvent("SET-ACTIVE path=" + trace.Path + " requested=" + trace.Requested +
                           " result=read-failed error=" + ex.GetType().Name + " caller=" + trace.Caller);
            }
        }

        private static Scene FindLoadedTargetScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded &&
                    string.Equals(scene.name, TargetSceneName, StringComparison.Ordinal))
                    return scene;
            }
            return default(Scene);
        }

        private static void Start(Scene scene)
        {
            try
            {
                string outputDirectory = Path.Combine(Paths.BepInExRootPath, "LogOutput");
                Directory.CreateDirectory(outputDirectory);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                _filePath = Path.Combine(outputDirectory, "Scene4-StartSecret-Probe-" + stamp + ".log");
                _writer = new StreamWriter(_filePath, false, Encoding.UTF8, 65536);
                _sceneHandle = scene.handle;
                _nextScanTime = 0f;
                _nextFlushTime = 0f;
                _nextSummaryTime = Time.unscaledTime + 5f;
                _totalChanges = 0;
                _scanCount = 0;
                Objects.Clear();
                Changes.Length = 0;
                WriteHeader("START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                            " scene=" + scene.name + " handle=" + scene.handle +
                            " build=" + Application.version + " unity=" + Application.unityVersion +
                            " scanInterval=" + ScanIntervalSeconds.ToString("F3", CultureInfo.InvariantCulture));
                DiagnosticLog.Info("[SCENE-PROBE] recording all observed scene changes to " + _filePath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[SCENE-PROBE] could not start probe: " +
                                      ex.GetType().Name + ": " + ex.Message);
                Stop("probe start failed");
            }
        }

        private static void Scan(Scene scene)
        {
            try
            {
                SeenObjects.Clear();
                Stack.Clear();
                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = roots.Length - 1; i >= 0; i--)
                    if (roots[i] != null) Stack.Add(roots[i].transform);

                Changes.Length = 0;
                int scanChanges = 0;
                while (Stack.Count > 0)
                {
                    int last = Stack.Count - 1;
                    Transform transform = Stack[last];
                    Stack.RemoveAt(last);
                    if (transform == null) continue;

                    GameObject gameObject = transform.gameObject;
                    if (gameObject == null) continue;
                    int objectId = gameObject.GetInstanceID();
                    SeenObjects.Add(objectId);

                    ObjectSnapshot snapshot;
                    bool isNew = !Objects.TryGetValue(objectId, out snapshot) || snapshot == null;
                    if (isNew)
                    {
                        snapshot = new ObjectSnapshot();
                        Objects[objectId] = snapshot;
                    }

                    string objectName = SafeName(gameObject);
                    int parentId = transform.parent != null ? transform.parent.gameObject.GetInstanceID() : 0;
                    string parentPath = string.Empty;
                    if (parentId != 0)
                    {
                        ObjectSnapshot parentSnapshot;
                        if (Objects.TryGetValue(parentId, out parentSnapshot) && parentSnapshot != null)
                            parentPath = parentSnapshot.Path;
                    }
                    string objectPath = parentId == 0
                        ? scene.name + "/" + objectName + "#" + objectId
                        : parentPath + "/" + objectName + "#" + objectId;

                    bool activeSelf = gameObject.activeSelf;
                    bool activeInHierarchy = gameObject.activeInHierarchy;
                    string tag = SafeTag(gameObject);
                    int layer = gameObject.layer;
                    Vector3 position = transform.position;
                    Quaternion rotation = transform.rotation;
                    Vector3 localPosition = transform.localPosition;
                    Quaternion localRotation = transform.localRotation;
                    Vector3 localScale = transform.localScale;

                    if (isNew)
                    {
                        AddChange(ref scanChanges, "GO+ " + objectPath + " activeSelf=" + activeSelf +
                            " active=" + activeInHierarchy + " tag=" + tag + " layer=" + layer +
                            " world=" + Format(position) + " rot=" + Format(rotation) +
                            " local=" + Format(localPosition) + " localRot=" + Format(localRotation) +
                            " scale=" + Format(localScale));
                    }
                    else
                    {
                        string oldPath = snapshot.Path;
                        if (!string.Equals(snapshot.Name, objectName, StringComparison.Ordinal) ||
                            snapshot.ParentId != parentId || snapshot.SiblingIndex != transform.GetSiblingIndex() ||
                            !string.Equals(snapshot.Path, objectPath, StringComparison.Ordinal))
                            AddChange(ref scanChanges, "HIERARCHY " + oldPath + " -> " + objectPath +
                                " parentId=" + parentId + " sibling=" + transform.GetSiblingIndex());

                        if (snapshot.ActiveSelf != activeSelf || snapshot.ActiveInHierarchy != activeInHierarchy)
                            AddChange(ref scanChanges, "ACTIVE " + objectPath +
                                " activeSelf=" + snapshot.ActiveSelf + "->" + activeSelf +
                                " activeInHierarchy=" + snapshot.ActiveInHierarchy + "->" + activeInHierarchy);
                        if (!string.Equals(snapshot.Tag, tag, StringComparison.Ordinal) || snapshot.Layer != layer)
                            AddChange(ref scanChanges, "OBJECT-PROPERTIES " + objectPath + " tag=" + tag +
                                " layer=" + layer);

                        bool worldPositionChanged = Vector3.Distance(snapshot.Position, position) > PositionEpsilon;
                        bool worldRotationChanged = Quaternion.Angle(snapshot.Rotation, rotation) > RotationEpsilonDegrees;
                        bool localPositionChanged = Vector3.Distance(snapshot.LocalPosition, localPosition) > PositionEpsilon;
                        bool localRotationChanged = Quaternion.Angle(snapshot.LocalRotation, localRotation) > RotationEpsilonDegrees;
                        bool scaleChanged = Vector3.Distance(snapshot.LocalScale, localScale) > ScalarEpsilon;
                        if ((worldPositionChanged || worldRotationChanged || localPositionChanged ||
                             localRotationChanged || scaleChanged) &&
                            Time.unscaledTime - snapshot.LastTransformLogTime >= TransformLogIntervalSeconds)
                        {
                            AddChange(ref scanChanges, "TRANSFORM " + objectPath +
                                " world=" + Format(position) + " rot=" + Format(rotation) +
                                " local=" + Format(localPosition) + " localRot=" + Format(localRotation) +
                                " scale=" + Format(localScale));
                            snapshot.LastTransformLogTime = Time.unscaledTime;
                        }
                    }

                    snapshot.Name = objectName;
                    snapshot.Path = objectPath;
                    snapshot.ParentId = parentId;
                    snapshot.SiblingIndex = transform.GetSiblingIndex();
                    snapshot.ActiveSelf = activeSelf;
                    snapshot.ActiveInHierarchy = activeInHierarchy;
                    snapshot.Tag = tag;
                    snapshot.Layer = layer;
                    snapshot.Position = position;
                    snapshot.Rotation = rotation;
                    snapshot.LocalPosition = localPosition;
                    snapshot.LocalRotation = localRotation;
                    snapshot.LocalScale = localScale;

                    ScanComponents(gameObject, objectPath, snapshot, ref scanChanges);

                    for (int i = transform.childCount - 1; i >= 0; i--)
                    {
                        Transform child = transform.GetChild(i);
                        if (child != null) Stack.Add(child);
                    }
                }

                RemovedIds.Clear();
                foreach (KeyValuePair<int, ObjectSnapshot> pair in Objects)
                    if (!SeenObjects.Contains(pair.Key)) RemovedIds.Add(pair.Key);
                for (int i = 0; i < RemovedIds.Count; i++)
                {
                    ObjectSnapshot old = Objects[RemovedIds[i]];
                    AddChange(ref scanChanges, "GO- " + (old != null ? old.Path : "instance#" + RemovedIds[i]));
                    Objects.Remove(RemovedIds[i]);
                }

                _scanCount++;
                if (scanChanges > 0 && _writer != null)
                {
                    _writer.WriteLine("SCAN frame=" + Time.frameCount +
                                      " t=" + Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture) +
                                      " changes=" + scanChanges);
                    _writer.Write(Changes.ToString());
                }

                if (_writer != null && Time.unscaledTime >= _nextFlushTime)
                {
                    _writer.Flush();
                    _nextFlushTime = Time.unscaledTime + FlushIntervalSeconds;
                }
                if (Time.unscaledTime >= _nextSummaryTime)
                {
                    DiagnosticLog.Info("[SCENE-PROBE] scans=" + _scanCount + " objects=" + Objects.Count +
                                       " totalChanges=" + _totalChanges + " file=" + _filePath);
                    _nextSummaryTime = Time.unscaledTime + 5f;
                }
            }
            catch (Exception ex)
            {
                WriteHeader("PROBE-ERROR frame=" + Time.frameCount + " t=" +
                            Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture) + " " +
                            ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("[SCENE-PROBE] scan failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ScanComponents(GameObject gameObject, string objectPath,
                                           ObjectSnapshot objectSnapshot, ref int scanChanges)
        {
            SeenComponents.Clear();
            Component[] components = gameObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    AddChange(ref scanChanges, "COMPONENT-MISSING " + objectPath + " index=" + i);
                    continue;
                }

                int componentId = component.GetInstanceID();
                SeenComponents.Add(componentId);
                string typeName = component.GetType().FullName ?? component.GetType().Name;
                string signature;
                try { signature = DescribeComponent(component); }
                catch (Exception ex)
                {
                    signature = "<state-read-failed:" + ex.GetType().Name + ":" + ex.Message + ">";
                }
                ComponentSnapshot previous;
                if (!objectSnapshot.Components.TryGetValue(componentId, out previous) || previous == null)
                {
                    previous = new ComponentSnapshot();
                    objectSnapshot.Components[componentId] = previous;
                    AddChange(ref scanChanges, "COMP+ " + objectPath + " :: " + typeName +
                              " id=" + componentId + " state=" + signature);
                }
                else if (!string.Equals(previous.Signature, signature, StringComparison.Ordinal))
                {
                    AddChange(ref scanChanges, "COMP~ " + objectPath + " :: " + typeName +
                              " id=" + componentId + " state=" + signature);
                }
                previous.TypeName = typeName;
                previous.Signature = signature;
            }

            RemovedIds.Clear();
            foreach (KeyValuePair<int, ComponentSnapshot> pair in objectSnapshot.Components)
                if (!SeenComponents.Contains(pair.Key)) RemovedIds.Add(pair.Key);
            for (int i = 0; i < RemovedIds.Count; i++)
            {
                ComponentSnapshot old = objectSnapshot.Components[RemovedIds[i]];
                AddChange(ref scanChanges, "COMP- " + objectPath + " :: " +
                          (old != null ? old.TypeName : "component") + " id=" + RemovedIds[i]);
                objectSnapshot.Components.Remove(RemovedIds[i]);
            }
        }

        private static string DescribeComponent(Component component)
        {
            StringBuilder state = new StringBuilder(256);
            Behaviour behaviour = component as Behaviour;
            if (behaviour != null)
                state.Append("enabled=").Append(behaviour.enabled).Append(' ');

            Renderer renderer = component as Renderer;
            if (renderer != null)
            {
                state.Append("rendering=").Append(renderer.enabled)
                    .Append(" forceOff=").Append(renderer.forceRenderingOff);
                Material material = renderer.sharedMaterial;
                if (material != null) state.Append(" material=").Append(material.name);
                state.Append(' ');
            }

            Collider collider = component as Collider;
            if (collider != null)
                state.Append("collider enabled=").Append(collider.enabled)
                    .Append(" trigger=").Append(collider.isTrigger)
                    .Append(" center=").Append(Format(collider.bounds.center))
                    .Append(" bounds=").Append(Format(collider.bounds.size)).Append(' ');

            Animator animator = component as Animator;
            if (animator != null) AppendAnimatorState(animator, state);

            Animation animation = component as Animation;
            if (animation != null) AppendLegacyAnimationState(animation, state);

            AudioSource audio = component as AudioSource;
            if (audio != null)
                state.Append("audio clip=").Append(audio.clip != null ? audio.clip.name : "<none>")
                    .Append(" playing=").Append(audio.isPlaying)
                    .Append(" loop=").Append(audio.loop).Append(" mute=").Append(audio.mute)
                    .Append(" volume=").Append(Format(audio.volume))
                    .Append(" pitch=").Append(Format(audio.pitch)).Append(' ');

            ParticleSystem particles = component as ParticleSystem;
            if (particles != null)
                state.Append("particles playing=").Append(particles.isPlaying)
                    .Append(" emitting=").Append(particles.isEmitting)
                    .Append(" count=").Append(particles.particleCount).Append(' ');

            Rigidbody body = component as Rigidbody;
            if (body != null)
                state.Append("rigidbody kinematic=").Append(body.isKinematic)
                    .Append(" velocity=").Append(Format(body.velocity))
                    .Append(" angular=").Append(Format(body.angularVelocity)).Append(' ');

            NavMeshAgent agent = component as NavMeshAgent;
            if (agent != null)
            {
                try
                {
                    state.Append("agent onNavMesh=").Append(agent.isOnNavMesh)
                        .Append(" stopped=").Append(agent.isStopped)
                        .Append(" hasPath=").Append(agent.hasPath)
                        .Append(" pathStatus=").Append(agent.pathStatus)
                        .Append(" destination=").Append(Format(agent.destination))
                        .Append(" velocity=").Append(Format(agent.velocity))
                        .Append(" next=").Append(Format(agent.nextPosition)).Append(' ');
                }
                catch (Exception ex) { state.Append("agent=<").Append(ex.GetType().Name).Append("> "); }
            }

            CanvasGroup canvasGroup = component as CanvasGroup;
            if (canvasGroup != null)
                state.Append("canvas alpha=").Append(Format(canvasGroup.alpha))
                    .Append(" interactable=").Append(canvasGroup.interactable)
                    .Append(" blocksRaycasts=").Append(canvasGroup.blocksRaycasts).Append(' ');

            Text text = component as Text;
            if (text != null)
                state.Append("text=").Append(Quote(text.text)).Append(" color=")
                    .Append(Format(text.color)).Append(' ');

            Light light = component as Light;
            if (light != null)
                state.Append("light enabled=").Append(light.enabled)
                    .Append(" intensity=").Append(Format(light.intensity))
                    .Append(" color=").Append(Format(light.color)).Append(' ');

            Camera camera = component as Camera;
            if (camera != null)
                state.Append("camera enabled=").Append(camera.enabled)
                    .Append(" fov=").Append(Format(camera.fieldOfView))
                    .Append(" ortho=").Append(Format(camera.orthographicSize)).Append(' ');

            MonoBehaviour script = component as MonoBehaviour;
            if (script != null) AppendScalarScriptFields(script, state);
            return state.ToString();
        }

        private static void AppendAnimatorState(Animator animator, StringBuilder state)
        {
            state.Append("animator controller=")
                .Append(animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.name : "<none>")
                .Append(" speed=").Append(Format(animator.speed))
                .Append(" rootMotion=").Append(animator.applyRootMotion)
                .Append(" layers=").Append(animator.layerCount);
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                try
                {
                    AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
                    state.Append(" L").Append(layer).Append("=")
                        .Append(current.fullPathHash).Append('@')
                        .Append((Mathf.Round(current.normalizedTime * 10f) / 10f)
                            .ToString("F1", CultureInfo.InvariantCulture));
                    if (animator.IsInTransition(layer))
                    {
                        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
                        state.Append("->").Append(next.fullPathHash).Append('@')
                            .Append((Mathf.Round(next.normalizedTime * 10f) / 10f)
                                .ToString("F1", CultureInfo.InvariantCulture));
                    }
                    var clips = animator.GetCurrentAnimatorClipInfo(layer);
                    for (int i = 0; i < clips.Length; i++)
                        if (clips[i].clip != null)
                            state.Append(" clip[").Append(clips[i].clip.name).Append(':')
                                .Append(Format(clips[i].weight)).Append(']');
                }
                catch { state.Append(" L").Append(layer).Append("=<unavailable>"); }
            }

            try
            {
                int parameterCount = animator.parameterCount;
                for (int i = 0; i < parameterCount; i++)
                {
                    AnimatorControllerParameter parameter = animator.GetParameter(i);
                    if (parameter == null) continue;
                    state.Append(" p[").Append(parameter.name).Append('=').Append(parameter.type).Append(':');
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            state.Append(animator.GetBool(parameter.name));
                            break;
                        case AnimatorControllerParameterType.Int:
                            state.Append(animator.GetInteger(parameter.name));
                            break;
                        case AnimatorControllerParameterType.Float:
                            state.Append((Mathf.Round(animator.GetFloat(parameter.name) * 100f) / 100f)
                                .ToString("F2", CultureInfo.InvariantCulture));
                            break;
                        default:
                            state.Append("trigger");
                            break;
                    }
                    state.Append(']');
                }
            }
            catch { state.Append(" parameters=<unavailable>"); }
            state.Append(' ');
        }

        private static void AppendLegacyAnimationState(Animation animation, StringBuilder state)
        {
            try
            {
                state.Append("legacyAnimation=").Append(animation.enabled);
                AnimationState[] states = new AnimationState[animation.GetClipCount()];
                int index = 0;
                foreach (AnimationState animationState in animation)
                {
                    if (index >= states.Length) break;
                    states[index++] = animationState;
                }
                for (int i = 0; i < index; i++)
                {
                    AnimationState animationState = states[i];
                    if (animationState != null && animationState.enabled)
                        state.Append(" clip[").Append(animationState.name).Append('@')
                            .Append((Mathf.Round(animationState.normalizedTime * 10f) / 10f)
                                .ToString("F1", CultureInfo.InvariantCulture)).Append(']');
                }
                state.Append(' ');
            }
            catch { state.Append("legacyAnimation=<unavailable> "); }
        }

        private static void AppendScalarScriptFields(MonoBehaviour script, StringBuilder state)
        {
            FieldInfo[] fields = GetScalarFields(script.GetType());
            if (fields.Length == 0) return;
            state.Append(" fields{");
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                try
                {
                    object value = field.GetValue(script);
                    state.Append(field.Name).Append('=').Append(FormatFieldValue(value)).Append(';');
                }
                catch { state.Append(field.Name).Append("=<unreadable>;"); }
            }
            state.Append("} ");
        }

        private static FieldInfo[] GetScalarFields(Type type)
        {
            FieldInfo[] cached;
            if (ScalarFields.TryGetValue(type, out cached)) return cached;
            List<FieldInfo> fields = new List<FieldInfo>();
            for (Type current = type; current != null && current != typeof(MonoBehaviour);
                 current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    FieldInfo field = declared[i];
                    if (field.IsStatic || field.IsLiteral || field.IsNotSerialized ||
                        !IsTrackableFieldType(field.FieldType)) continue;
                    fields.Add(field);
                }
            }
            cached = fields.ToArray();
            ScalarFields[type] = cached;
            return cached;
        }

        private static bool IsTrackableFieldType(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                   typeof(UnityEngine.Object).IsAssignableFrom(type) ||
                   type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                   type == typeof(Quaternion) || type == typeof(Color);
        }

        private static string FormatFieldValue(object value)
        {
            if (value == null) return "<null>";
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                if (unityObject == null) return "<destroyed>";
                try { return unityObject.name + "#" + unityObject.GetInstanceID(); }
                catch { return "<unity-object>"; }
            }
            if (value is float) return Format((float)value);
            if (value is double) return ((double)value).ToString("G6", CultureInfo.InvariantCulture);
            if (value is Vector2) return Format((Vector2)value);
            if (value is Vector3) return Format((Vector3)value);
            if (value is Vector4) return Format((Vector4)value);
            if (value is Quaternion) return Format((Quaternion)value);
            if (value is Color) return Format((Color)value);
            if (value is string) return Quote((string)value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static void AddChange(ref int scanChanges, string text)
        {
            if (scanChanges == 0)
                Changes.Append("  frame=").Append(Time.frameCount).Append(" t=")
                    .Append(Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
            Changes.Append("    ").AppendLine(text);
            scanChanges++;
            _totalChanges++;
        }

        private static void WriteEvent(string text)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine("EVENT frame=" + Time.frameCount + " t=" +
                                  Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture) +
                                  " " + text);
                _totalChanges++;
            }
            catch { }
        }

        private static bool IsTargetScene(GameObject gameObject)
        {
            return gameObject != null && gameObject.scene.IsValid() &&
                   string.Equals(gameObject.scene.name, TargetSceneName, StringComparison.Ordinal);
        }

        private static string FindSetActiveCaller()
        {
            try
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(2, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    MethodBase method = trace.GetFrame(i).GetMethod();
                    if (method == null) continue;
                    Type declaring = method.DeclaringType;
                    if (declaring == typeof(SceneActivityProbe) ||
                        declaring == typeof(WorldOccupancyGuard)) continue;
                    string typeName = declaring != null ? declaring.FullName : "<unknown-type>";
                    if (typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
                        typeName.StartsWith("System.Reflection.", StringComparison.Ordinal)) continue;
                    return typeName + "." + method.Name;
                }
            }
            catch { }
            return "<native-or-unavailable>";
        }

        private static void WriteHeader(string line)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch { }
        }

        private static string SafeName(GameObject gameObject)
        {
            try { return gameObject.name; }
            catch { return "<unnamed>"; }
        }

        private static string SafeTag(GameObject gameObject)
        {
            try { return gameObject.tag; }
            catch { return "<untagged>"; }
        }

        private static string Quote(string text)
        {
            if (text == null) return "<null>";
            return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r")
                .Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
        }

        private static string Format(float value) => value.ToString("G5", CultureInfo.InvariantCulture);
        private static string Format(Vector2 value) => "(" + Format(value.x) + "," + Format(value.y) + ")";
        private static string Format(Vector3 value) => "(" + Format(value.x) + "," + Format(value.y) + "," + Format(value.z) + ")";
        private static string Format(Vector4 value) => "(" + Format(value.x) + "," + Format(value.y) + "," + Format(value.z) + "," + Format(value.w) + ")";
        private static string Format(Quaternion value) => "(" + Format(value.x) + "," + Format(value.y) + "," + Format(value.z) + "," + Format(value.w) + ")";
        private static string Format(Color value) => "(" + Format(value.r) + "," + Format(value.g) + "," + Format(value.b) + "," + Format(value.a) + ")";
    }
}
