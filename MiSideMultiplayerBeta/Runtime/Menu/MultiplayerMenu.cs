using System.Collections;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Login;
using MiSideMultiplayer.Runtime.Login.Stages;
using MiSideMultiplayer.Runtime.Menu.UI;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerMenu
    {
        public const string PageName   = "Location Multiplayer";
        public const string PageTitle  = "MULTIPLAYER";
        public const string PromptText = "PRESS 'ENTER' TO CONTINUE";
        public const string PromptChildName = "Prompt";

        public const string LoggingInText = "LOGGING IN...";
        public const string LoggingInChildName = "LoggingIn";

        public const string LoggedInText = "";
        public const string LoggedInChildName = "LoggedIn";

        public const string HomeMenuName = "Location Multiplayer Home";

        private const string ContinueOptionName = "CONTINUE";

        private const float BackY   = -650f;
        private const float PromptWidth  = 700f;
        private const float PromptHeight = 80f;
        private const float PromptX = 450f;

        private const float ContinueOffscreenY = -9000f;

        private const float FadeDuration = 0.2f;

        private const float CloseDebounceSeconds = 0.4f;

        public enum LoginState
        {
            NotLoggedIn,
            LoggingIn,
            LoggedIn
        }

        private static LoginState _currentState = LoginState.NotLoggedIn;
        public static LoginState CurrentState { get { return _currentState; } }

        private static LoginStageController _loginController;
        public static LoginStageController LoginController { get { return _loginController; } }

        private static MenuOption _backOption;
        private static MenuOption _continueOption;
        private static GameObject _cachedBackTarget;

        // Fader for the BACK button's CanvasGroup, so it can fade with the
        // prompt.
        private static CanvasGroupFader _backFader;

        private static System.Action _pendingAfterPromptFadeOut;
        private static bool _waitingForPromptFadeOut;

        private static float _lastOpenTime = -999f;

        public static GameMenu Build(GameMenu backTarget)
        {
            if (MenuManager.Instance == null) return null;

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
            {
                if (_backOption == null || _backOption.OptionObject == null)
                    DiagnosticLog.Warning("MultiplayerMenu.Build: existing menu found but BACK reference is stale.");
                return existing;
            }

            _backOption = null;
            _continueOption = null;
            _cachedBackTarget = null;
            _backFader = null;
            _pendingAfterPromptFadeOut = null;
            _waitingForPromptFadeOut = false;
            _lastOpenTime = -999f;

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();

            if (menu == null) return null;

            AddCenteredText(menu, PromptChildName,    PromptText,    0f);
            AddCenteredText(menu, LoggingInChildName, LoggingInText, 0f);
            AddCenteredText(menu, LoggedInChildName,  LoggedInText,  0f);

            if (backTarget != null)
            {
                MenuOption back = new MenuOptionFactory()
                    .SetName("BACK")
                    .SetParent(menu)
                    .SetNextLocation(backTarget)
                    .SetOnClick(() => OnBackClicked())
                    .Build();

                _backOption = back;

                if (back != null && back.OptionObject != null)
                {
                    RectTransform rt = back.OptionObject.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        Vector2 pos = rt.anchoredPosition;
                        pos.y = BackY;
                        rt.anchoredPosition = pos;
                    }

                    CacheBackTargetFromOption(back.OptionObject);

                    // Attach a CanvasGroupFader so BACK fades with the prompt.
                    _backFader = AttachFaderToOption(back.OptionObject);
                }
            }

            try
            {
                _continueOption = new MenuOptionFactory()
                    .SetName(ContinueOptionName)
                    .SetParent(menu)
                    .SetNextLocation(menu)
                    .Build();

                if (_continueOption != null && _continueOption.OptionObject != null)
                {
                    RectTransform rt = _continueOption.OptionObject.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        Vector2 pos = rt.anchoredPosition;
                        pos.y = ContinueOffscreenY;
                        rt.anchoredPosition = pos;
                    }

                    Component next = _continueOption.OptionObject.GetComponent("MenuNextLocation");
                    if (next != null)
                        QBLib.Fields.WriteRef(next, "changeCase", _continueOption.OptionObject);
                    else
                        DiagnosticLog.Warning("MultiplayerMenu: CONTINUE has no MenuNextLocation at build time.");
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenu: CONTINUE option failed: " + ex.Message);
                _continueOption = null;
            }

            menu.MenuObject.AddComponent<MultiplayerMenuEnterHandler>();

            _currentState = LoginState.NotLoggedIn;
            ApplyStateInstant();

            DiagnosticLog.Info("MultiplayerMenu: built '" + PageName + "'.");
            return menu;
        }

        // ── BACK fader attach ──────────────────────────────────────────────

        private static CanvasGroupFader AttachFaderToOption(GameObject optionObject)
        {
            if (optionObject == null) return null;

            try
            {
                CanvasGroup group = optionObject.GetComponent<CanvasGroup>();
                if (group == null)
                    group = optionObject.AddComponent<CanvasGroup>();

                CanvasGroupFader fader = optionObject.GetComponent<CanvasGroupFader>();
                if (fader == null)
                    fader = optionObject.AddComponent<CanvasGroupFader>();

                fader.FadeDuration = FadeDuration;
                fader.Initialize(group, false); // start hidden; OnMenuOpened fades it in

                DiagnosticLog.Info("MultiplayerMenu: BACK fader attached (starts hidden).");
                return fader;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenu: AttachFaderToOption failed: " + ex.Message);
                return null;
            }
        }

        // ── Menu open / close hooks ────────────────────────────────────────

        public static void OnMenuOpened()
        {
            _lastOpenTime = Time.unscaledTime;

            if (_currentState == LoginState.LoggingIn)
            {
                DiagnosticLog.Info("MultiplayerMenu: OnMenuOpened ignored (state=LoggingIn).");
                return;
            }

            if (_currentState != LoginState.NotLoggedIn)
            {
                _currentState = LoginState.NotLoggedIn;
                DiagnosticLog.Info("MultiplayerMenu: OnMenuOpened resetting state to NotLoggedIn.");
            }

            TextFader prompt = GetFader(PromptChildName);
            if (prompt == null) { DiagnosticLog.Warning("MultiplayerMenu: OnMenuOpened — no prompt fader."); return; }

            SetChildActive(PromptChildName, true);
            SetChildActive(LoggingInChildName, true);
            SetChildActive(LoggedInChildName, true);

            TextFader logging = GetFader(LoggingInChildName);
            if (logging != null) logging.SetAlpha(0f);

            prompt.SetAlpha(0f);
            prompt.FadeTo(1f, FadeDuration);

            if (_backFader != null) _backFader.SetVisible(true);

            SetBackEnabled(true);

            DiagnosticLog.Info("MultiplayerMenu: OnMenuOpened — fading prompt + BACK in from 0.");
        }

        public static void OnMenuClosed()
        {
            if (Time.unscaledTime - _lastOpenTime < CloseDebounceSeconds)
            {
                DiagnosticLog.Info("MultiplayerMenu: OnMenuClosed ignored (within " +
                                   CloseDebounceSeconds.ToString("F2") + "s of open).");
                return;
            }

            TextFader prompt  = GetFader(PromptChildName);
            TextFader logging = GetFader(LoggingInChildName);

            if (prompt  != null) prompt.FadeTo(0f, FadeDuration);
            if (logging != null) logging.FadeTo(0f, FadeDuration);
            if (_backFader != null) _backFader.SetVisible(false);

            _pendingAfterPromptFadeOut = null;
            _waitingForPromptFadeOut = false;

            _currentState = LoginState.NotLoggedIn;
            _loginController = null;

            DiagnosticLog.Info("MultiplayerMenu: OnMenuClosed — fading prompt/logging/BACK out, state → NotLoggedIn.");
        }

        private static void OnBackClicked()
        {
            OnMenuClosed();
        }

        // ── Login fade sequence ────────────────────────────────────────────

        public static void StartLoginFade(MonoBehaviour runner)
        {
            if (_currentState != LoginState.NotLoggedIn)
            {
                DiagnosticLog.Info("MultiplayerMenu: StartLoginFade ignored (state=" + _currentState + ").");
                return;
            }

            if (runner == null) return;

            TextFader promptFader  = GetFader(PromptChildName);
            TextFader loggingFader = GetFader(LoggingInChildName);
            if (promptFader == null || loggingFader == null)
            {
                DiagnosticLog.Warning("MultiplayerMenu: StartLoginFade — missing fader.");
                return;
            }

            _currentState = LoginState.LoggingIn;

            SetChildActive(PromptChildName, true);
            SetChildActive(LoggingInChildName, true);
            SetChildActive(LoggedInChildName, true);

            loggingFader.SetAlpha(0f);

            // BACK fades out alongside the prompt.
            if (_backFader != null) _backFader.SetVisible(false);

            _waitingForPromptFadeOut = true;
            _pendingAfterPromptFadeOut = () =>
            {
                loggingFader.SetAlpha(0f);
                loggingFader.FadeTo(1f, FadeDuration);
                DiagnosticLog.Info("MultiplayerMenu: prompt faded out — fading logging-in in, starting login flow.");

                StartLoginFlow();
            };

            promptFader.FadeTo(0f, FadeDuration);

            SetBackEnabled(false);

            DiagnosticLog.Info("MultiplayerMenu: state → LoggingIn. Prompt + BACK fading out; login flow queued.");
        }

        public static void SetNotLoggedIn() { SetState(LoginState.NotLoggedIn); }
        public static void SetLoggingIn()   { SetState(LoginState.LoggingIn); }

        public static void SetLoggedIn()
        {
            GameMenu home = null;
            try { home = MenuManager.Instance != null ? MenuManager.Instance.Find(HomeMenuName) : null; }
            catch { }

            if (home == null)
            {
                GameMenu modeSelect = null;
                try { modeSelect = MenuManager.Instance != null ? MenuManager.Instance.Find("Location Mode Select") : null; }
                catch { }

                try { home = MultiplayerHomeMenu.Build(modeSelect); }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerMenu.SetLoggedIn: MultiplayerHomeMenu.Build threw:\n" + ex.ToString());
                    home = null;
                }
            }

            SetState(LoginState.LoggedIn);

            if (home == null || home.MenuObject == null)
            {
                DiagnosticLog.Warning("MultiplayerMenu: home menu not available; cannot navigate.");
                return;
            }

            DiagnosticLog.Info("MultiplayerMenu: home menu ready ('" + home.MenuObject.name + "').");

            if (_continueOption != null && _continueOption.OptionObject != null)
            {
                try
                {
                    _continueOption.NextLocation = home.MenuObject;

                    var next = _continueOption.OptionObject.GetComponent<MenuNextLocation>();
                    if (next != null)
                    {
                        next.Click();
                        DiagnosticLog.Info("MultiplayerMenu: MenuNextLocation.Click() → '" + home.MenuObject.name + "'.");
                    }
                    else
                    {
                        DiagnosticLog.Warning("MultiplayerMenu: CONTINUE has no MenuNextLocation component.");
                    }
                }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerMenu: CONTINUE Click() failed:\n" + ex.ToString());
                }
            }
            else
            {
                DiagnosticLog.Warning("MultiplayerMenu: CONTINUE option missing; cannot navigate to home.");
            }
        }

        public static void SetState(LoginState next)
        {
            _currentState = next;
            ApplyStateInstant();
            DiagnosticLog.Info("MultiplayerMenu: state → " + next + ".");
        }

        public static void TickLogin(float dt)
        {
            TickPendingFade();

            if (_loginController == null) return;
            _loginController.Tick(dt);
        }

        private static void TickPendingFade()
        {
            if (!_waitingForPromptFadeOut) return;

            TextFader prompt = GetFader(PromptChildName);
            if (prompt == null || prompt.IsFading) return;

            System.Action action = _pendingAfterPromptFadeOut;
            _pendingAfterPromptFadeOut = null;
            _waitingForPromptFadeOut = false;

            if (action != null)
            {
                try { action(); }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerMenu: pending fade action threw: " + ex.Message);
                }
            }
        }

        private static void ApplyStateInstant()
        {
            switch (_currentState)
            {
                case LoginState.NotLoggedIn:
                    SetChildActive(PromptChildName, true);
                    SetChildActive(LoggingInChildName, true);
                    SetChildActive(LoggedInChildName, true);
                    SetBackEnabled(true);
                    break;

                case LoginState.LoggingIn:
                    SetChildActive(PromptChildName, true);
                    SetChildActive(LoggingInChildName, true);
                    SetChildActive(LoggedInChildName, true);
                    SetBackEnabled(false);
                    break;

                case LoginState.LoggedIn:
                    SetChildActive(PromptChildName, true);
                    SetChildActive(LoggingInChildName, true);
                    SetChildActive(LoggedInChildName, true);
                    SetBackEnabled(true);
                    break;
            }
        }

        private static void StartLoginFlow()
        {
            _loginController = new LoginStageController();

            _loginController.Add(new VersionCheckStage());
            _loginController.Add(new RelayConnectStage(IsRelayConnected));
            _loginController.Add(new SessionHandshakeStage(IsHandshakeComplete));

            _loginController.OnStageStarted += stage =>
            {
                DiagnosticLog.Info("LoginStage started: index=" + stage.StageIndex);
            };

            _loginController.OnAllStagesCompleted += () =>
            {
                DiagnosticLog.Info("Login flow complete → LoggedIn.");
                _loginController = null;
                SetLoggedIn();
            };

            _loginController.OnStageFailed += (stage, ex) =>
            {
                DiagnosticLog.Error("Login flow failed at stage " + stage.StageIndex + ": " + ex);
                _loginController = null;
                SetNotLoggedIn();
            };

            _loginController.Start();
        }

        private static bool IsRelayConnected() { return true; }
        private static bool IsHandshakeComplete() { return true; }

        private static void SetBackEnabled(bool enabled)
        {
            try
            {
                if (_backOption == null || _backOption.OptionObject == null)
                {
                    DiagnosticLog.Warning("MultiplayerMenu.SetBackEnabled: BACK option reference is null.");
                    return;
                }

                GameObject optionGO = _backOption.OptionObject;

                Component next = optionGO.GetComponent("MenuNextLocation");
                if (next == null)
                {
                    Transform buttonChild = optionGO.transform.Find("Button");
                    if (buttonChild != null) next = buttonChild.GetComponent("MenuNextLocation");
                }

                if (next == null)
                {
                    DiagnosticLog.Warning("MultiplayerMenu.SetBackEnabled: BACK has no MenuNextLocation.");
                    return;
                }

                if (enabled)
                {
                    if (_cachedBackTarget != null)
                    {
                        QBLib.Fields.WriteRef(next, "nextLocation", _cachedBackTarget);
                        DiagnosticLog.Info("MultiplayerMenu: BACK enabled → " + _cachedBackTarget.name + ".");
                    }
                    else
                    {
                        DiagnosticLog.Warning("MultiplayerMenu.SetBackEnabled(true): no cached target to restore.");
                    }
                }
                else
                {
                    if (_cachedBackTarget == null)
                        CacheBackTargetFromComponent(next);

                    QBLib.Fields.WriteRef(next, "nextLocation", null);
                    DiagnosticLog.Info("MultiplayerMenu: BACK disabled.");
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenu.SetBackEnabled failed: " + ex.Message);
            }
        }

        private static void CacheBackTargetFromOption(GameObject optionGO)
        {
            if (optionGO == null) return;

            Component next = optionGO.GetComponent("MenuNextLocation");
            if (next == null)
            {
                Transform buttonChild = optionGO.transform.Find("Button");
                if (buttonChild != null) next = buttonChild.GetComponent("MenuNextLocation");
            }

            if (next != null) CacheBackTargetFromComponent(next);
        }

        private static void CacheBackTargetFromComponent(Component next)
        {
            try
            {
                Il2CppSystem.Object current = QBLib.Fields.ReadRef<Il2CppSystem.Object>(next, "nextLocation");
                if (current == null) return;

                GameObject go = current.TryCast<GameObject>();
                if (go != null)
                {
                    _cachedBackTarget = go;
                    DiagnosticLog.Info("MultiplayerMenu: cached BACK target '" + go.name + "'.");
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenu.CacheBackTargetFromComponent failed: " + ex.Message);
            }
        }

        private static void SetAlpha(string childName, float alpha)
        {
            TextFader f = GetFader(childName);
            if (f != null) f.SetAlpha(alpha);
        }

        private static void SetChildActive(string childName, bool active)
        {
            try
            {
                if (MenuManager.Instance == null) return;
                GameMenu menu = MenuManager.Instance.Find(PageName);
                if (menu == null || menu.MenuObject == null) return;

                Transform t = menu.MenuObject.transform.Find(childName);
                if (t != null) t.gameObject.SetActive(active);
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenu.SetChildActive('" + childName + "') failed: " + ex.Message);
            }
        }

        private static TextFader GetFader(string childName)
        {
            try
            {
                if (MenuManager.Instance == null) return null;
                GameMenu menu = MenuManager.Instance.Find(PageName);
                if (menu == null || menu.MenuObject == null) return null;

                Transform t = menu.MenuObject.transform.Find(childName);
                if (t == null) return null;
                return t.GetComponent<TextFader>();
            }
            catch { return null; }
        }

        private static void AddCenteredText(GameMenu menu, string childName, string content, float startAlpha)
        {
            if (menu == null || menu.MenuObject == null) return;

            GameObject templateGO = null;
            Transform titleTransform = menu.MenuObject.transform.Find("Text");
            if (titleTransform != null) templateGO = titleTransform.gameObject;

            GameObject go;
            if (templateGO != null)
            {
                go = Object.Instantiate(templateGO, menu.MenuObject.transform);
            }
            else
            {
                go = new GameObject(childName);
                go.transform.SetParent(menu.MenuObject.transform, false);
                go.AddComponent<RectTransform>();
                go.AddComponent<Text>();
            }

            go.name = childName;
            if (go.transform.parent != menu.MenuObject.transform)
                go.transform.SetParent(menu.MenuObject.transform, false);

            StripInterferingComponents(go);

            Text text = go.GetComponent<Text>();
            if (text == null) text = go.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.text = content;
                text.font = GlobalGame.fontUse;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                Color c = text.color;
                c.a = startAlpha;
                text.color = c;
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(PromptX, 0f);
                rt.localScale = Vector3.one;
                rt.sizeDelta = new Vector2(PromptWidth, PromptHeight);
            }

            TextFader fader = go.AddComponent<TextFader>();
            fader.Initialize(text);
        }

        private static void StripInterferingComponents(GameObject go)
        {
            if (go == null) return;

            string[] toStrip = { "Localization_UIText", "UI_Colors", "UIShadow", "UIShiny", "UI_TextFontLanguage" };

            for (int i = 0; i < toStrip.Length; i++)
            {
                string name = toStrip[i];

                Component c = go.GetComponent(name);
                if (c != null) Object.Destroy(c);

                for (int j = 0; j < go.transform.childCount; j++)
                {
                    Transform child = go.transform.GetChild(j);
                    if (child == null) continue;
                    Component cc = child.GetComponent(name);
                    if (cc != null) Object.Destroy(cc);
                }
            }
        }
    }
}