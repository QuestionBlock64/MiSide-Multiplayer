using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MiSideMultiplayer.Runtime.Lobby;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    internal sealed class InventorySharingController : IDisposable
    {
        private const float PendingTimeoutSeconds = 4f;
        private const string Scene5BasementKeyPickupPath =
            "World/Quests/Quest 4 KeyTake/ItemTake Key";

        private static InventorySharingController activeController;

        // Set while WorldTransitionSync is force-clearing the local inventory
        // on a world change. Those removals must not be broadcast into the
        // world being left.
        public static bool BulkClearingInventory = false;

        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly Harmony harmony;
        private readonly Dictionary<string, Component> pendingPickups = new Dictionary<string, Component>();
        private readonly Dictionary<string, float> pendingPickupExpiry = new Dictionary<string, float>();
        private readonly HashSet<string> claimedPickups = new HashSet<string>();
        private readonly Dictionary<string, string> pickupOwners = new Dictionary<string, string>();
        private readonly HashSet<string> hostPickupClicks = new HashSet<string>();
        private readonly HashSet<string> knownKeyItems = new HashSet<string>();
        private readonly HashSet<string> pendingConsumes = new HashSet<string>();
        private readonly HashSet<string> consumedKeyItems = new HashSet<string>();
        private bool localBasementKeyAdded;

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
                dispatcher.InventoryKeyRemovedReceived += OnKeyRemovedReceived;
                dispatcher.InventoryConsumeResultReceived += OnConsumeResult;
                dispatcher.InventorySnapshotReceived += OnSnapshotReceived;
                dispatcher.CustomMessageReceived += OnCustomMessage;
            }

            activeController = this;
            InstallHooks();
        }

        internal static string Scene5BasementKeyOwnerId
        {
            get
            {
                InventorySharingController controller = activeController;
                if (controller == null) return string.Empty;
                foreach (KeyValuePair<string, string> claim in controller.pickupOwners)
                {
                    if (IsScene5BasementKeyPath(claim.Key) &&
                        !string.IsNullOrWhiteSpace(claim.Value))
                        return claim.Value;
                }
                return controller.localBasementKeyAdded
                    ? controller.localPlayerId
                    : string.Empty;
            }
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
                pickupOwners.Clear();
                hostPickupClicks.Clear();
                knownKeyItems.Clear();
                pendingConsumes.Clear();
                consumedKeyItems.Clear();
                localBasementKeyAdded = false;
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
                dispatcher.InventoryKeyRemovedReceived -= OnKeyRemovedReceived;
                dispatcher.InventoryConsumeResultReceived -= OnConsumeResult;
                dispatcher.InventorySnapshotReceived -= OnSnapshotReceived;
                dispatcher.CustomMessageReceived -= OnCustomMessage;
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

        /// <summary>
        /// Clears all per-world cached state. Called by WorldTransitionSync when
        /// a world changes within the same scene name (which the Tick()'s
        /// scene-name-change check wouldn't catch).
        /// </summary>
        public static void ForceCacheClear()
        {
            InventorySharingController c = activeController;
            if (c == null) return;

            c.pendingPickups.Clear();
            c.pendingPickupExpiry.Clear();
            c.claimedPickups.Clear();
            c.pickupOwners.Clear();
            c.hostPickupClicks.Clear();
            c.knownKeyItems.Clear();
            c.pendingConsumes.Clear();
            c.consumedKeyItems.Clear();
            c.localBasementKeyAdded = false;
            c.allowedPickupPath = null;
            c.applyingRemoteKeyChange = false;

            DiagnosticLog.Info("InventorySharingController: caches cleared for world transition.");
        }

        private void InstallHooks()
        {
            MethodInfo click = FindMethod("ObjectInteractive", "Click", Type.EmptyTypes);
            MethodInfo keyAdd = FindMethod("GameController", "AddKeyItem", new[] { typeof(GameObject) });
            MethodInfo keyRemove = FindMethod("GameController", "RemoveKeyItem", new[] { typeof(GameObject) });
            MethodInfo clickPrefix = GetPatchMethod(nameof(ClickPrefix));
            MethodInfo keyAddPostfix = GetPatchMethod(nameof(KeyAddPostfix));
            MethodInfo keyRemovePrefix = GetPatchMethod(nameof(KeyRemovePrefix));
            MethodInfo keyRemovePostfix = GetPatchMethod(nameof(KeyRemovePostfix));
            MethodInfo clickPostfix = GetPatchMethod(nameof(ClickPostfix));

            int installed = 0;
            try
            {
                if (click != null)
                {
                    harmony.Patch(click, prefix: new HarmonyMethod(clickPrefix));
                    harmony.Patch(click, postfix: new HarmonyMethod(clickPostfix));
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
                    // Capture the stable scene path before the game mutates
                    // inventory state, then let its local removal complete and
                    // broadcast the same change to peers.
                    harmony.Patch(keyRemove, prefix: new HarmonyMethod(keyRemovePrefix));
                    harmony.Patch(keyRemove, postfix: new HarmonyMethod(keyRemovePostfix));
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

        private static bool ClickPrefix(Component __instance, out string __state)
        {
            __state = GetPickupPath(__instance);
            InventorySharingController controller = activeController;
            return controller == null || controller.ShouldRunPickupClick(__instance);
        }

        private static void ClickPostfix(Component __instance, string __state)
        {
            InventorySharingController controller = activeController;
            if (controller != null)
                controller.OnPickupClickCompleted(__instance, __state);
        }

        private static void KeyAddPostfix(Component __instance, GameObject _item)
        {
            InventorySharingController controller = activeController;
            if (controller != null)
                controller.OnLocalKeyAdded(_item);
        }

        private static void KeyRemovePrefix(GameObject _item, out string __state)
        {
            __state = GetPath(_item);
        }

        private static void KeyRemovePostfix(GameObject _item, string __state)
        {
            InventorySharingController controller = activeController;
            if (controller != null)
                controller.OnLocalKeyRemoved(_item, __state);
        }

        private bool ShouldRunPickupClick(Component interactive)
        {
            if (disposed || applyingRemoteKeyChange || !IsGameplayScene() || !IsPickup(interactive))
                return true;

            string itemPath = GetPickupPath(interactive);
            if (string.IsNullOrEmpty(itemPath)) return true;
            if (IsExcludedInventoryPath(itemPath)) return true;

            if (string.Equals(allowedPickupPath, itemPath, StringComparison.Ordinal))
                return true;

            if (claimedPickups.Contains(itemPath))
            {
                DeactivateRemotePickup(interactive.gameObject);
                return false;
            }

            if (pendingPickups.ContainsKey(itemPath))
                return false;

            SessionController session = SessionController.Active;
            if (session == null || dispatcher == null)
                return true;

            if (session.IsHost)
            {
                claimedPickups.Add(itemPath);
                pickupOwners[itemPath] = localPlayerId;
                hostPickupClicks.Add(itemPath);
                return true;
            }

            pendingPickups[itemPath] = interactive;
            pendingPickupExpiry[itemPath] = Time.unscaledTime + PendingTimeoutSeconds;
            dispatcher.SendInventoryClaimRequest(new InventoryClaimRequest
            {
                sceneName = SceneManager.GetActiveScene().name,
                itemPath = itemPath,
                senderId = localPlayerId
            });
            return false;
        }

        private void OnPickupClickCompleted(Component interactive, string capturedPath)
        {
            if (disposed || !IsGameplayScene()) return;
            string itemPath = string.IsNullOrEmpty(capturedPath)
                ? GetPickupPath(interactive)
                : capturedPath;
            if (string.IsNullOrEmpty(itemPath) || !hostPickupClicks.Remove(itemPath)) return;
            if (IsScene5BasementKeyPath(itemPath) && localBasementKeyAdded) return;
            if (knownKeyItems.Contains(itemPath)) return;

            // The interaction didn't grant an inventory key. Release the local
            // claim so another player can try it.
            claimedPickups.Remove(itemPath);
            pickupOwners.Remove(itemPath);
        }

        private void OnCustomMessage(string eventName, string json)
        {
            if (disposed || dispatcher == null || !SessionIsHost() ||
                !string.Equals(eventName, RpcDispatcher.InventoryClaimRequestEvent,
                    StringComparison.Ordinal) || string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                InventoryClaimRequest request = JsonSerializer.Deserialize<InventoryClaimRequest>(json,
                    new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
                if (request == null || !IsCurrentScene(request.sceneName) ||
                    string.IsNullOrWhiteSpace(request.itemPath) ||
                    string.IsNullOrWhiteSpace(request.senderId) ||
                    string.Equals(request.senderId, localPlayerId, StringComparison.Ordinal) ||
                    IsExcludedInventoryPath(request.itemPath) ||
                    !IsKnownParticipant(request.senderId))
                    return;

                // A claim can only reserve a real pickup in the host's scene.
                // Reply with a denial if the object is missing so the client
                // does not keep waiting on a request that can never succeed.
                if (FindPickupByPath(request.itemPath) == null)
                {
                    dispatcher.SendInventoryClaimResult(new InventoryClaimResult
                    {
                        sceneName = request.sceneName,
                        itemPath = request.itemPath,
                        ownerId = string.Empty,
                        approved = false
                    });
                    return;
                }

                string ownerId;
                bool alreadyClaimed = pickupOwners.TryGetValue(request.itemPath, out ownerId);
                bool isMarkedClaimed = claimedPickups.Contains(request.itemPath);
                bool approved = (!alreadyClaimed && !isMarkedClaimed) ||
                    string.Equals(ownerId, request.senderId, StringComparison.Ordinal);
                if (approved)
                {
                    claimedPickups.Add(request.itemPath);
                    pickupOwners[request.itemPath] = request.senderId;
                }

                dispatcher.SendInventoryClaimResult(new InventoryClaimResult
                {
                    sceneName = request.sceneName,
                    itemPath = request.itemPath,
                    ownerId = approved ? request.senderId : ownerId,
                    approved = approved
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("Inventory sharing could not process pickup claim: " + ex.Message);
            }
        }

        private void OnLocalKeyRemoved(GameObject item, string capturedPath)
        {
            if (disposed || applyingRemoteKeyChange || BulkClearingInventory ||
                !IsGameplayScene()) return;
            if (IsBasementKeyItem(item))
            {
                localBasementKeyAdded = false;
                return;
            }
            string itemPath = string.IsNullOrEmpty(capturedPath) ? GetPath(item) : capturedPath;
            if (string.IsNullOrEmpty(itemPath) || IsExcludedInventoryPath(itemPath) ||
                IsScene5BasementKeyPath(itemPath)) return;

            bool wasKnown = knownKeyItems.Remove(itemPath);
            bool newlyConsumed = consumedKeyItems.Add(itemPath);
            if (!wasKnown && !newlyConsumed) return;

            pendingConsumes.Remove(itemPath);
            if (dispatcher == null) return;
            dispatcher.SendInventoryKeyRemoved(new InventoryKeyRemoval
            {
                sceneName = SceneManager.GetActiveScene().name,
                itemPath = itemPath,
                senderId = localPlayerId
            });
        }

        private void OnClaimResult(InventoryClaimResult result)
        {
            if (disposed || result == null || !IsCurrentScene(result.sceneName) ||
                string.IsNullOrEmpty(result.itemPath) ||
                IsExcludedInventoryPath(result.itemPath))
                return;

            Component interactive;
            pendingPickups.TryGetValue(result.itemPath, out interactive);
            pendingPickups.Remove(result.itemPath);
            pendingPickupExpiry.Remove(result.itemPath);

            if (!result.approved)
            {
                if (!string.IsNullOrEmpty(result.ownerId))
                {
                    claimedPickups.Add(result.itemPath);
                    pickupOwners[result.itemPath] = result.ownerId;
                }
                else if (IsScene5BasementKeyPath(result.itemPath))
                {
                    claimedPickups.Remove(result.itemPath);
                    return;
                }
                if (interactive != null)
                    RemoveClaimedPickup(interactive.gameObject, result.itemPath, result.ownerId);
                return;
            }

            claimedPickups.Add(result.itemPath);
            if (!string.IsNullOrEmpty(result.ownerId))
                pickupOwners[result.itemPath] = result.ownerId;
            if (string.Equals(result.ownerId, localPlayerId, StringComparison.Ordinal))
            {
                if (interactive == null)
                    interactive = FindPickupByPath(result.itemPath);
                RunApprovedPickup(interactive, result.itemPath);
            }
            else if (interactive != null)
            {
                RemoveClaimedPickup(interactive.gameObject, result.itemPath, result.ownerId);
            }
            else
            {
                GameObject item = FindByPath(result.itemPath);
                if (item != null) RemoveClaimedPickup(item, result.itemPath, result.ownerId);
            }
        }

        private void OnLocalKeyAdded(GameObject item)
        {
            if (disposed || dispatcher == null || applyingRemoteKeyChange ||
                item == null || !IsGameplayScene())
                return;

            string itemPath = GetPath(item);
            if (IsBasementKeyItem(item))
            {
                localBasementKeyAdded = true;
                string claimPath = IsScene5BasementKeyPath(allowedPickupPath)
                    ? allowedPickupPath
                    : FindBasementKeyClickPath();
                if (string.IsNullOrEmpty(claimPath))
                    claimPath = Scene5BasementKeyPickupPath;
                claimedPickups.Add(claimPath);
                pickupOwners[claimPath] = localPlayerId;
                hostPickupClicks.Remove(claimPath);
                dispatcher.SendInventoryKeyAdded(new InventoryKeyChange
                {
                    sceneName = SceneManager.GetActiveScene().name,
                    itemPath = claimPath,
                    ownerId = localPlayerId
                });
                return;
            }
            if (string.IsNullOrEmpty(itemPath) || IsExcludedInventoryPath(itemPath)) return;

            consumedKeyItems.Remove(itemPath);
            knownKeyItems.Add(itemPath);
            claimedPickups.Add(itemPath);
            pickupOwners[itemPath] = localPlayerId;
            hostPickupClicks.Remove(itemPath);
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
                string.IsNullOrEmpty(change.itemPath) ||
                IsExcludedInventoryPath(change.itemPath))
                return;

            GameObject worldItem = FindByPath(change.itemPath);
            if (IsScene5BasementKeyPath(change.itemPath) || IsBasementKeyItem(worldItem))
            {
                claimedPickups.Add(change.itemPath);
                if (!string.IsNullOrEmpty(change.ownerId))
                    pickupOwners[change.itemPath] = change.ownerId;
                if (worldItem != null && change.ownerId != localPlayerId)
                    RemoveClaimedPickup(worldItem, change.itemPath, change.ownerId);
                return;
            }

            consumedKeyItems.Remove(change.itemPath);
            claimedPickups.Add(change.itemPath);
            pickupOwners[change.itemPath] = change.ownerId ?? string.Empty;
            pendingPickups.Remove(change.itemPath);
            pendingPickupExpiry.Remove(change.itemPath);
            if (worldItem != null) DeactivateRemotePickup(worldItem);
            ApplyKeyAdd(change.itemPath);
        }

        private void OnKeyRemovedReceived(InventoryKeyRemoval removal)
        {
            if (disposed || removal == null || !IsCurrentScene(removal.sceneName) ||
                string.IsNullOrEmpty(removal.itemPath) ||
                string.IsNullOrWhiteSpace(removal.senderId) ||
                string.Equals(removal.senderId, localPlayerId, StringComparison.Ordinal) ||
                IsExcludedInventoryPath(removal.itemPath) ||
                IsScene5BasementKeyPath(removal.itemPath))
                return;

            pendingConsumes.Remove(removal.itemPath);
            consumedKeyItems.Add(removal.itemPath);
            ApplyKeyRemove(removal.itemPath);
        }

        private void OnConsumeResult(InventoryConsumeResult result)
        {
            if (disposed || result == null || !IsCurrentScene(result.sceneName) ||
                string.IsNullOrEmpty(result.itemPath) ||
                IsExcludedInventoryPath(result.itemPath))
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
                if (item == null || string.IsNullOrEmpty(item.itemPath) ||
                    IsExcludedInventoryPath(item.itemPath) ||
                    IsScene5BasementKeyPath(item.itemPath)) continue;

                claimedPickups.Add(item.itemPath);
                if (!string.IsNullOrEmpty(item.ownerId))
                    pickupOwners[item.itemPath] = item.ownerId;
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
            if (IsExcludedInventoryPath(itemPath) || IsScene5BasementKeyPath(itemPath)) return;
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
            if (IsExcludedInventoryPath(itemPath) || IsScene5BasementKeyPath(itemPath)) return;
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
                return interactive != null &&
                       !IsExcludedInventoryItem(interactive.gameObject) &&
                       interactive.GetComponent("ObjectInteractiveItemTake") != null;
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

        private void RemoveClaimedPickup(GameObject item, string itemPath, string ownerId)
        {
            if (item == null) return;
            if (IsScene5BasementKeyPath(itemPath) || IsBasementKeyItem(item))
            {
                if (string.IsNullOrWhiteSpace(ownerId) ||
                    string.Equals(ownerId, localPlayerId, StringComparison.Ordinal)) return;
                try
                {
                    item.SetActive(false);
                    UnityEngine.Object.Destroy(item);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("Inventory sharing could not destroy the remote basement key: " +
                                          ex.Message);
                    DeactivateRemotePickup(item);
                }
                return;
            }
            DeactivateRemotePickup(item);
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

        private static string GetPickupPath(Component interactive)
        {
            if (interactive == null) return null;
            try
            {
                Transform node = interactive.transform;
                while (node != null)
                {
                    string path = LocalPlayerLocator.GetPath(node);
                    if (IsScene5BasementKeyPath(path)) return path;
                    node = node.parent;
                }
                return GetPath(interactive.gameObject);
            }
            catch
            {
                return GetPath(interactive.gameObject);
            }
        }

        private string FindBasementKeyClickPath()
        {
            foreach (string path in hostPickupClicks)
                if (IsScene5BasementKeyPath(path)) return path;
            foreach (string path in pickupOwners.Keys)
                if (IsScene5BasementKeyPath(path)) return path;
            return null;
        }

        private static bool IsExcludedInventoryItem(GameObject item)
        {
            return item != null && IsExcludedInventoryPath(GetPath(item));
        }

        private static bool IsBasementKeyItem(GameObject item)
        {
            if (item == null) return false;
            return string.Equals(RemoveCloneSuffix(item.name), "ItemTake Key",
                       StringComparison.OrdinalIgnoreCase) ||
                   IsScene5BasementKeyPath(GetPath(item));
        }

        private static bool IsScene5BasementKeyPath(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) return false;
            string normalized = itemPath.Replace('\\', '/');
            int leafIndex = normalized.LastIndexOf('/');
            string leaf = leafIndex >= 0 ? normalized.Substring(leafIndex + 1) : normalized;
            string parent = leafIndex >= 0 ? normalized.Substring(0, leafIndex) : string.Empty;
            return string.Equals(RemoveCloneSuffix(leaf), "ItemTake Key",
                       StringComparison.OrdinalIgnoreCase) &&
                   parent.IndexOf("Quest 4 KeyTake", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoveCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            const string cloneSuffix = " (Clone)";
            return name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - cloneSuffix.Length).TrimEnd()
                : name;
        }

        private static bool IsExcludedInventoryPath(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) return false;
            string[] segments = itemPath.Replace('\\', '/').Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string name = segments[i].Trim();
                const string cloneSuffix = " (Clone)";
                if (name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - cloneSuffix.Length).TrimEnd();

                if (string.Equals(name, "Flashlight", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Bat", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

        private bool SessionIsHost()
        {
            SessionController session = SessionController.Active;
            return session != null && session.IsHost;
        }

        private static bool IsKnownParticipant(string playerId)
        {
            SessionController session = SessionController.Active;
            if (session == null || string.IsNullOrWhiteSpace(playerId)) return false;
            if (string.Equals(session.LocalPlayerId, playerId, StringComparison.Ordinal)) return true;

            foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
            {
                SessionPlayer player = pair.Value;
                if (player != null &&
                    (string.Equals(player.playerId, playerId, StringComparison.Ordinal) ||
                     string.Equals(pair.Key, playerId, StringComparison.Ordinal)))
                    return true;
            }
            return false;
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
