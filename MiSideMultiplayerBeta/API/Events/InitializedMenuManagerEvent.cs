using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.API.Events
{
    public static class InitializedMenuManagerEvent
    {
        private static readonly List<Action> Actions = [];

        public static void AddEventListener(Action listener)
        {
            Actions.Add(listener);
        }

        internal static void Invoke()
        {
            foreach (Action action in Actions)
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception e)
                {
                    DiagnosticLog.Warning("QBLib: InitializedMenuManager event listener error: " + e);
                }
            }
        }
    }
}