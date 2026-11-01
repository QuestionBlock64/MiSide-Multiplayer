using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Settings
{
    /// <summary>
    /// Populates and manages the Location Multiplayer Settings menu.
    /// The menu itself is built by Runtime/Menu/MultiplayerSettingsMenu.cs;
    /// this class only adds and maintains rows.
    /// </summary>
    public sealed class MultiplayerSettingsController
    {
        private GameMenu _page;

        private string _displayName;
        private string _playerId;
        private bool   _debugLogging;

        private string _editingKey;
        private string _editingValue;
        private int    _editingCursor;

        private MenuOption _rowDisplayName;
        private MenuOption _rowPlayerId;
        private MenuOption _rowDebug;

        public bool IsOpen => _page != null;

        public void Populate(GameMenu page)
        {
            if (page == null)
            {
                DiagnosticLog.Warning("MultiplayerSettingsController: Populate called with null page.");
                return;
            }

            _page = page;

            if (_rowDisplayName != null && _rowDisplayName.OptionObject != null)
                return;

            LoadFromConfig();

            _rowDisplayName = BuildRow("DISPLAY NAME", RowDisplayName(), OnDisplayNameClicked);
            _rowPlayerId    = BuildRow("PLAYER ID",    RowPlayerId(),    null);
            _rowDebug       = BuildRow("DEBUG LOGGING", RowToggle(_debugLogging), OnDebugClicked);

            DiagnosticLog.Info("MultiplayerSettingsController: rows populated on '" + page.MenuObject.name + "'.");
        }

        private void LoadFromConfig()
        {
            _displayName  = MiSideMultiplayerPlugin.GetDisplayName();
            _playerId     = MiSideMultiplayerPlugin.GetLocalPlayerId();
            _debugLogging = MiSideMultiplayerPlugin.GetDebugLogging();
        }

        private void SaveToConfig()
        {
            MiSideMultiplayerPlugin.SetDisplayName(_displayName);
            MiSideMultiplayerPlugin.SetDebugLogging(_debugLogging);
        }

        private MenuOption BuildRow(string label, string display, System.Action onClick)
        {
            try
            {
                MenuOptionFactory f = new MenuOptionFactory()
                    .SetName(label + ": " + display)
                    .SetParent(_page)
                    .SetNextLocation(_page);

                if (onClick != null)
                    f = f.SetOnClick(() => onClick());

                MenuOption option = f.Build();
                NeutralizeNavigation(option);
                return option;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerSettingsController: row '" + label + "' failed: " + ex.Message);
                return null;
            }
        }

        private static void NeutralizeNavigation(MenuOption option)
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
        }

        private void RefreshAllLabels()
        {
            RefreshLabel(_rowDisplayName, "DISPLAY NAME", RowDisplayName());
            RefreshLabel(_rowPlayerId,    "PLAYER ID",    RowPlayerId());
            RefreshLabel(_rowDebug,       "DEBUG LOGGING", RowToggle(_debugLogging));
        }

        private static void RefreshLabel(MenuOption option, string label, string display)
        {
            if (option == null || option.OptionObject == null) return;
            try { option.Text = label + ": " + display; }
            catch { }
        }

        private string RowDisplayName()
        {
            if (_editingKey == "displayName")
                return Cursorize(_editingValue, _editingCursor);
            return string.IsNullOrEmpty(_displayName) ? "<empty>" : _displayName;
        }

        private string RowPlayerId()     { return string.IsNullOrEmpty(_playerId) ? "<none>" : _playerId; }
        private string RowToggle(bool b) { return b ? "ON" : "OFF"; }

        private static string Cursorize(string value, int cursor)
        {
            value = value ?? "";
            cursor = Mathf.Clamp(cursor, 0, value.Length);
            int blink = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2;
            string caret = blink == 0 ? "|" : " ";
            return value.Insert(cursor, caret);
        }

        private void OnDisplayNameClicked()
        {
            if (_editingKey == "displayName")
            {
                CommitEdit();
                return;
            }

            if (_editingKey != null)
                CommitEdit();

            BeginEdit("displayName", _displayName);
        }

        private void OnDebugClicked()
        {
            if (_editingKey != null) CommitEdit();
            _debugLogging = !_debugLogging;
            SaveToConfig();
            RefreshAllLabels();
        }

        private void BeginEdit(string key, string initial)
        {
            _editingKey = key;
            _editingValue = initial ?? "";
            _editingCursor = _editingValue.Length;

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            RefreshAllLabels();
        }

        private void CommitEdit()
        {
            if (_editingKey == null) return;

            if (_editingKey == "displayName")
            {
                _displayName = _editingValue ?? "";
                SaveToConfig();
            }

            _editingKey = null;
            _editingValue = null;
            _editingCursor = 0;
            RefreshAllLabels();
        }

        public void Tick()
        {
            if (_editingKey != null)
                RefreshAllLabels();
        }

        public void OnGui()
        {
            if (_editingKey == null) return;

            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                CommitEdit();
                e.Use();
                return;
            }

            string value = _editingValue ?? "";

            if (e.keyCode == KeyCode.Backspace)
            {
                if (_editingCursor > 0)
                {
                    value = value.Remove(_editingCursor - 1, 1);
                    _editingCursor--;
                }
            }
            else if (e.keyCode == KeyCode.Delete)
            {
                if (_editingCursor < value.Length)
                    value = value.Remove(_editingCursor, 1);
            }
            else if (e.keyCode == KeyCode.LeftArrow)
            {
                _editingCursor = Mathf.Max(0, _editingCursor - 1);
            }
            else if (e.keyCode == KeyCode.RightArrow)
            {
                _editingCursor = Mathf.Min(value.Length, _editingCursor + 1);
            }
            else if (e.character != '\0' && !char.IsControl(e.character) && value.Length < 32)
            {
                value = value.Insert(_editingCursor, e.character.ToString());
                _editingCursor++;
            }
            else
            {
                return;
            }

            _editingValue = value;
            e.Use();
        }

        public void Dispose()
        {
            if (_editingKey != null) CommitEdit();
            _page = null;
            _rowDisplayName = null;
            _rowPlayerId    = null;
            _rowDebug       = null;
        }
    }
}