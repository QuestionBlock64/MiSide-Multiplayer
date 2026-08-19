using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Minecraft-style in-game chat. The full list is deliberately retained until
    /// SceneMenu is entered; closing chat only changes which messages are drawn.
    /// </summary>
    public sealed class ChatController : IDisposable
    {
        private const float MessageFadeSeconds = 10f;
        private const int ClosedMessageCount = 4;
        private const float ProximityDistance = 30f;
        private const int MaxMessageLength = 512;

        private const float MinecraftChatWidth = 640f;
        private const float MinecraftInputWidth = 1280f;
        private const float ClosedChatBottomOffset = 80f;
        private const float ChatBackgroundOpacity = 0.55f;
        private readonly RpcDispatcher dispatcher;
        private readonly NetworkManager networkManager;
        private readonly string localPlayerId;
        private readonly Func<string> displayNameProvider;
        private readonly List<ChatLine> messages = new List<ChatLine>();
        private MonoBehaviour playerMoveScript;

        private string inputText = string.Empty;
        private string activeSceneName;
        private bool isOpen;
        private bool cursorStateCaptured;
        private bool cursorWasVisible;
        private CursorLockMode cursorLockState;
        private GUIStyle messageStyle;
        private GUIStyle inputStyle;

        public ChatController(
            RpcDispatcher dispatcher,
            NetworkManager networkManager,
            string localPlayerId,
            Func<string> displayNameProvider)
        {
            this.dispatcher = dispatcher;
            this.networkManager = networkManager;
            this.localPlayerId = localPlayerId ?? string.Empty;
            this.displayNameProvider = displayNameProvider;
            activeSceneName = SceneManager.GetActiveScene().name;

            if (dispatcher != null)
            {
                dispatcher.ChatMessageReceived += OnChatMessageReceived;
                dispatcher.ChatSystemMessageReceived += OnChatSystemMessageReceived;
                dispatcher.ServerResponseReceived += OnServerResponseReceived;
            }
        }

        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != activeSceneName)
            {
                activeSceneName = sceneName;
                if (string.Equals(sceneName, "SceneMenu", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHistory();
                    CloseChat(false);
                }
            }

            if (!IsChatAvailable())
            {
                if (isOpen)
                    CloseChat(false);
                return;
            }

            if (isOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                Input.ResetInputAxes();
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (isOpen)
                    SubmitInput();
                else
                    OpenChat();
            }

            if (isOpen)
            {
                SetChatCursor();
                FreezeLocalMovement();
            }
        }

        public void OnGui()
        {
            if (Event.current == null || GUI.skin == null)
                return;

            if (!IsChatAvailable())
                return;

            EnsureStyles();
            HandleKeyboard(Event.current);

            if (isOpen)
                DrawOpenChat();
            else
                DrawClosedChat();
        }

        public void Dispose()
        {
            if (dispatcher != null)
            {
                dispatcher.ChatMessageReceived -= OnChatMessageReceived;
                dispatcher.ChatSystemMessageReceived -= OnChatSystemMessageReceived;
                dispatcher.ServerResponseReceived -= OnServerResponseReceived;
            }

            if (isOpen)
                CloseChat();
            RestoreLocalMovement();
        }

        private void HandleKeyboard(Event currentEvent)
        {
            if (!isOpen || currentEvent.type != EventType.KeyDown)
                return;

            if (currentEvent.keyCode == KeyCode.Escape)
            {
                CloseChat();
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.Backspace)
            {
                if (inputText.Length > 0)
                    inputText = inputText.Substring(0, inputText.Length - 1);
                currentEvent.Use();
                return;
            }

            if (currentEvent.character != '\0' && !char.IsControl(currentEvent.character) &&
                inputText.Length < MaxMessageLength)
            {
                inputText += currentEvent.character;
                currentEvent.Use();
            }
        }

        private void OpenChat()
        {
            isOpen = true;
            inputText = string.Empty;
            CaptureGameCursorState();
            SetChatCursor();
            FreezeLocalMovement();
        }

        private void CloseChat(bool restoreCursor = true)
        {
            isOpen = false;
            inputText = string.Empty;
            if (restoreCursor)
                RestoreGameCursor();
            else
                ReleaseCursorState();
            RestoreLocalMovement();
        }

        private void CaptureGameCursorState()
        {
            if (cursorStateCaptured)
                return;

            cursorWasVisible = Cursor.visible;
            cursorLockState = Cursor.lockState;
            cursorStateCaptured = true;
        }

        private static void SetChatCursor()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void RestoreGameCursor()
        {
            if (!cursorStateCaptured)
                return;

            Cursor.visible = cursorWasVisible;
            Cursor.lockState = cursorLockState;
            ReleaseCursorState();
        }

        private void ReleaseCursorState()
        {
            cursorStateCaptured = false;
        }

        private void SubmitInput()
        {
            string text = (inputText ?? string.Empty).Trim();
            CloseChat();

            if (text.Length == 0 || dispatcher == null)
                return;

            if (text.Length > MaxMessageLength)
                text = text.Substring(0, MaxMessageLength);

            ChatMessagePayload message = new ChatMessagePayload();
            message.senderId = localPlayerId;
            message.displayName = GetDisplayName();
            message.sceneName = SceneManager.GetActiveScene().name;
            message.position = GetLocalPosition();
            message.text = text;

            // Commands are handled exclusively by the relay. They must never be
            // inserted into local history, even when the sender is not an OP.
            if (!text.StartsWith("/"))
                AddLine("You: " + text, "white");

            dispatcher.SendChatMessage(message);
        }

        private void OnChatMessageReceived(ChatMessagePayload message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.text))
                return;
            if (!string.IsNullOrEmpty(message.senderId) && message.senderId == localPlayerId)
                return;
            if (!string.IsNullOrEmpty(message.sceneName) &&
                !string.Equals(message.sceneName, SceneManager.GetActiveScene().name, StringComparison.OrdinalIgnoreCase))
                return;
            if (!IsSenderNear(message.position))
                return;

            string sender = string.IsNullOrWhiteSpace(message.displayName)
                ? message.senderId
                : message.displayName;
            if (string.IsNullOrWhiteSpace(sender))
                sender = "Player";

            AddLine(sender + ": " + message.text, "white");
        }

        private void OnChatSystemMessageReceived(ChatSystemMessagePayload message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.text))
                return;
            if (!string.IsNullOrEmpty(message.sceneName) &&
                !string.Equals(message.sceneName, SceneManager.GetActiveScene().name, StringComparison.OrdinalIgnoreCase))
                return;

            // System messages intentionally bypass proximity checks.
            AddLine(message.text, string.IsNullOrEmpty(message.color) ? "yellow" : message.color);
        }

        private void OnServerResponseReceived(ServerResponsePayload response)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.text))
                return;
            AddLine(response.text, string.IsNullOrEmpty(response.color) ? "white" : response.color);
        }

        private bool IsSenderNear(NetVector3 senderPosition)
        {
            if (networkManager == null || networkManager.LocalPlayerRoot == null)
                return false;

            return Vector3.Distance(networkManager.LocalPlayerRoot.position, senderPosition.ToUnity()) <= ProximityDistance;
        }

        private NetVector3 GetLocalPosition()
        {
            if (networkManager != null && networkManager.LocalPlayerRoot != null)
                return NetVector3.FromUnity(networkManager.LocalPlayerRoot.position);
            return NetVector3.FromUnity(Vector3.zero);
        }

        private string GetDisplayName()
        {
            string displayName = displayNameProvider != null ? displayNameProvider() : null;
            return string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();
        }

        private void AddLine(string text, string color)
        {
            messages.Add(new ChatLine
            {
                Text = text,
                Color = color,
                ReceivedAt = Time.unscaledTime
            });
        }

        private void ClearHistory()
        {
            messages.Clear();
        }

        private static bool IsChatAvailable()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(sceneName) || sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return LocalPlayerLocator.FindHardcodedPlayerPath() != null;
        }

        private void FreezeLocalMovement()
        {
            if (playerMoveScript != null)
            {
                playerMoveScript.enabled = false;
                return;
            }

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == null)
                return;

            Component playerMove = localPlayer.GetComponent("PlayerMove");
            playerMoveScript = playerMove as MonoBehaviour;
            if (playerMoveScript != null)
                playerMoveScript.enabled = false;
        }

        private void RestoreLocalMovement()
        {
            if (playerMoveScript != null)
            {
                playerMoveScript.enabled = true;
                playerMoveScript = null;
            }
        }

        private void EnsureStyles()
        {
            if (messageStyle != null && inputStyle != null)
                return;

            messageStyle = new GUIStyle(GUI.skin.label);
            messageStyle.fontSize = 16;
            messageStyle.wordWrap = true;
            messageStyle.padding = new RectOffset
            {
                left = 8,
                right = 8,
                top = 1,
                bottom = 1
            };

            inputStyle = new GUIStyle(GUI.skin.label);
            inputStyle.fontSize = 16;
            inputStyle.wordWrap = false;
            inputStyle.padding = new RectOffset
            {
                left = 8,
                right = 8,
                top = 1,
                bottom = 1
            };
        }

        private void DrawOpenChat()
        {
            float chatWidth = Mathf.Min(MinecraftChatWidth, Screen.width);
            const float inputHeight = 20f;
            float inputWidth = Mathf.Min(MinecraftInputWidth, Screen.width);
            float historyBottom = Screen.height - ClosedChatBottomOffset;
            float historyHeight = Mathf.Min(360f, historyBottom);
            Rect historyRect = new Rect(0f, historyBottom - historyHeight, chatWidth, historyHeight);
            DrawOpenHistory(historyRect);

            Rect inputRect = new Rect(0f, Screen.height - inputHeight, inputWidth, inputHeight);
            DrawBackground(inputRect, ChatBackgroundOpacity);
            Rect inputTextRect = new Rect(inputRect.x + 12f, inputRect.y + 1f, inputRect.width - 24f, inputRect.height - 2f);
            string fullInput = inputText ?? string.Empty;
            const float cursorWidth = 8f;
            float availableTextWidth = Mathf.Max(0f, inputTextRect.width - inputStyle.padding.horizontal - cursorWidth);
            int firstVisibleCharacter = 0;
            string visibleInput = fullInput;
            float visibleTextWidth = Mathf.Max(0f, inputStyle.CalcSize(new GUIContent(visibleInput)).x - inputStyle.padding.horizontal);
            while (visibleTextWidth > availableTextWidth && firstVisibleCharacter < fullInput.Length)
            {
                firstVisibleCharacter++;
                visibleInput = fullInput.Substring(firstVisibleCharacter);
                visibleTextWidth = Mathf.Max(0f, inputStyle.CalcSize(new GUIContent(visibleInput)).x - inputStyle.padding.horizontal);
            }
            GUI.Label(inputTextRect, visibleInput, inputStyle);

            if ((Time.unscaledTime % 1f) < 0.5f)
            {
                float cursorX = Mathf.Min(inputTextRect.xMax - inputStyle.padding.right - cursorWidth, inputTextRect.x + inputStyle.padding.left + visibleTextWidth);
                Color previousColor = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(cursorX, inputRect.yMax - 6f, cursorWidth, 2f), Texture2D.whiteTexture);
                GUI.color = previousColor;
            }
        }

        private void DrawOpenHistory(Rect historyRect)
        {
            if (messages.Count == 0)
                return;

            // BeginScrollView is stripped from MiSide's IL2CPP metadata. Keep
            // the latest messages visible without relying on that API. Like
            // Minecraft, each line has its own background instead of one panel.
            float contentWidth = historyRect.width - 16f;
            float y = historyRect.yMax;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                ChatLine line = messages[i];
                float height = GetLineHeight(line, contentWidth);
                y -= height;
                if (y < historyRect.y)
                    break;

                Rect lineRect = new Rect(historyRect.x, y, historyRect.width, height);
                DrawBackground(lineRect, ChatBackgroundOpacity);
                Color previousColor = GUI.color;
                GUI.color = GetColor(line.Color, 1f);
                GUI.Label(new Rect(historyRect.x + 8f, y, contentWidth, height), line.Text, messageStyle);
                GUI.color = previousColor;
            }
        }

        private void DrawClosedChat()
        {
            const float margin = 0f;
            float width = Mathf.Min(MinecraftChatWidth, Screen.width);
            float y = Screen.height - ClosedChatBottomOffset;
            int shown = 0;

            for (int i = messages.Count - 1; i >= 0 && shown < ClosedMessageCount; i--)
            {
                ChatLine line = messages[i];
                float age = Time.unscaledTime - line.ReceivedAt;
                if (age >= MessageFadeSeconds)
                    continue;

                float alpha = Mathf.Clamp01(1f - age / MessageFadeSeconds);
                float height = GetLineHeight(line, width);
                y -= height;
                Rect lineRect = new Rect(margin, y, width, height);
                DrawBackground(lineRect, ChatBackgroundOpacity * alpha);
                Color previousColor = GUI.color;
                GUI.color = GetColor(line.Color, alpha);
                GUI.Label(lineRect, line.Text, messageStyle);
                GUI.color = previousColor;
                shown++;
            }
        }

        private float GetLineHeight(ChatLine line, float width)
        {
            return Mathf.Max(18f, messageStyle.CalcHeight(new GUIContent(line.Text), width));
        }

        private static void DrawBackground(Rect rect, float alpha)
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private static Color GetColor(string color, float alpha)
        {
            Color result;
            switch ((color ?? string.Empty).ToLowerInvariant())
            {
                case "yellow":  result = Color.yellow; break;
                case "red":     result = Color.red; break;
                case "darkred": result = new Color(0.65f, 0.05f, 0.05f); break;
                case "green":   result = Color.green; break;
                default:          result = Color.white; break;
            }
            result.a = alpha;
            return result;
        }

        private sealed class ChatLine
        {
            public string Text;
            public string Color;
            public float ReceivedAt;
        }
    }
}
