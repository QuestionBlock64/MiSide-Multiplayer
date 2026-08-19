using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Makes item pickups a relay-approved operation. The first player to click a
    /// pickup owns the world item; other clients deactivate their local copy.
    /// Key items are then mirrored through MiSide's own GameController methods,
    /// keeping normal quest checks and UI events intact on every client.
    /// </summary>
    internal sealed class InventorySharingController : IDisposable
    {
        private const float PendingTimeoutSeconds = 4f;

        private static InventorySharingController activeController;

        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly Harmony harmony;
        private readonly Dictionary<string, Component> pendingPickups = new Dictionary<string, Component>();
        private readonly Dictionary<string, float> pendingPickupExpiry = new Dictionary<string, float>();
        private readonly HashSet<string> claimedPickups = new HashSet<string>();
        private readonly HashSet<string> knownKeyItems = new HashSet<string>();
        private readonly HashSet<string> pendingConsumes = new HashSet<string>();
        private readonly HashSet<string> consumedKeyItems = new HashSet<string>();

        private string lastSceneName;
        private string allowedPickupPath;
        private bool applyingRemoteKeyChange;
        private bool disposed;

        public InventorySharingController(RpcDispatcher dispatcher, string localPlayerId)
        {
            this.dispatcher = dispatcher;
            this.localPlayerId = localPlayerId ?? string.Empty;
            harmony = new Harmony("com.miside.multiplayer.inventorysharing");

            if (dispatcher != null)
            {
                dispatcher.InventoryClaimResultReceived += OnClaimResult;
                dispatcher.InventoryKeyAddedReceived += OnKeyAddedReceived;
                dispatcher.InventoryConsumeResultReceived += OnConsumeResult;
                dispatcher.InventorySnapshotReceived += OnSnapshotReceived;
            }

            activeController = this;
            InstallHooks();
        }

        public void Tick()
        {
            if (disposed) return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (!string.Equals(sceneName, lastSceneName, StringComparison.Ordinal))
            {
                lastSceneName = sceneName;
                pendingPickups.Clear();
                pendingPickupExpiry.Clear();
                claimedPickups.Clear();
                knownKeyItems.Clear();
                pendingConsumes.Clear();
                consumedKeyItems.Clear();
            }

            if (pendingPickupExpiry.Count == 0) return;

            List<string> expired = null;
            foreach (KeyValuePair<string, float> pair in pendingPickupExpiry)
            {
                if (Time.unscaledTime < pair.Value) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
            }

            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                pendingPickups.Remove(expired[i]);
                pendingPickupExpiry.Remove(expired[i]);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (dispatcher != null)
            {
                dispatcher.InventoryClaimResultReceived -= OnClaimResult;
                dispatcher.InventoryKeyAddedReceived -= OnKeyAddedReceived;
                dispatcher.InventoryConsumeResultReceived -= OnConsumeResult;
                dispatcher.InventorySnapshotReceived -= OnSnapshotReceived;
            }

            if (ReferenceEquals(activeController, this))
                activeController = null;

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing patch cleanup failed: " + ex.Message);
            }
        }

        private void InstallHooks()
        {
            MethodInfo click = FindMethod("ObjectInteractive", "Click", Type.EmptyTypes);
            MethodInfo keyAdd = FindMethod("GameController", "AddKeyItem", new[] { typeof(GameObject) });
            MethodInfo keyRemove = FindMethod("GameController", "RemoveKeyItem", new[] { typeof(GameObject) });
            MethodInfo clickPrefix = GetPatchMethod(nameof(ClickPrefix));
            MethodInfo keyAddPostfix = GetPatchMethod(nameof(KeyAddPostfix));
            MethodInfo keyRemovePrefix = GetPatchMethod(nameof(KeyRemovePrefix));

            int installed = 0;
            try
            {
                if (click != null)
                {
                    harmony.Patch(click, prefix: new HarmonyMethod(clickPrefix));
                    installed++;
                }
                else DiagnosticLog.Warning("Inventory sharing could not find ObjectInteractive.Click.");

                if (keyAdd != null)
                {
                    harmony.Patch(keyAdd, postfix: new HarmonyMethod(keyAddPostfix));
                    installed++;
                }
                else DiagnosticLog.Warning("Inventory sharing could not find GameController.AddKeyItem.");

                if (keyRemove != null)
                {
                    harmony.Patch(keyRemove, prefix: new HarmonyMethod(keyRemovePrefix));
                    installed++;
                }
                else DiagnosticLog.Warning("Inventory sharing could not find GameController.RemoveKeyItem.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing could not install a hook: " + ex.Message);
            }

            DiagnosticLog.Info("Inventory sharing prepared " + installed + " hook(s).");
        }

        private static bool ClickPrefix(Component __instance)
        {
            InventorySharingController controller = activeController;
            return controller == null || controller.ShouldRunPickupClick(__instance);
        }

        private static void KeyAddPostfix(Component __instance, GameObject _item)
        {
            InventorySharingController controller = activeController;
            if (controller != null)
                controller.OnLocalKeyAdded(_item);
        }

        private static bool KeyRemovePrefix(Component __instance, GameObject _item)
        {
            InventorySharingController controller = activeController;
            return controller == null || controller.ShouldRunKeyRemove(_item);
        }

        private bool ShouldRunPickupClick(Component interactive)
        {
            if (disposed || applyingRemoteKeyChange || !IsGameplayScene() || !IsPickup(interactive))
                return true;

            string itemPath = GetPath(interactive == null ? null : interactive.gameObject);
            if (string.IsNullOrEmpty(itemPath)) return true;

            if (string.Equals(allowedPickupPath, itemPath, StringComparison.Ordinal))
                return true;

            if (claimedPickups.Contains(itemPath))
            {
                DeactivateRemotePickup(interactive.gameObject);
                return false;
            }

            if (pendingPickups.ContainsKey(itemPath))
                return false;

            pendingPickups[itemPath] = interactive;
            pendingPickupExpiry[itemPath] = Time.unscaledTime + PendingTimeoutSeconds;
            dispatcher.SendInventoryClaimRequest(new InventoryClaimRequest
            {
                sceneName = SceneManager.GetActiveScene().name,
                itemPath = itemPath
            });
            return false;
        }

        private bool ShouldRunKeyRemove(GameObject item)
        {
            if (disposed || applyingRemoteKeyChange || !IsGameplayScene())
                return true;

            string itemPath = GetPath(item);
            if (string.IsNullOrEmpty(itemPath)) return true;
            if (consumedKeyItems.Contains(itemPath) || pendingConsumes.Contains(itemPath))
                return false;

            pendingConsumes.Add(itemPath);
            dispatcher.SendInventoryConsumeRequest(new InventoryConsumeRequest
            {
                sceneName = SceneManager.GetActiveScene().name,
                itemPath = itemPath
            });
            return false;
        }

        private void OnClaimResult(InventoryClaimResult result)
        {
            if (disposed || result == null || !IsCurrentScene(result.sceneName) ||
                string.IsNullOrEmpty(result.itemPath))
                return;

            Component interactive;
            pendingPickups.TryGetValue(result.itemPath, out interactive);
            pendingPickups.Remove(result.itemPath);
            pendingPickupExpiry.Remove(result.itemPath);

            if (!result.approved)
            {
                claimedPickups.Add(result.itemPath);
                if (interactive != null) DeactivateRemotePickup(interactive.gameObject);
                return;
            }

            claimedPickups.Add(result.itemPath);
            if (string.Equals(result.ownerId, localPlayerId, StringComparison.Ordinal))
            {
                if (interactive == null)
                    interactive = FindPickupByPath(result.itemPath);
                RunApprovedPickup(interactive, result.itemPath);
            }
            else if (interactive != null)
            {
                DeactivateRemotePickup(interactive.gameObject);
            }
            else
            {
                GameObject item = FindByPath(result.itemPath);
                if (item != null) DeactivateRemotePickup(item);
            }
        }

        private void OnLocalKeyAdded(GameObject item)
        {
            if (disposed || applyingRemoteKeyChange || item == null || !IsGameplayScene())
                return;

            string itemPath = GetPath(item);
            if (string.IsNullOrEmpty(itemPath)) return;

            knownKeyItems.Add(itemPath);
            dispatcher.SendInventoryKeyAdded(new InventoryKeyChange
            {
                sceneName = SceneManager.GetActiveScene().name,
                itemPath = itemPath,
                ownerId = localPlayerId
            });
        }

        private void OnKeyAddedReceived(InventoryKeyChange change)
        {
            if (disposed || change == null || !IsCurrentScene(change.sceneName) ||
                string.IsNullOrEmpty(change.itemPath))
                return;

            if (consumedKeyItems.Contains(change.itemPath)) return;
            ApplyKeyAdd(change.itemPath);
        }

        private void OnConsumeResult(InventoryConsumeResult result)
        {
            if (disposed || result == null || !IsCurrentScene(result.sceneName) ||
                string.IsNullOrEmpty(result.itemPath))
                return;

            pendingConsumes.Remove(result.itemPath);
            if (!result.approved) return;

            consumedKeyItems.Add(result.itemPath);
            ApplyKeyRemove(result.itemPath);
        }

        private void OnSnapshotReceived(InventorySnapshot snapshot)
        {
            if (disposed || snapshot == null || !IsCurrentScene(snapshot.sceneName) ||
                snapshot.items == null)
                return;

            for (int i = 0; i < snapshot.items.Count; i++)
            {
                InventorySnapshotItem item = snapshot.items[i];
                if (item == null || string.IsNullOrEmpty(item.itemPath)) continue;

                claimedPickups.Add(item.itemPath);
                GameObject worldItem = FindByPath(item.itemPath);
                if (worldItem != null) DeactivateRemotePickup(worldItem);

                if (item.consumed)
                {
                    consumedKeyItems.Add(item.itemPath);
                    ApplyKeyRemove(item.itemPath);
                }
                else if (item.hasKey)
                {
                    ApplyKeyAdd(item.itemPath);
                }
            }
        }

        private void RunApprovedPickup(Component interactive, string itemPath)
        {
            if (interactive == null)
            {
                DiagnosticLog.Warning("Inventory sharing could not find approved pickup '" + itemPath + "'.");
                return;
            }

            MethodInfo click = FindMethod("ObjectInteractive", "Click", Type.EmptyTypes);
            if (click == null)
            {
                DiagnosticLog.Warning("Inventory sharing lost ObjectInteractive.Click.");
                return;
            }

            allowedPickupPath = itemPath;
            try
            {
                click.Invoke(interactive, null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing failed to run approved pickup: " + ex.Message);
            }
            finally
            {
                allowedPickupPath = null;
            }
        }

        private void ApplyKeyAdd(string itemPath)
        {
            if (knownKeyItems.Contains(itemPath)) return;

            GameObject item = FindByPath(itemPath);
            Component gameController = FindGameController();
            MethodInfo addKeyItem = FindMethod("GameController", "AddKeyItem", new[] { typeof(GameObject) });
            if (item == null || gameController == null || addKeyItem == null) return;

            applyingRemoteKeyChange = true;
            try
            {
                addKeyItem.Invoke(gameController, new object[] { item });
                knownKeyItems.Add(itemPath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing failed to add shared key: " + ex.Message);
            }
            finally
            {
                applyingRemoteKeyChange = false;
            }
        }

        private void ApplyKeyRemove(string itemPath)
        {
            GameObject item = FindByPath(itemPath);
            Component gameController = FindGameController();
            MethodInfo removeKeyItem = FindMethod("GameController", "RemoveKeyItem", new[] { typeof(GameObject) });
            if (item == null || gameController == null || removeKeyItem == null) return;

            applyingRemoteKeyChange = true;
            try
            {
                removeKeyItem.Invoke(gameController, new object[] { item });
                knownKeyItems.Remove(itemPath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing failed to remove shared key: " + ex.Message);
            }
            finally
            {
                applyingRemoteKeyChange = false;
            }
        }

        private static bool IsPickup(Component interactive)
        {
            try
            {
                return interactive != null && interactive.GetComponent("ObjectInteractiveItemTake") != null;
            }
            catch
            {
                return false;
            }
        }

        private static void DeactivateRemotePickup(GameObject item)
        {
            if (item == null) return;
            try
            {
                item.SetActive(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing could not hide shared pickup: " + ex.Message);
            }
        }

        private static Component FindPickupByPath(string itemPath)
        {
            GameObject item = FindByPath(itemPath);
            return item == null ? null : item.GetComponent("ObjectInteractive");
        }

        private static Component FindGameController()
        {
            try
            {
                GameObject controller = GameObject.FindWithTag("GameController");
                if (controller == null) controller = GameObject.Find("GameController");
                return controller == null ? null : controller.GetComponent("GameController");
            }
            catch
            {
                return null;
            }
        }

        private static string GetPath(GameObject item)
        {
            return item == null ? null : LocalPlayerLocator.GetPath(item.transform);
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r] == null || roots[r].name != parts[0]) continue;

                Transform current = roots[r].transform;
                for (int i = 1; i < parts.Length && current != null; i++)
                    current = current.Find(parts[i]);

                if (current != null) return current.gameObject;
            }

            return null;
        }

        private bool IsCurrentScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) &&
                   string.Equals(sceneName, SceneManager.GetActiveScene().name,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGameplayScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            return !string.IsNullOrEmpty(sceneName) &&
                   sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0 &&
                   sceneName.IndexOf("loading", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static MethodInfo GetPatchMethod(string name)
        {
            return typeof(InventorySharingController).GetMethod(
                name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static MethodInfo FindMethod(string typeName, string methodName, Type[] parameters)
        {
            Type type = FindGameType(typeName);
            return type == null ? null : type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameters,
                null);
        }

        private static Type FindGameType(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Assembly-CSharp") continue;
                Type type = assembly.GetType(typeName, false);
                if (type != null) return type;
            }

            return null;
        }
    }
}
