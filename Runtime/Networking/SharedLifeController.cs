using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>Relays real in-game death sequences to players in the same scene.</summary>
    internal sealed class SharedLifeController : IDisposable
    {
        private const float DuplicateSuppressionSeconds = 3f;

        private static readonly DeathHook[] Hooks =
        {
            new DeathHook("Shooter_Main", "PlayerDeath"),
            new DeathHook("Location10_ManekenChekpoint", "PlayerDeath"),
            new DeathHook("Location11_Lift", "DeathPlayer"),
            new DeathHook("Location20_Arena", "PlayerDeath"),
            new DeathHook("Location20_RunCorridor", "KillPlayerStart"),
            new DeathHook("Mob_Maneken", "StartKillPlayer"),
            new DeathHook("Location6_MitaKiller", "Kill"),
            new DeathHook("TetrisGame", "Death"),
            new DeathHook("Location19_Game4", "Death")
        };

        private static SharedLifeController activeController;

        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly Harmony harmony;

        private bool applyingRemoteDeath;
        private bool disposed;
        private float suppressLocalDeathUntil;

        public SharedLifeController(RpcDispatcher dispatcher, string localPlayerId)
        {
            this.dispatcher = dispatcher;
            this.localPlayerId = localPlayerId ?? string.Empty;
            harmony = new Harmony("com.miside.multiplayer.sharedlife");

            if (dispatcher != null)
                dispatcher.DeathLinkReceived += OnDeathLinkReceived;

            activeController = this;
            InstallHooks();
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            if (dispatcher != null)
                dispatcher.DeathLinkReceived -= OnDeathLinkReceived;
            if (ReferenceEquals(activeController, this))
                activeController = null;

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Shared life patch cleanup failed: " + ex.Message);
            }
        }

        private void InstallHooks()
        {
            MethodInfo postfix = typeof(SharedLifeController).GetMethod(
                nameof(DeathMethodPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            int installed = 0;

            foreach (DeathHook hook in Hooks)
            {
                MethodInfo method = FindMethod(hook);
                if (method == null)
                {
                    DiagnosticLog.Warning("Shared life could not hook " + hook.Id + ".");
                    continue;
                }

                try
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    installed++;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("Shared life could not patch " + hook.Id + ": " + ex.Message);
                }
            }

            DiagnosticLog.Info("Shared life prepared " + installed + " death hook(s).");
        }

        private static void DeathMethodPostfix(MethodBase __originalMethod)
        {
            SharedLifeController controller = activeController;
            if (controller != null)
                controller.OnLocalDeath(__originalMethod);
        }

        private void OnLocalDeath(MethodBase method)
        {
            if (disposed || applyingRemoteDeath || Time.unscaledTime < suppressLocalDeathUntil)
                return;

            DeathHook hook = FindHook(method);
            if (hook == null || !IsGameplayScene())
                return;

            suppressLocalDeathUntil = Time.unscaledTime + DuplicateSuppressionSeconds;
            dispatcher.SendDeathLink(new SharedLifeMessage
            {
                senderId = localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                deathSource = hook.Id
            });
        }

        private void OnDeathLinkReceived(SharedLifeMessage message)
        {
            if (disposed || message == null ||
                string.Equals(message.senderId, localPlayerId, StringComparison.Ordinal))
                return;

            if (!string.Equals(message.sceneName, SceneManager.GetActiveScene().name,
                StringComparison.OrdinalIgnoreCase))
                return;

            DeathHook hook = FindHook(message.deathSource);
            MethodInfo method = FindMethod(hook);
            Type type = hook == null ? null : FindGameType(hook.TypeName);
            UnityEngine.Object target = FindActiveComponent(type);
            if (method == null || target == null)
            {
                DiagnosticLog.Warning("Shared life could not apply " + (message.deathSource ?? "unknown") + ".");
                return;
            }

            applyingRemoteDeath = true;
            suppressLocalDeathUntil = Time.unscaledTime + DuplicateSuppressionSeconds;
            try
            {
                method.Invoke(target, null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Shared life failed to apply " + hook.Id + ": " + ex.Message);
            }
            finally
            {
                applyingRemoteDeath = false;
            }
        }

        private static UnityEngine.Object FindActiveComponent(Type type)
        {
            if (type == null)
                return null;

            foreach (MonoBehaviour candidate in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (candidate != null && candidate.GetType() == type)
                    return candidate;
            }

            return null;
        }

        private static DeathHook FindHook(MethodBase method)
        {
            if (method == null || method.DeclaringType == null)
                return null;

            foreach (DeathHook hook in Hooks)
            {
                if (hook.TypeName == method.DeclaringType.Name && hook.MethodName == method.Name)
                    return hook;
            }

            return null;
        }

        private static DeathHook FindHook(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            foreach (DeathHook hook in Hooks)
            {
                if (hook.Id == id)
                    return hook;
            }

            return null;
        }

        private static MethodInfo FindMethod(DeathHook hook)
        {
            if (hook == null)
                return null;

            Type type = FindGameType(hook.TypeName);
            return type == null ? null : type.GetMethod(
                hook.MethodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
        }

        private static Type FindGameType(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Assembly-CSharp")
                    continue;

                Type type = assembly.GetType(typeName, false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static bool IsGameplayScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            return !string.IsNullOrEmpty(sceneName) &&
                   sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0 &&
                   sceneName.IndexOf("loading", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private sealed class DeathHook
        {
            public readonly string TypeName;
            public readonly string MethodName;
            public readonly string Id;

            public DeathHook(string typeName, string methodName)
            {
                TypeName = typeName;
                MethodName = methodName;
                Id = typeName + "." + methodName;
            }
        }
    }
}
