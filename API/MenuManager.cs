using System.Collections.Generic;
using System.Linq;
using MiSideMultiplayer.API.Events;
using MiSideMultiplayer.API.Factories;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MiSideMultiplayer.API
{
    public class MenuManager
    {
        public static MenuManager Instance { get; private set; }

        private GameObject _frameMenu;
        private GameObject _cacheLocation;

        private List<GameMenu> Menus => GetMenus();

        internal static void Initialize()
        {
            Instance = new MenuManager();
        }

        private MenuManager()
        {
            PrepareCache();
            InitializedMenuManagerEvent.Invoke();
        }

        private List<GameMenu> GetMenus()
        {
            List<GameMenu> menus = [];

            for (int i = 0; i < _frameMenu.transform.childCount; i++)
            {
                GameObject child = _frameMenu.transform.GetChild(i).gameObject;
                if (child.GetComponent<MenuLocation>() == null) continue;

                menus.Add(MenuFactory.FromExistingMenu(child));
            }

            return menus;
        }

        private void PrepareCache()
        {
            _frameMenu = GameObject.Find("MenuGame/Canvas/FrameMenu");
            _cacheLocation = new GameObject
            {
                transform =
                {
                    parent = _frameMenu.transform
                },
                name = "QBLibCache",
                active = false
            };

            GameObject locationMenu = GameObject.Find("MenuGame/Canvas/FrameMenu/Location Menu");
            GameObject cachedLocationMenu = Object.Instantiate(locationMenu, _cacheLocation.transform);
            cachedLocationMenu.name = "Menu";

            for (int i = 0; i < cachedLocationMenu.transform.childCount; i++)
            {
                GameObject child = cachedLocationMenu.transform.GetChild(i).gameObject;
                if (child.name == "Text")
                {
                    Object.Destroy(child.GetComponent<Localization_UIText>());
                }
                for (int j = 0; j < child.transform.childCount; j++)
                {
                    GameObject grandChild = child.transform.GetChild(j).gameObject;
                    Object.Destroy(grandChild.GetComponent<Localization_UIText>());
                }
            }
        }

        public GameMenu Find(string name)
        {
            return Menus.FirstOrDefault(menu =>
                menu.MenuObject.gameObject.name.ToLower().Equals(name.ToLower()), null);
        }

        internal GameObject CreateMenuFromTemplate()
        {
            GameObject menu = Object.Instantiate(_cacheLocation.transform.Find("Menu").gameObject, _frameMenu.transform);
            List<GameObject> childrenThatShouldBeOrphaned = [];
            GameObject textObject = null;
            for (int i = 0; i < menu.transform.childCount; i++)
            {
                GameObject child = menu.transform.GetChild(i).gameObject;
                if (child.name != "Text")
                    childrenThatShouldBeOrphaned.Add(child);
                else
                    textObject = child;
            }

            foreach (GameObject child in childrenThatShouldBeOrphaned)
            {
                child.transform.SetParent(null);
                Object.Destroy(child);
            }

            MenuLocation menuLocation = menu.GetComponent<MenuLocation>();
            menuLocation.countCases = 0;
            menuLocation.objects.Clear();
            menuLocation.buttonBack = null;

            if (textObject != null)
                menuLocation.objects.Add(textObject.GetComponent<RectTransform>());

            menu.active = false;
            return menu;
        }

        internal GameObject CreateOptionFromTemplate(GameMenu parent)
        {
            return Object.Instantiate(_cacheLocation.transform.Find("Menu/Button NewGame").gameObject, parent.MenuObject.transform);
        }

        internal GameObject CreateDividerFromTemplate(GameMenu parent)
        {
            return Object.Instantiate(_cacheLocation.transform.Find("Menu/-").gameObject, parent.MenuObject.transform);
        }
    }
}