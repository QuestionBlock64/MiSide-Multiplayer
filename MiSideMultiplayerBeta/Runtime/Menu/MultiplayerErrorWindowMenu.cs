using System;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal enum ErrorWindowButtons { OK, YesNo, YesNoCancel }
    internal enum ErrorWindowResult  { None, OK, Yes, No, Cancel }

    internal static class MultiplayerErrorWindowMenu
    {
        public const string PageName  = "Location Multiplayer Error";
        public const string PageTitle = "ERROR";

        private const float OffscreenY = -9000f;

        private static GameMenu   _menu;
        private static Text       _bodyText;
        private static MenuOption _btnPrimary;
        private static MenuOption _btnSecondary;
        private static MenuOption _btnTertiary;
        private static MenuOption _navigator;
        private static Action<ErrorWindowResult> _callback;
        private static GameMenu   _returnTo;
        private static ErrorWindowResult _primaryResult = ErrorWindowResult.OK;
        private static int _showGeneration;
        private static int _firedGeneration = -1;
        private static bool _manualPointerWasDown;

        public static GameMenu Menu { get { return _menu; } }

        // ── Build ───────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the error menu and all of its child elements exist.
        /// Safe to call repeatedly. If the menu already exists, repairs any
        /// missing pieces (body, icon, buttons, navigator) instead of
        /// bailing out early.
        /// </summary>
        public static GameMenu Build()
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: MenuManager not initialized.");
                return null;
            }

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing == null)
            {
                existing = new MenuFactory()
                    .SetObjectName(PageName)
                    .SetTitle(PageTitle)
                    .Build();

                if (existing == null)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: MenuFactory.Build returned null.");
                    return null;
                }
                DiagnosticLog.Info("MultiplayerErrorWindowMenu: built '" + PageName + "'.");
            }

            _menu = existing;
            EnsureChildren();
            return _menu;
        }

        private static void EnsureChildren()
        {
            if (_menu == null || _menu.MenuObject == null) return;

            Transform titleTf = null;
            try
            {
                Transform t = _menu.MenuObject.transform.Find("Text");
                if (t != null) titleTf = t;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: title lookup failed: " + ex.Message);
            }

            if (titleTf == null) titleTf = _menu.MenuObject.transform;

            if (_bodyText == null) BuildBody(titleTf);
            TryBuildIcon(titleTf);

            // MenuOption is a managed wrapper, so its reference can survive a
            // SceneMenu reload after Unity has destroyed its OptionObject.
            // Rebuild from the current menu whenever that happens; otherwise an
            // error page can show its text but have no actionable OK button.
            if (!HasLiveOption(_btnPrimary))
                _btnPrimary = BuildVisibleButton("OK", _menu, () => Fire(ErrorWindowResult.OK));
            if (!HasLiveOption(_btnSecondary))
                _btnSecondary = BuildVisibleButton("NO", _menu, () => Fire(ErrorWindowResult.No));
            if (!HasLiveOption(_btnTertiary))
                _btnTertiary = BuildVisibleButton("CANCEL", _menu, () => Fire(ErrorWindowResult.Cancel));

            EnsureButtonClick(_btnPrimary, PrimaryButtonClicked);
            EnsureButtonClick(_btnSecondary, SecondaryButtonClicked);
            EnsureButtonClick(_btnTertiary, TertiaryButtonClicked);

            ReparentButtonToTitle(_btnPrimary,   titleTf);
            ReparentButtonToTitle(_btnSecondary, titleTf);
            ReparentButtonToTitle(_btnTertiary,  titleTf);

            PositionButtons();

            if (!HasLiveOption(_navigator))
                _navigator = BuildNavigator(_menu);
            ParkOffscreen(_navigator);

            RemoveButtonNavigation(_btnPrimary);
            RemoveButtonNavigation(_btnSecondary);
            RemoveButtonNavigation(_btnTertiary);
        }

        private static bool HasLiveOption(MenuOption option)
        {
            return option != null && option.OptionObject != null;
        }

        private static void BuildBody(Transform titleTf)
        {
            GameObject bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(titleTf, false);
            bodyGo.AddComponent<RectTransform>();
            bodyGo.AddComponent<Text>();

            // Use the title's left edge as the shared layout origin. The body sits
            // directly under ERROR, rather than floating above the menu title.
            RectTransform bodyRt = bodyGo.GetComponent<RectTransform>();
            bodyRt.anchorMin = new Vector2(0f, 0.5f);
            bodyRt.anchorMax = new Vector2(0f, 0.5f);
            bodyRt.pivot     = new Vector2(0f, 0.5f);
            bodyRt.anchoredPosition = new Vector2(0f, -78f);
            bodyRt.sizeDelta = new Vector2(620f, 64f);

            _bodyText = bodyGo.GetComponent<Text>();
            _bodyText.alignment = TextAnchor.MiddleLeft;
            _bodyText.color     = Color.white;
            _bodyText.fontSize  = 30;
            _bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _bodyText.verticalOverflow   = VerticalWrapMode.Overflow;
            _bodyText.raycastTarget = false;
            if (GlobalGame.fontUse != null)
                _bodyText.font = GlobalGame.fontUse;
        }

        private static MenuOption BuildVisibleButton(string label, GameMenu parent, Action onClick)
        {
            try
            {
                MenuOption opt = new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(parent)
                    .SetOnClick(onClick)
                    .Build();

                if (opt != null && opt.OptionObject != null)
                {
                    MenuNextLocation nl = opt.OptionObject.GetComponent<MenuNextLocation>();
                    // ButtonMouseClick.PointerDown calls Click() whenever this
                    // component exists, even when disabled. Fire() owns the
                    // button route, so native navigation must be removed.
                    if (nl != null) UnityEngine.Object.Destroy(nl);

                    ButtonMouseClick click = opt.OptionObject.GetComponent<ButtonMouseClick>();
                    if (click != null) click.ActivationInteractive(true);
                }
                return opt;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: button '" + label +
                                      "' failed: " + ex.Message);
                return null;
            }
        }

        private static void EnsureButtonClick(MenuOption option, Action handler)
        {
            if (option == null || option.OptionObject == null || handler == null) return;
            try
            {
                ButtonMouseClick mouseClick = option.OptionObject.GetComponent<ButtonMouseClick>();
                if (mouseClick == null)
                    mouseClick = option.OptionObject.AddComponent<ButtonMouseClick>();
                if (mouseClick != null)
                {
                    mouseClick.ActivationInteractive(true);
                    if (mouseClick.eventClick == null)
                        mouseClick.eventClick = new UnityEngine.Events.UnityEvent();
                    mouseClick.eventClick.RemoveAllListeners();
                    mouseClick.eventClick.AddListener(handler);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: custom click wiring failed: " + ex.Message);
            }

            // The game's pointer handler is not consistent across menu
            // prefabs. Wire Unity's standard Button too, so either input path
            // executes the same idempotent result handler.
            try
            {
                Button button = option.OptionObject.GetComponent<Button>();
                if (button == null)
                    button = option.OptionObject.GetComponentInChildren<Button>(true);
                if (button == null)
                    button = option.OptionObject.AddComponent<Button>();

                if (button != null)
                {
                    Graphic targetGraphic = button.targetGraphic;
                    if (targetGraphic == null)
                        targetGraphic = option.OptionObject.GetComponentInChildren<Graphic>(true);
                    if (targetGraphic == null)
                    {
                        Image hitTarget = option.OptionObject.AddComponent<Image>();
                        hitTarget.color = new Color(1f, 1f, 1f, 0f);
                        hitTarget.raycastTarget = true;
                        targetGraphic = hitTarget;
                    }

                    button.targetGraphic = targetGraphic;
                    if (targetGraphic != null) targetGraphic.raycastTarget = true;
                    button.enabled = true;
                    button.interactable = true;
                    Navigation navigation = button.navigation;
                    navigation.mode = Navigation.Mode.None;
                    button.navigation = navigation;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(handler);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: Unity Button fallback wiring failed: " + ex.Message);
            }
        }

        private static void PrimaryButtonClicked() { Fire(_primaryResult); }
        private static void SecondaryButtonClicked() { Fire(ErrorWindowResult.No); }
        private static void TertiaryButtonClicked() { Fire(ErrorWindowResult.Cancel); }

        /// <summary>
        /// Last-resort input route for the error dialog, polled on the main
        /// thread in case a game's menu prefab stops forwarding UI events.
        /// </summary>
        public static void Tick()
        {
            if (_menu == null || _menu.MenuObject == null ||
                _btnPrimary == null || _btnPrimary.OptionObject == null ||
                !_btnPrimary.OptionObject.activeInHierarchy ||
                !IsLocationActive(_menu) ||
                _firedGeneration == _showGeneration)
            {
                _manualPointerWasDown = false;
                return;
            }

            try
            {
                if (Input.GetKeyDown(KeyCode.Return) ||
                    Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) ||
                    Input.GetKeyDown(KeyCode.Escape))
                {
                    Fire(_primaryResult);
                    return;
                }

                bool pointerDown = Input.GetMouseButtonDown(0);
                if (pointerDown && !_manualPointerWasDown && PointerOverPrimaryButton())
                    Fire(_primaryResult);
                _manualPointerWasDown = pointerDown;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Verbose("MultiplayerErrorWindowMenu: manual input fallback unavailable: " + ex.Message);
            }
        }

        private static bool PointerOverPrimaryButton()
        {
            RectTransform rect = _btnPrimary.OptionObject.GetComponent<RectTransform>();
            if (rect == null) return false;

            Camera eventCamera = null;
            try
            {
                Canvas canvas = _btnPrimary.OptionObject.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    eventCamera = canvas.worldCamera;
            }
            catch { }

            return RectTransformUtility.RectangleContainsScreenPoint(
                rect, Input.mousePosition, eventCamera);
        }

        private static void RemoveButtonNavigation(MenuOption option)
        {
            if (option == null || option.OptionObject == null) return;
            try
            {
                MenuNextLocation next = option.OptionObject.GetComponent<MenuNextLocation>();
                if (next != null) UnityEngine.Object.Destroy(next);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: removing button navigation failed: " + ex.Message);
            }
        }

        private static MenuOption BuildNavigator(GameMenu parent)
        {
            try
            {
                MenuOption opt = new MenuOptionFactory()
                    .SetName("__navigator")
                    .SetParent(parent)
                    .SetNextLocation(parent)
                    .SetObjectName("__navigator")
                    .Build();

                DiagnosticLog.Info("MultiplayerErrorWindowMenu: navigator built.");
                return opt;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigator build failed: " + ex.Message);
                return null;
            }
        }

        private static void ParkOffscreen(MenuOption opt)
        {
            if (opt == null || opt.OptionObject == null) return;
            try
            {
                RectTransform rt = opt.OptionObject.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 pos = rt.anchoredPosition;
                    pos.y = OffscreenY;
                    rt.anchoredPosition = pos;
                }
                Text t = opt.OptionObject.GetComponentInChildren<Text>();
                if (t != null) t.color = new Color(0f, 0f, 0f, 0f);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: park failed: " + ex.Message);
            }
        }

        private static void ReparentButtonToTitle(MenuOption opt, Transform titleTf)
        {
            if (opt == null || opt.OptionObject == null || titleTf == null) return;
            if (opt.OptionObject.transform.parent == titleTf) return;
            try
            {
                opt.OptionObject.transform.SetParent(titleTf, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: reparent failed: " + ex.Message);
            }
        }

        private static void PositionButtons()
        {
            MenuOption[] all = { _btnPrimary, _btnSecondary, _btnTertiary };
            int count = 0;
            foreach (MenuOption o in all) if (o != null && o.OptionObject != null) count++;
            if (count == 0) return;

            const float spacing = 20f;
            const float width   = 220f;
            const float y       = -240f;

            float totalWidth = count * width + (count - 1) * spacing;
            float startX = -totalWidth * 0.5f + width * 0.5f;

            int i = 0;
            foreach (MenuOption o in all)
            {
                if (o == null || o.OptionObject == null) continue;

                RectTransform rt = o.OptionObject.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot     = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = new Vector2(startX + i * (width + spacing), y);
                    rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
                }
                i++;
            }
        }

        private static void TryBuildIcon(Transform titleTf)
        {
            if (_menu == null || _menu.MenuObject == null || titleTf == null) return;

            // WarningIcon is a child of the title so it moves with the title.
            if (titleTf.Find("WarningIcon") != null) return;

            Sprite icon = ErrorWindowIcon.Load();
            if (icon == null) return;

            GameObject iconGo = new GameObject("WarningIcon");
            iconGo.transform.SetParent(titleTf, false);
            iconGo.AddComponent<RectTransform>();
            iconGo.AddComponent<Image>();

            // Position the icon immediately after ERROR on the same baseline.
            RectTransform rt = iconGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot     = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(160f, 0f);
            rt.sizeDelta = new Vector2(72f, 72f);

            Image img = iconGo.GetComponent<Image>();
            img.sprite = icon;
            img.preserveAspect = true;
            img.raycastTarget = false;
        }

        // ── Show / Hide ─────────────────────────────────────────────────────

        public static void Show(
            GameMenu returnTo,
            string message,
            ErrorWindowButtons buttons,
            Action<ErrorWindowResult> onResult,
            string title = PageTitle)
        {
            DiagnosticLog.Info("MultiplayerErrorWindowMenu: Show ENTER.");

            GameMenu resolvedReturn = ResolveReturnTarget(returnTo) ?? ResolveFirstMenuLocation();

            // Always ensure children exist, even if the menu was already
            // built in a prior session where children failed to attach.
            GameMenu m = Build();
            if (m == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: Show failed, menu not built.");
                if (resolvedReturn != null && !ForceNavigateTo(resolvedReturn))
                    SetLocationActive(resolvedReturn, true);
                if (onResult != null) onResult(ErrorWindowResult.OK);
                return;
            }

            _returnTo = resolvedReturn;
            if (_returnTo == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: no return page is available; OK will hide the dialog.");
            }
            if (returnTo != null && _returnTo != returnTo)
            {
                string fallbackName = _returnTo != null && _returnTo.MenuObject != null
                    ? _returnTo.MenuObject.name : "<none>";
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: requested return page is unavailable; using '" + fallbackName + "'.");
            }
            _callback = onResult;
            _showGeneration++;
            _firedGeneration = -1;

            try { _menu.Title = string.IsNullOrEmpty(title) ? PageTitle : title; }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: could not set title: " + ex.Message);
            }

            if (_bodyText != null)
                _bodyText.text = message ?? "";

            ApplyButtons(buttons);

            bool navigated = NavigateTo(_menu);
            if ((!navigated || !IsLocationActive(_menu)) && _menu.MenuObject != null &&
                !ForceNavigateTo(_menu))
                SetLocationActive(_menu, true);

            // Make the primary action focusable for keyboard/controller users.
            try
            {
                if (_btnPrimary != null && _btnPrimary.OptionObject != null)
                {
                    _btnPrimary.OptionObject.SetActive(true);
                    EventSystem.current?.SetSelectedGameObject(_btnPrimary.OptionObject);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: primary selection failed: " + ex.Message);
            }
        }

        public static void Hide()
        {
            _callback = null;
            if (_menu != null && _menu.MenuObject != null)
                SetLocationActive(_menu, false);
        }

        private static bool NavigateTo(GameMenu target)
        {
            if (target == null || target.MenuObject == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: NavigateTo null target.");
                return false;
            }

            if (_navigator == null || _navigator.OptionObject == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigator missing; rebuilding.");
                _navigator = BuildNavigator(_menu);
                ParkOffscreen(_navigator);
            }

            if (_navigator == null || _navigator.OptionObject == null)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigator still missing; cannot navigate.");
                return false;
            }

            try
            {
                MenuNextLocation nl = _navigator.OptionObject.GetComponent<MenuNextLocation>();
                if (nl == null)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigator has no MenuNextLocation.");
                    return false;
                }

                GameObject world = GameObject.FindWithTag("World");
                global::Menu gameMenu = world == null ? null : world.GetComponent<global::Menu>();
                if (gameMenu == null)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: cannot find the active game Menu for navigation.");
                    return false;
                }

                // The hidden navigator belongs to the inactive target menu, so Unity has
                // not necessarily run MenuNextLocation.Start(). Seed the same state that
                // Start() and a real button click would provide before invoking Click().
                GameObject focusTarget = FindFocusTarget(target) ?? _navigator.OptionObject;
                bool menuAssigned = QBLib.Fields.WriteRef(nl, "menu", gameMenu);
                bool focusAssigned = QBLib.Fields.WriteRef(nl, "changeCase", focusTarget);
                if (!menuAssigned || !focusAssigned)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigator initialization failed " +
                                          "(menu=" + menuAssigned + ", changeCase=" + focusAssigned + ").");
                    return false;
                }

                DiagnosticLog.Info("MultiplayerErrorWindowMenu: navigating to '" + target.MenuObject.name + "'.");
                nl.nextLocation = target.MenuObject;
                nl.enabled = true;
                nl.Click();
                DiagnosticLog.Info("MultiplayerErrorWindowMenu: Click() returned.");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: navigation failed: " + ex);
                return false;
            }
        }

        private static GameMenu ResolveReturnTarget(GameMenu requested)
        {
            if (requested != null && requested.MenuObject != null) return requested;
            if (MenuManager.Instance == null) return null;

            GameMenu found = null;
            try { found = MenuManager.Instance.Find(MultiplayerHomeMenu.PageName); } catch { }
            if (found != null && found.MenuObject != null) return found;

            try { found = MenuManager.Instance.Find(MultiplayerMenu.PageName); } catch { }
            if (found != null && found.MenuObject != null) return found;

            try { found = MenuManager.Instance.Find(ModeSelectMenu.PageName); } catch { }
            if (found != null && found.MenuObject != null) return found;

            try { ModeSelectMenu.Build(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: rebuilding the menu chain failed: " + ex.Message);
            }

            try { found = MenuManager.Instance.Find(MultiplayerHomeMenu.PageName); } catch { }
            if (found != null && found.MenuObject != null) return found;
            try { found = MenuManager.Instance.Find(MultiplayerMenu.PageName); } catch { }
            if (found != null && found.MenuObject != null) return found;
            try { found = MenuManager.Instance.Find(ModeSelectMenu.PageName); } catch { }
            return found != null && found.MenuObject != null ? found : null;
        }

        private static GameMenu ResolveFirstMenuLocation()
        {
            try
            {
                GameObject world = GameObject.FindWithTag("World");
                global::Menu gameMenu = world == null ? null : world.GetComponent<global::Menu>();
                GameObject first = gameMenu == null ? null : gameMenu.firstLocation;
                if (first != null && first.GetComponent<MenuLocation>() != null)
                    return new GameMenu(first);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: first-location fallback failed: " + ex.Message);
            }

            try
            {
                GameMenu vanilla = MenuManager.Instance != null
                    ? MenuManager.Instance.Find(ModeSelectMenu.VanillaMainMenu) : null;
                if (vanilla != null && vanilla.MenuObject != null) return vanilla;
            }
            catch { }
            return null;
        }

        private static bool ForceNavigateTo(GameMenu target)
        {
            if (target == null || target.MenuObject == null) return false;
            try
            {
                GameObject world = GameObject.FindWithTag("World");
                global::Menu gameMenu = world == null ? null : world.GetComponent<global::Menu>();
                if (gameMenu == null) return false;

                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: forcing navigation to '" + target.MenuObject.name + "'.");
                gameMenu.OpenNextLocation(target.MenuObject);

                GameObject focusTarget = FindFocusTarget(target);
                try
                {
                    ButtonMouseMenu buttonMenu = gameMenu.GetComponent<ButtonMouseMenu>();
                    if (buttonMenu != null && focusTarget != null &&
                        focusTarget.GetComponent<ButtonMouseClick>() != null)
                        buttonMenu.ChangeCase(focusTarget);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: fallback focus assignment failed: " + ex.Message);
                }

                return IsLocationActive(target);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: direct game-menu navigation failed: " + ex.Message);
                return false;
            }
        }

        private static GameObject FindFocusTarget(GameMenu target)
        {
            if (target == null || target.MenuObject == null) return null;
            if (target == _menu && _btnPrimary != null && _btnPrimary.OptionObject != null &&
                _btnPrimary.OptionObject.activeSelf)
                return _btnPrimary.OptionObject;

            try
            {
                foreach (MenuOption option in target.MenuOptions)
                {
                    if (option == null || option.OptionObject == null || !option.OptionObject.activeSelf)
                        continue;
                    if (option.OptionObject.GetComponent<ButtonMouseClick>() != null)
                        return option.OptionObject;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: destination focus lookup failed: " + ex.Message);
            }
            return null;
        }

        private static void ApplyButtons(ErrorWindowButtons which)
        {
            switch (which)
            {
                case ErrorWindowButtons.OK:
                    _primaryResult = ErrorWindowResult.OK;
                    SetVisible(_btnPrimary,   true,  "OK");
                    SetVisible(_btnSecondary, false, "NO");
                    SetVisible(_btnTertiary,  false, "CANCEL");
                    break;
                case ErrorWindowButtons.YesNo:
                    _primaryResult = ErrorWindowResult.Yes;
                    SetVisible(_btnPrimary,   true,  "YES");
                    SetVisible(_btnSecondary, true,  "NO");
                    SetVisible(_btnTertiary,  false, "CANCEL");
                    break;
                case ErrorWindowButtons.YesNoCancel:
                    _primaryResult = ErrorWindowResult.Yes;
                    SetVisible(_btnPrimary,   true,  "YES");
                    SetVisible(_btnSecondary, true,  "NO");
                    SetVisible(_btnTertiary,  true,  "CANCEL");
                    break;
            }

            PositionButtons();
        }

        private static void SetVisible(MenuOption opt, bool visible, string label)
        {
            if (opt == null || opt.OptionObject == null) return;
            opt.OptionObject.SetActive(visible);
            try
            {
                ButtonMouseClick click = opt.OptionObject.GetComponent<ButtonMouseClick>();
                if (click != null) click.ActivationInteractive(visible);
            }
            catch { }
            try
            {
                Button button = opt.OptionObject.GetComponent<Button>();
                if (button == null) button = opt.OptionObject.GetComponentInChildren<Button>(true);
                if (button != null)
                {
                    button.enabled = visible;
                    button.interactable = visible;
                }
            }
            catch { }
            try { opt.Text = label; } catch { }
        }

        private static void Fire(ErrorWindowResult result)
        {
            if (_firedGeneration == _showGeneration) return;
            _firedGeneration = _showGeneration;
            DiagnosticLog.Info("MultiplayerErrorWindowMenu: Fire result=" + result + ".");

            Action<ErrorWindowResult> cb = _callback;
            GameMenu returnTo = _returnTo;
            _callback = null;
            _returnTo = null;

            bool navigated = returnTo != null && NavigateTo(returnTo);
            if (returnTo != null)
            {
                if ((!navigated || !IsLocationActive(returnTo)) && !ForceNavigateTo(returnTo))
                    SetLocationActive(returnTo, true);
                if (_menu != null && _menu.MenuObject != null)
                    SetLocationActive(_menu, false);
            }
            else if (returnTo == null)
            {
                Hide();
            }

            if (cb != null)
            {
                try { cb(result); }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerErrorWindowMenu: callback threw: " + ex.Message);
                }
            }
        }

        private static bool IsLocationActive(GameMenu menu)
        {
            if (menu == null || menu.MenuObject == null) return false;
            try
            {
                MenuLocation location = menu.MenuObject.GetComponent<MenuLocation>();
                if (location != null)
                    return QBLib.Fields.ReadBool(location, "active", false);
            }
            catch { }
            return menu.MenuObject.activeInHierarchy;
        }

        private static void SetLocationActive(GameMenu menu, bool active)
        {
            if (menu == null || menu.MenuObject == null) return;
            try
            {
                MenuLocation location = menu.MenuObject.GetComponent<MenuLocation>();
                if (location != null)
                {
                    location.Active(active);
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: MenuLocation.Active fallback failed: " + ex.Message);
            }

            try { menu.MenuObject.SetActive(active); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerErrorWindowMenu: GameObject activation fallback failed: " + ex.Message);
            }
        }
    }
}
