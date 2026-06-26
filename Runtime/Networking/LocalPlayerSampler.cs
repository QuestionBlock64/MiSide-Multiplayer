using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class LocalPlayerSampler
    {
        private RpcDispatcher rpcDispatcher;
        private NetworkManager networkManager;
        private string localPlayerId;
        private string displayName;
        private float sendRate = 20f;
        private float heartbeatSeconds = 1f;
        private bool emitSnapshots = true;
        private float nextSendTime;
        private float nextHeartbeatTime;
        private float nextErrorLogTime;
        private int tick;
        private bool loggedFirstSnapshot;
        private bool hasLastPosition;
        private Vector3 lastPosition;
        private bool hasLastSentState;
        private RemotePlayerState lastSentState;

        public void Configure(
            RpcDispatcher dispatcher,
            NetworkManager manager,
            string configuredLocalPlayerId,
            string configuredDisplayName,
            float configuredSendRate,
            float configuredHeartbeatSeconds,
            bool shouldEmitSnapshots)
        {
            rpcDispatcher = dispatcher;
            networkManager = manager;
            localPlayerId = configuredLocalPlayerId;
            displayName = configuredDisplayName;
            sendRate = Mathf.Max(1f, configuredSendRate);
            heartbeatSeconds = Mathf.Max(0.25f, configuredHeartbeatSeconds);
            emitSnapshots = shouldEmitSnapshots;
        }

        public void Tick()
        {
            if (!emitSnapshots || rpcDispatcher == null || networkManager == null)
                return;

            if (Time.unscaledTime < nextSendTime)
                return;

            nextSendTime = Time.unscaledTime + 1f / sendRate;

            if (!networkManager.TryBindLocalPlayer())
                return;

            try
            {
                RemotePlayerState state = BuildState(networkManager.LocalPlayerRoot);
                bool isHeartbeatDue = Time.unscaledTime >= nextHeartbeatTime;
                if (!HasMeaningfulChange(state, lastSentState) && !isHeartbeatDue)
                    return;

                lastSentState = state;
                hasLastSentState = true;
                nextHeartbeatTime = Time.unscaledTime + heartbeatSeconds;
                rpcDispatcher.SendState(state);

                if (!loggedFirstSnapshot)
                {
                    loggedFirstSnapshot = true;
                    DiagnosticLog.Info(
                        "Sending local player snapshots as '" +
                        localPlayerId +
                        "' in scene '" +
                        state.sceneName +
                        "'.");
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Warning("Local player sampling failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private RemotePlayerState BuildState(Transform localRoot)
        {
            RemotePlayerState state = new RemotePlayerState();
            state.playerId = localPlayerId;
            state.displayName = displayName;
            state.sceneName = SceneManager.GetActiveScene().name;
            state.position = NetVector3.FromUnity(localRoot.position);
            state.rotation = NetQuaternion.FromUnity(localRoot.rotation);

            Vector3 velocity = CalculateVelocity(localRoot.position);
            state.velocity = NetVector3.FromUnity(velocity);
            state.speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            state.isVisible = true;
            state.tick = tick++;

            Animator animator = localRoot.GetComponentInChildren<Animator>(true);
            if (animator != null)
                state.action = ReadCurrentActionName(animator);

            bool grounded;
            if (LocalPlayerLocator.TryReadGrounded(localRoot, out grounded))
                state.isGrounded = grounded;

            if (animator != null)
                FillAnimatorParameters(animator, state);

            return state;
        }

        private static string ReadCurrentActionName(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return null;

            try
            {
                AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
                for (int i = 0; i < clips.Length; i++)
                {
                    AnimationClip clip = clips[i];
                    if (clip != null && stateInfo.IsName(clip.name))
                        return clip.name;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private Vector3 CalculateVelocity(Vector3 currentPosition)
        {
            if (!hasLastPosition)
            {
                hasLastPosition = true;
                lastPosition = currentPosition;
                return Vector3.zero;
            }

            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 velocity = (currentPosition - lastPosition) / deltaTime;
            lastPosition = currentPosition;
            return velocity;
        }

        private static void FillAnimatorParameters(Animator animator, RemotePlayerState state)
        {
            state.floatParameters = new AnimatorFloatParam[0];
            state.boolParameters = new AnimatorBoolParam[0];
            state.intParameters = new AnimatorIntParam[0];

            try
            {
                int layerCount = Mathf.Min(animator.layerCount, 6);
                state.blendWeights = new float[layerCount];
                for (int i = 0; i < layerCount; i++)
                    state.blendWeights[i] = animator.GetLayerWeight(i);
            }
            catch (Exception)
            {
                state.blendWeights = new float[0];
            }
        }

        private bool HasMeaningfulChange(RemotePlayerState current, RemotePlayerState previous)
        {
            if (!hasLastSentState || previous == null)
                return true;

            if (Vector3.Distance(current.position.ToUnity(), previous.position.ToUnity()) > 0.0025f)
                return true;

            if (Quaternion.Dot(current.rotation.ToUnity(), previous.rotation.ToUnity()) < 0.9995f)
                return true;

            if (Mathf.Abs(current.speed - previous.speed) > 0.01f)
                return true;

            if (current.isGrounded != previous.isGrounded)
                return true;

            if (current.action != previous.action)
                return true;

            return !AnimatorParametersEqual(current, previous);
        }

        private static bool AnimatorParametersEqual(RemotePlayerState current, RemotePlayerState previous)
        {
            if (!FloatParamsEqual(current.floatParameters, previous.floatParameters))
                return false;

            if (!BoolParamsEqual(current.boolParameters, previous.boolParameters))
                return false;

            if (!IntParamsEqual(current.intParameters, previous.intParameters))
                return false;

            return true;
        }

        private static bool FloatParamsEqual(AnimatorFloatParam[] left, AnimatorFloatParam[] right)
        {
            if ((left == null) != (right == null))
                return false;

            if (left == null)
                return true;

            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].name != right[i].name || Mathf.Abs(left[i].value - right[i].value) > 0.01f)
                    return false;
            }

            return true;
        }

        private static bool BoolParamsEqual(AnimatorBoolParam[] left, AnimatorBoolParam[] right)
        {
            if ((left == null) != (right == null))
                return false;

            if (left == null)
                return true;

            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].name != right[i].name || left[i].value != right[i].value)
                    return false;
            }

            return true;
        }

        private static bool IntParamsEqual(AnimatorIntParam[] left, AnimatorIntParam[] right)
        {
            if ((left == null) != (right == null))
                return false;

            if (left == null)
                return true;

            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].name != right[i].name || left[i].value != right[i].value)
                    return false;
            }

            return true;
        }
    }
}
