using System;
using HarmonyLib;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Events;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.API.Patches;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed partial class MiSideMultiplayerPlugin
    {
        private static MiSideMultiplayerPlugin activeSettingsPlugin;

        private bool runtimeReconfigurePending;

        private GameMenu mpPage;
        private MultiplayerSettingsValues draft;
        private EditableField editingField;

        private const string PageName = "Location Multiplayer Settings";
        private const string MainOptionsName = "Location MainOptions";

        private void InitializeMultiplayerSettings()
        {
            activeSettingsPlugin = this;

            SceneLoadedEvent.RegisterEvent();

            new Harmony("com.misidemultiplayer.qblib.menu")
                .CreateClassProcessor(typeof(MenuPatch))
                .Patch();

            InitializedEvent.AddEventListener(OnMenuReady);
        }

        private static void TickMultiplayerSettings()
        {
            MiSideMultiplayerPlugin plugin = activeSettingsPlugin;
            if (plugin == null) return;

            if (plugin.runtimeReconfigurePending)
                plugin.RecreateRuntimeServices();

            plugin.TickEditable();
        }

        private static void DrawMultiplayerSettingsGui()
        {
            MiSideMultiplayerPlugin plugin = activeSettingsPlugin;
            if (plugin == null) return;

            plugin.OnGuiEditable();
        }

        // ── MenuLib hookup ──────────────────────────────────────────────────

        private void OnMenuReady()
        {
            try
            {
                if (mpPage != null) return;

                mpPage = QBLib.FindMenu(PageName);
                if (mpPage == null)
                {
                    Log.LogWarning("Could not find '" + PageName + "'.");
                    return;
                }

                if (mpPage.Find("DISPLAY NAME:") != null) return;

                draft = ReadRuntimeSettings();

                AddTextRow("DISPLAY NAME", "displayName");
                AddTextRow("PLAYER ID",    "playerId");
                AddTextRow("RELAY HOST",   "host");
                AddTextRow("RELAY PORT",   "port");
                AddTextRow("ROOM NAME",    "room");
                AddToggleRow("MULTIPLAYER",     "networking");
                AddToggleRow("SEND SNAPSHOTS",  "snapshots");
                AddToggleRow("DEBUG LOGGING",   "debug");
                AddActionRow("APPLY & RECONNECT", "apply");

                if (mpPage.Find("BACK") == null)
                {
                    GameMenu mainOptions = QBLib.FindMenu(MainOptionsName);
                    if (mainOptions != null)
                    {
                        new MenuOptionFactory()
                            .SetName("BACK")
                            .SetParent(mpPage)
                            .SetNextLocation(mainOptions)
                            .Build();
                    }
                }

                Log.LogInfo("Multiplayer settings menu rows built.");
            }
            catch (Exception ex)
            {
                Log.LogError("OnMenuReady failed: " + ex);
            }
        }

        // ── Row construction ────────────────────────────────────────────────

        private void AddTextRow(string label, string key)
        {
            string display = label + ": " + RawValue(key);

            MenuOption option = QBLib.NewOption()
                .SetName(display)
                .SetParent(mpPage)
                .SetNextLocation(mpPage)
                .SetOnClick(() => OnTextRowClicked(key))
                .Build();

            if (option == null) return;
            NeutralizeNavigation(option);
        }

        private void AddToggleRow(string label, string key)
        {
            string display = label + ": " + RawValue(key);

            MenuOption option = QBLib.NewOption()
                .SetName(display)
                .SetParent(mpPage)
                .SetNextLocation(mpPage)
                .SetOnClick(() => OnToggleRowClicked(key))
                .Build();

            if (option == null) return;
            NeutralizeNavigation(option);
        }

        private void AddActionRow(string label, string key)
        {
            MenuOption option = QBLib.NewOption()
                .SetName(label)
                .SetParent(mpPage)
                .SetNextLocation(mpPage)
                .SetOnClick(() => OnActionRowClicked(key))
                .Build();

            if (option == null) return;
            NeutralizeNavigation(option);
        }

        private void NeutralizeNavigation(MenuOption option)
        {
            if (option == null || option.OptionObject == null) return;

            try
            {
                MenuNextLocation ml = option.OptionObject.GetComponent<MenuNextLocation>();
                if (ml != null)
                {
                    ml.nextLocation = null;
                    ml.enabled = false;
                }
            }
            catch { }

            try
            {
                MenuNextLocation ml = option.OptionObject.GetComponent<MenuNextLocation>();
                if (ml != null)
                {
                    var f = ml.GetType().GetField("changeCase",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public);
                    if (f != null) f.SetValue(ml, null);
                }
            }
            catch { }
        }

        // ── Row click handlers ──────────────────────────────────────────────

        private void OnTextRowClicked(string key)
        {
            if (draft == null) return;

            if (editingField != null && editingField.Key == key)
            {
                CommitEdit();
                return;
            }

            if (editingField != null)
                CommitEdit();

            StartEditing(key);
        }

        private void OnToggleRowClicked(string key)
        {
            if (editingField != null)
                CommitEdit();

            switch (key)
            {
                case "networking": draft.EnableNetworking = !draft.EnableNetworking; break;
                case "snapshots":  draft.EmitLocalSnapshots = !draft.EmitLocalSnapshots; break;
                case "debug":      draft.DebugLogging = !draft.DebugLogging; break;
            }

            RefreshRows();
        }

        private void OnActionRowClicked(string key)
        {
            if (editingField != null)
                CommitEdit();

            if (key == "apply")
                ApplyDraft();
        }

        // ── Labels ──────────────────────────────────────────────────────────

        private string RawValue(string key)
        {
            switch (key)
            {
                case "displayName": return draft.DisplayName ?? "";
                case "playerId":    return draft.LocalPlayerId ?? "";
                case "host":        return draft.ServerHost ?? "";
                case "port":        return draft.ServerPort.ToString();
                case "room":        return draft.RoomName ?? "";
                case "networking":  return draft.EnableNetworking ? "ON" : "OFF";
                case "snapshots":   return draft.EmitLocalSnapshots ? "ON" : "OFF";
                case "debug":       return draft.DebugLogging ? "ON" : "OFF";
                case "apply":       return "";
            }
            return "";
        }

        private string PrettyKey(string key)
        {
            switch (key)
            {
                case "displayName": return "DISPLAY NAME";
                case "playerId":    return "PLAYER ID";
                case "host":        return "RELAY HOST";
                case "port":        return "RELAY PORT";
                case "room":        return "ROOM NAME";
                case "networking":  return "MULTIPLAYER";
                case "snapshots":   return "SEND SNAPSHOTS";
                case "debug":       return "DEBUG LOGGING";
                case "apply":       return "APPLY & RECONNECT";
            }
            return key;
        }

        private void RefreshRows()
        {
            if (mpPage == null) return;

            RefreshOne("displayName");
            RefreshOne("playerId");
            RefreshOne("host");
            RefreshOne("port");
            RefreshOne("room");
            RefreshOne("networking");
            RefreshOne("snapshots");
            RefreshOne("debug");
        }

        private void RefreshOne(string key)
        {
            if (mpPage == null) return;

            string pretty = PrettyKey(key);
            bool isEditing = editingField != null && editingField.Key == key;

            foreach (MenuOption option in mpPage.MenuOptions)
            {
                string txt = option.Text ?? "";
                if (txt == pretty ||
                    txt.StartsWith(pretty + ":") ||
                    txt.StartsWith("> " + pretty))
                {
                    string prefix = isEditing ? "> " : "";
                    string display = IsAction(key)
                        ? pretty
                        : (prefix + pretty + ": " + RawValue(key));
                    option.Text = display;
                    return;
                }
            }
        }

        private bool IsAction(string key)
        {
            return key == "apply";
        }

        // ── Inline editing ──────────────────────────────────────────────────

        private sealed class EditableField
        {
            public string Key;
            public string Value;
            public int Cursor;
        }

        private void StartEditing(string key)
        {
            if (draft == null) return;

            editingField = new EditableField
            {
                Key = key,
                Value = RawValue(key)
            };
            editingField.Cursor = editingField.Value.Length;

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            RefreshOne(key);
        }

        private void TickEditable()
        {
            if (editingField == null) return;

            int blink = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2;
            string v = editingField.Value ?? "";
            int cursor = Mathf.Clamp(editingField.Cursor, 0, v.Length);
            string shown = blink == 0
                ? v.Insert(cursor, "_")
                : v.Insert(cursor, " ");

            SetLabel(editingField.Key, "> " + PrettyKey(editingField.Key) + ": " + shown);
        }

        private void OnGuiEditable()
        {
            if (Event.current == null) return;

            // If a text row is being edited, keyboard input goes to the editor.
            if (editingField != null)
            {
                Event e = Event.current;
                if (e.type != EventType.KeyDown) return;

                if (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    CommitEdit();
                    e.Use();
                    return;
                }

                string value = editingField.Value ?? "";

                if (e.keyCode == KeyCode.Backspace)
                {
                    if (editingField.Cursor > 0)
                    {
                        value = value.Remove(editingField.Cursor - 1, 1);
                        editingField.Cursor--;
                    }
                }
                else if (e.keyCode == KeyCode.Delete)
                {
                    if (editingField.Cursor < value.Length)
                        value = value.Remove(editingField.Cursor, 1);
                }
                else if (e.keyCode == KeyCode.LeftArrow)
                {
                    editingField.Cursor = Mathf.Max(0, editingField.Cursor - 1);
                }
                else if (e.keyCode == KeyCode.RightArrow)
                {
                    editingField.Cursor = Mathf.Min(value.Length, editingField.Cursor + 1);
                }
                else if (e.character != '\0' && !char.IsControl(e.character) && value.Length < 96)
                {
                    value = value.Insert(editingField.Cursor, e.character.ToString());
                    editingField.Cursor++;
                }
                else return;

                editingField.Value = value;
                e.Use();
                return;
            }

            // No edit in progress: Escape acts like clicking BACK.
            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Escape)
            {
                TriggerBack();
                Event.current.Use();
            }
        }

        /// <summary>
        /// Fire the same action the BACK button on our page would.
        /// </summary>
        private void TriggerBack()
        {
            if (mpPage == null) return;

            // Find the BACK button on our page and invoke its OnClick.
            foreach (MenuOption option in mpPage.MenuOptions)
            {
                if (option.Text == "BACK")
                {
                    try
                    {
                        UnityEngine.Events.UnityEvent ev = option.OnClick;
                        if (ev != null)
                        {
                            ev.Invoke();
                            return;
                        }
                    }
                    catch { }

                    // Fallback: do the MenuLocation transition ourselves.
                    try
                    {
                        GameMenu mainOptions = QBLib.FindMenu(MainOptionsName);
                        if (mainOptions != null)
                        {
                            MenuLocation mpLoc = mpPage.MenuObject.GetComponent<MenuLocation>();
                            if (mpLoc != null) mpLoc.Active(false);

                            MenuLocation mainLoc = mainOptions.MenuObject.GetComponent<MenuLocation>();
                            if (mainLoc != null) mainLoc.Active(true);

                            mainOptions.MenuObject.SetActive(true);
                        }
                    }
                    catch { }

                    return;
                }
            }
        }

        private void CommitEdit()
        {
            if (editingField == null) return;

            string value = editingField.Value ?? "";

            switch (editingField.Key)
            {
                case "displayName": draft.DisplayName = value; break;
                case "playerId":    draft.LocalPlayerId = value; break;
                case "host":        draft.ServerHost = value; break;
                case "port":
                    int p;
                    if (int.TryParse(value, out p)) draft.ServerPort = p;
                    break;
                case "room":        draft.RoomName = value; break;
            }

            editingField = null;
            RefreshRows();
        }

        private void SetLabel(string key, string label)
        {
            if (mpPage == null) return;

            string pretty = PrettyKey(key);

            foreach (MenuOption option in mpPage.MenuOptions)
            {
                string txt = option.Text ?? "";
                if (txt == pretty ||
                    txt.StartsWith(pretty + ":") ||
                    txt.StartsWith("> " + pretty))
                {
                    option.Text = label;
                    return;
                }
            }
        }

        // ── Config plumbing ─────────────────────────────────────────────────

        private MultiplayerSettingsValues ReadRuntimeSettings()
        {
            return new MultiplayerSettingsValues
            {
                LocalPlayerId      = localPlayerId.Value,
                DisplayName        = displayName.Value,
                EnableNetworking   = enableNetworking.Value,
                ServerHost         = serverHost.Value,
                ServerPort         = serverPort.Value,
                RoomName           = roomName.Value,
                EmitLocalSnapshots = emitLocalSnapshots.Value,
                DebugLogging       = debugLogging.Value
            };
        }

        private void ApplyDraft()
        {
            if (draft == null) return;

            if (string.IsNullOrWhiteSpace(draft.LocalPlayerId)) { Log.LogWarning("MP: Player ID empty."); return; }
            if (string.IsNullOrWhiteSpace(draft.ServerHost)) { Log.LogWarning("MP: Relay host empty."); return; }
            if (draft.ServerPort < 1 || draft.ServerPort > 65535) { Log.LogWarning("MP: Port 1-65535."); return; }
            if (string.IsNullOrWhiteSpace(draft.RoomName)) { Log.LogWarning("MP: Room name empty."); return; }

            localPlayerId.Value      = draft.LocalPlayerId.Trim();
            displayName.Value        = draft.DisplayName ?? "";
            enableNetworking.Value   = draft.EnableNetworking;
            serverHost.Value         = draft.ServerHost.Trim();
            serverPort.Value         = draft.ServerPort;
            roomName.Value           = draft.RoomName.Trim();
            emitLocalSnapshots.Value = draft.EmitLocalSnapshots;
            debugLogging.Value       = draft.DebugLogging;

            DiagnosticLog.Enabled = debugLogging.Value;

            // Save the config so it persists between sessions.
            try
            {
                Config.Save();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not save BepInEx config: " + ex.Message);
            }

            TryWriteVersionFile();

            runtimeReconfigurePending = true;
            RefreshRows();

            Log.LogInfo("Multiplayer settings applied.");
        }

        private void RecreateRuntimeServices()
        {
            runtimeReconfigurePending = false;

            RuntimeServices previous = runtimeServices;
            runtimeServices = null;

            try
            {
                if (previous != null) previous.Dispose();

                MiSideMultiplayerRuntime driver = MiSideMultiplayerPlugin.RuntimeDriver;
                if (driver == null)
                    driver = UnityEngine.Object.FindObjectOfType<MiSideMultiplayerRuntime>();

                if (driver == null)
                {
                    Log.LogWarning("Could not apply multiplayer settings: runtime object missing.");
                    return;
                }

                runtimeServices = new RuntimeServices(
                    driver.transform,
                    localPlayerId.Value,
                    displayName.Value,
                    SplitCsv(visualRootCandidates.Value),
                    SplitCsv(playerRootCandidates.Value),
                    snapshotSendRate.Value,
                    snapshotHeartbeatSeconds.Value,
                    emitLocalSnapshots.Value,
                    enableNetworking.Value,
                    serverHost.Value,
                    serverPort.Value,
                    roomName.Value,
                    () => displayName.Value);

                Log.LogInfo("Multiplayer settings applied; multiplayer runtime reconnected.");
            }
            catch (Exception ex)
            {
                Log.LogError("Could not recreate multiplayer runtime: " + ex);
            }
        }
    }
}