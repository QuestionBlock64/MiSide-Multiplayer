using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Keeps the Scene 1 tutorial hint local and makes sure the game's own
    /// Location1Tutorial entry point runs once after its hierarchy exists.
    /// </summary>
    internal static class PrologueLocalQuestSupport
    {
        private const string SceneName = "Scene 1 - RealRoom";
        private const string TutorialPath = "Quests/Quest 1 - Взять телефон/Tutorial";
        private const string TutorialComponentName = "Location1Tutorial";

        private static int _attemptedTutorialInstanceId;
        private static bool _missingMethodLogged;

        public static void Tick()
        {
            string activeScene;
            try { activeScene = SceneManager.GetActiveScene().name; }
            catch { return; }

            if (!string.Equals(activeScene, SceneName, StringComparison.Ordinal))
            {
                _attemptedTutorialInstanceId = 0;
                _missingMethodLogged = false;
                return;
            }

            if (_attemptedTutorialInstanceId != 0) return;

            try
            {
                GameObject world = null;
                GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
                for (int i = 0; roots != null && i < roots.Length; i++)
                {
                    if (roots[i] != null && string.Equals(roots[i].name, "World",
                            StringComparison.Ordinal))
                    {
                        world = roots[i];
                        break;
                    }
                }

                Transform tutorialTransform = world != null
                    ? world.transform.Find(TutorialPath)
                    : null;
                if (tutorialTransform == null) return;

                GameObject tutorialObject = tutorialTransform.gameObject;
                Component tutorial = tutorialObject.GetComponent(TutorialComponentName);
                if (tutorial == null) return;

                int instanceId = tutorialObject.GetInstanceID();
                if (instanceId == _attemptedTutorialInstanceId) return;

                MethodInfo changePlayerQuest = tutorial.GetType().GetMethod(
                    "ChangePlayerQuest",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(bool) }, null);
                if (changePlayerQuest == null)
                {
                    DiagnosticLog.Warning("PrologueLocalQuestSupport: Location1Tutorial.ChangePlayerQuest(bool) was not found; leaving the tutorial intact.");
                    return;
                }

                MethodInfo closeQuestHint = tutorial.GetType().GetMethod(
                    "CloseQuestHint",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (closeQuestHint == null)
                {
                    if (!_missingMethodLogged)
                    {
                        DiagnosticLog.Warning("PrologueLocalQuestSupport: Location1Tutorial.CloseQuestHint() was not found.");
                        _missingMethodLogged = true;
                    }
                    return;
                }

                // CloseQuestHint destroys the tutorial and its hint UI when
                // requestPlayer is false. Keep this local client on that
                // branch so the prologue hint never captures its UI/input.
                changePlayerQuest.Invoke(tutorial, new object[] { false });

                // Mark before invocation: the method may destroy this object
                // or invoke UnityEvents, so retrying after a partial failure
                // could replay its side effects.
                _attemptedTutorialInstanceId = instanceId;
                closeQuestHint.Invoke(tutorial, null);
                DiagnosticLog.Info("PrologueLocalQuestSupport: dismissed the Scene 1 tutorial UI with CloseQuestHint().");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("PrologueLocalQuestSupport: tutorial hint setup failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
