using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MiSideMultiplayer.API.Notifications
{
    /// <summary>
    /// Creates short notifications from MiSide's built-in Interface/HintScreen
    /// prefab. Calls may be queued from a non-Unity thread; all UI work happens
    /// on the runtime's main-thread tick.
    /// </summary>
    internal static class NotificationManager
    {
        private sealed class Request
        {
            public string Text;
            public string Key;
            public float Duration;
            public float Cooldown;
            public float ExpiresAt;
            public bool Persistent;
        }

        private sealed class VisibleNotification
        {
            public string Text;
            public string Key;
            public GameObject Object;
            public float HideAt;
            public float DestroyAt;
            public bool HideStarted;
            public bool Persistent;
        }

        private sealed class PeriodicRequest
        {
            public int Id;
            public Func<string> TextFactory;
            public string Key;
            public float Interval;
            public float Duration;
            public float Cooldown;
            public float NextAt;
            public bool ShowImmediately;
            public bool Initialized;
        }

        private sealed class PeriodicHandle : IDisposable
        {
            private int _id;
            public PeriodicHandle(int id) { _id = id; }
            public void Dispose()
            {
                int id = Interlocked.Exchange(ref _id, 0);
                if (id != 0) RemovePeriodic(id);
            }
        }

        private static readonly object QueueLock = new object();
        private static readonly Queue<Request> Requests = new Queue<Request>();
        private static readonly HashSet<string> QueuedKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> PendingPersistentClears =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<Request> WaitingForUi = new List<Request>();
        private static readonly List<VisibleNotification> Visible = new List<VisibleNotification>();
        private static readonly Dictionary<string, float> LastShownAt =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly object PeriodicLock = new object();
        private static readonly List<PeriodicRequest> PeriodicRequests = new List<PeriodicRequest>();
        private static int _nextPeriodicId;

        private static GameObject _hintTemplate;
        private static GameObject _interfaceObject;
        private static float _bottomMargin;
        private static float _rightMargin;
        private static bool _marginsResolved;
        private static float _nextFailureWarningTime;
        private static readonly int HideHash = Animator.StringToHash("Hide");

        internal static bool Enqueue(string text, float durationSeconds, string key, float cooldownSeconds)
        {
            return EnqueueCore(text, durationSeconds, key, cooldownSeconds, false);
        }

        internal static bool EnqueuePersistent(string text, string key)
        {
            return EnqueueCore(text, 0f, key, 0f, true);
        }

        private static bool EnqueueCore(string text, float durationSeconds, string key,
                                        float cooldownSeconds, bool persistent)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string normalizedKey = string.IsNullOrWhiteSpace(key) ? text.Trim() : key.Trim();
            Request request = new Request
            {
                Text = text,
                Key = normalizedKey,
                Duration = Math.Max(0.25f, Math.Min(60f, durationSeconds)),
                Cooldown = Math.Max(0f, cooldownSeconds),
                Persistent = persistent
            };

            lock (QueueLock)
            {
                if (persistent) PendingPersistentClears.Remove(normalizedKey);
                if (!QueuedKeys.Add(normalizedKey)) return true;
                Requests.Enqueue(request);
            }
            return true;
        }

        internal static void ClearPersistent(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            string normalizedKey = key.Trim();
            lock (QueueLock)
            {
                if (Requests.Count > 0)
                {
                    Request[] requests = Requests.ToArray();
                    Requests.Clear();
                    for (int i = 0; i < requests.Length; i++)
                    {
                        if (requests[i].Persistent && requests[i].Key == normalizedKey)
                            continue;
                        Requests.Enqueue(requests[i]);
                    }
                }
                QueuedKeys.Remove(normalizedKey);
                PendingPersistentClears.Add(normalizedKey);
            }
        }

        private static void ProcessPersistentClears()
        {
            string[] keys;
            lock (QueueLock)
            {
                keys = new string[PendingPersistentClears.Count];
                PendingPersistentClears.CopyTo(keys);
                PendingPersistentClears.Clear();
            }
            if (keys.Length == 0) return;

            float now = Time.realtimeSinceStartup;
            HashSet<string> clearing = new HashSet<string>(keys, StringComparer.Ordinal);
            for (int i = WaitingForUi.Count - 1; i >= 0; i--)
            {
                Request request = WaitingForUi[i];
                if (!request.Persistent || !clearing.Contains(request.Key)) continue;
                WaitingForUi.RemoveAt(i);
            }

            for (int i = 0; i < Visible.Count; i++)
            {
                VisibleNotification notification = Visible[i];
                if (!notification.Persistent || !clearing.Contains(notification.Key) ||
                    notification.Object == null)
                    continue;

                notification.Persistent = false;
                notification.HideAt = now;
                notification.DestroyAt = now + 1f;
                notification.HideStarted = false;
            }
        }

        internal static IDisposable RegisterPeriodic(Func<string> textFactory, float intervalSeconds,
                                                     string key, float durationSeconds,
                                                     float cooldownSeconds, bool showImmediately)
        {
            if (textFactory == null) throw new ArgumentNullException(nameof(textFactory));
            int id = Interlocked.Increment(ref _nextPeriodicId);
            PeriodicRequest request = new PeriodicRequest
            {
                Id = id,
                TextFactory = textFactory,
                Key = string.IsNullOrWhiteSpace(key) ? "qblib.periodic." + id : key.Trim(),
                Interval = Math.Max(0.5f, Math.Min(3600f, intervalSeconds)),
                Duration = Math.Max(0.25f, Math.Min(60f, durationSeconds)),
                Cooldown = Math.Max(0f, cooldownSeconds),
                ShowImmediately = showImmediately
            };
            lock (PeriodicLock) PeriodicRequests.Add(request);
            return new PeriodicHandle(id);
        }

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            ProcessPersistentClears();
            UpdatePeriodicRequests(now);
            DrainRequests();
            UpdateVisible(now);
            RetryWaiting(now);
        }

        private static void UpdatePeriodicRequests(float now)
        {
            PeriodicRequest[] snapshot;
            lock (PeriodicLock) snapshot = PeriodicRequests.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                PeriodicRequest request = snapshot[i];
                if (!request.Initialized)
                {
                    request.Initialized = true;
                    request.NextAt = request.ShowImmediately ? now : now + request.Interval;
                }
                if (now < request.NextAt) continue;

                request.NextAt = now + request.Interval;
                try
                {
                    string text = request.TextFactory();
                    if (!string.IsNullOrWhiteSpace(text))
                        Enqueue(text, request.Duration, request.Key, request.Cooldown);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("QBLib notifications: periodic provider failed: " + ex.Message);
                }
            }
        }

        private static void RemovePeriodic(int id)
        {
            lock (PeriodicLock)
                PeriodicRequests.RemoveAll(request => request.Id == id);
        }

        private static void DrainRequests()
        {
            while (true)
            {
                Request request;
                lock (QueueLock)
                {
                    if (Requests.Count == 0) return;
                    request = Requests.Dequeue();
                }

                if (!request.Persistent && request.ExpiresAt <= 0f)
                    request.ExpiresAt = Time.realtimeSinceStartup + 15f;

                if (!request.Persistent && request.ExpiresAt <= Time.realtimeSinceStartup)
                {
                    ReleaseQueuedKey(request.Key);
                    continue;
                }

                if (!TryShow(request))
                    WaitingForUi.Add(request);
            }
        }

        private static void RetryWaiting(float now)
        {
            for (int i = WaitingForUi.Count - 1; i >= 0; i--)
            {
                Request request = WaitingForUi[i];
                if (!request.Persistent && request.ExpiresAt <= now)
                {
                    WaitingForUi.RemoveAt(i);
                    ReleaseQueuedKey(request.Key);
                    continue;
                }

                if (TryShow(request))
                    WaitingForUi.RemoveAt(i);
            }
        }

        private static bool TryShow(Request request)
        {
            if (!IsGameplayScene(SceneManager.GetActiveScene().name)) return false;

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < Visible.Count; i++)
            {
                VisibleNotification current = Visible[i];
                if (current.Object != null && current.Key == request.Key)
                {
                    // A persistent warning can return while its previous
                    // instance is fading out. Revive that instance instead of
                    // treating it as already visible and dropping the request.
                    if (request.Persistent && !current.Persistent)
                    {
                        current.Text = request.Text;
                        current.Persistent = true;
                        current.HideAt = float.MaxValue;
                        current.DestroyAt = float.MaxValue;
                        current.HideStarted = false;
                        Text label = current.Object.GetComponentInChildren<Text>();
                        if (label != null) label.text = request.Text;
                        Animator animator = current.Object.GetComponent<Animator>();
                        if (animator != null) animator.SetBool(HideHash, false);
                        if (!current.Object.activeSelf) current.Object.SetActive(true);
                        UpdatePositions();
                    }
                    ReleaseQueuedKey(request.Key);
                    return true;
                }
            }

            float lastShown;
            if (request.Cooldown > 0f && LastShownAt.TryGetValue(request.Key, out lastShown) &&
                now - lastShown < request.Cooldown)
            {
                ReleaseQueuedKey(request.Key);
                return true;
            }

            try
            {
                if (!ResolveTemplate()) return false;
            }
            catch (Exception ex)
            {
                LogFailure("could not resolve the game's HintScreen: " + ex.Message);
                return false;
            }

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(_hintTemplate);
                if (clone == null) return false;
                clone.transform.SetParent(_interfaceObject.transform, false);

                Text label = clone.GetComponentInChildren<Text>();
                if (label == null)
                {
                    UnityEngine.Object.Destroy(clone);
                    ReleaseQueuedKey(request.Key);
                    DiagnosticLog.Warning("QBLib notifications: built-in HintScreen has no Text component.");
                    return true;
                }
                label.text = request.Text;
                clone.SetActive(true);

                Visible.Add(new VisibleNotification
                {
                    Text = request.Text,
                    Key = request.Key,
                    Object = clone,
                    HideAt = request.Persistent ? float.MaxValue : now + request.Duration,
                    DestroyAt = request.Persistent ? float.MaxValue : now + request.Duration + 1f,
                    Persistent = request.Persistent
                });
                LastShownAt[request.Key] = now;
                ReleaseQueuedKey(request.Key);
                UpdatePositions();
                return true;
            }
            catch (Exception ex)
            {
                if (clone != null) UnityEngine.Object.Destroy(clone);
                LogFailure("failed to show notification: " + ex.Message);
                return false;
            }
        }

        private static void UpdateVisible(float now)
        {
            bool changed = false;
            for (int i = Visible.Count - 1; i >= 0; i--)
            {
                VisibleNotification notification = Visible[i];
                if (notification.Object == null)
                {
                    Visible.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (notification.Persistent) continue;

                if (!notification.HideStarted && now >= notification.HideAt)
                {
                    notification.HideStarted = true;
                    Animator animator = notification.Object.GetComponent<Animator>();
                    if (animator != null)
                        animator.SetBool(HideHash, true);
                    else
                        notification.DestroyAt = now;
                }

                if (now >= notification.DestroyAt)
                {
                    UnityEngine.Object.Destroy(notification.Object);
                    Visible.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed) UpdatePositions();
        }

        private static bool ResolveTemplate()
        {
            if (_hintTemplate != null && _interfaceObject != null) return true;
            _hintTemplate = null;
            _interfaceObject = null;
            _marginsResolved = false;

            GameController controller = null;
            try { controller = UnityEngine.Object.FindObjectOfType<GameController>(); }
            catch { }
            if (controller == null) return false;

            Transform controllerTransform = controller.transform;
            for (int i = 0; i < controllerTransform.childCount; i++)
            {
                Transform child = controllerTransform.GetChild(i);
                if (child != null && child.name == "Interface")
                {
                    _interfaceObject = child.gameObject;
                    break;
                }
            }
            if (_interfaceObject == null) return false;

            Transform interfaceTransform = _interfaceObject.transform;
            for (int i = 0; i < interfaceTransform.childCount; i++)
            {
                Transform child = interfaceTransform.GetChild(i);
                if (child != null && child.name == "HintScreen")
                {
                    _hintTemplate = child.gameObject;
                    break;
                }
            }
            if (_hintTemplate == null) return false;

            RectTransform rect = _hintTemplate.GetComponent<RectTransform>();
            if (rect != null)
            {
                _bottomMargin = -rect.anchoredPosition.y;
                _rightMargin = -rect.anchoredPosition.x;
            }
            _marginsResolved = true;
            return true;
        }

        private static void UpdatePositions()
        {
            if (_interfaceObject == null) return;
            int visibleIndex = 0;
            for (int i = 0; i < Visible.Count; i++)
            {
                VisibleNotification notification = Visible[i];
                if (notification.Object == null) continue;
                RectTransform rect = notification.Object.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchorMin = new Vector2(1f, 0f);
                    rect.anchorMax = new Vector2(1f, 0f);
                    rect.pivot = new Vector2(1f, 0f);
                    rect.anchoredPosition = new Vector2(
                        _marginsResolved ? _rightMargin : 0f,
                        visibleIndex * 100f + (_marginsResolved ? _bottomMargin : 0f));
                }
                visibleIndex++;
            }
        }

        private static bool IsGameplayScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) &&
                   !string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal) &&
                   !string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal);
        }

        private static void ReleaseQueuedKey(string key)
        {
            lock (QueueLock) QueuedKeys.Remove(key);
        }

        private static void LogFailure(string message)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextFailureWarningTime) return;
            _nextFailureWarningTime = now + 5f;
            DiagnosticLog.Warning("QBLib notifications: " + message);
        }
    }
}
