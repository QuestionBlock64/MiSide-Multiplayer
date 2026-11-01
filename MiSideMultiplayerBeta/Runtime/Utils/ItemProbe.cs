using System;
using System.Reflection;
using UnityEngine;

namespace MiSideMultiplayer
{
    public static class HeldItemProbe
    {
        private static float _nextDumpTime;

        public static void Tick()
        {
            if (Time.unscaledTime < _nextDumpTime) return;
            _nextDumpTime = Time.unscaledTime + 2.0f;

            try
            {
                Transform playerRoot = LocalPlayerLocator.FindHardcodedPlayerPath();
                if (playerRoot == null) return;

                Component pm = playerRoot.GetComponent("PlayerMove");
                if (pm == null)
                {
                    DiagnosticLog.Info("[HELD-ITEM] PlayerMove component not found on GameController/Player.");
                    return;
                }

                Type t = pm.GetType();
                const BindingFlags Flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                DiagnosticLog.Info("[HELD-ITEM] ───── PlayerMove state ─────");

                DumpGameObjectMember(pm, t, Flags, "item");
                DumpBoolMember(pm, t, Flags, "keepItem");
                DumpBoolMember(pm, t, Flags, "hideItem");
                DumpBoolMember(pm, t, Flags, "animationHandRun");
                DumpGameObjectMember(pm, t, Flags, "objectCast");
                DumpGameObjectMember(pm, t, Flags, "objectCastInteractive");
                DumpGameObjectMember(pm, t, Flags, "objectCastHandInteractive");
                DumpStringMember(pm, t, Flags, "nameStatePlayNow");

                Transform rightHand = InvokeTransformMethod(pm, t, Flags, "GetBoneRightItem");
                Transform leftHand  = InvokeTransformMethod(pm, t, Flags, "GetBoneLeftItem");

                DiagnosticLog.Info("  GetBoneRightItem() = " +
                    (rightHand != null ? LocalPlayerLocator.GetPath(rightHand) : "<null>"));
                DiagnosticLog.Info("  GetBoneLeftItem()  = " +
                    (leftHand != null ? LocalPlayerLocator.GetPath(leftHand) : "<null>"));

                DumpHandChildren("right hand", rightHand);
                DumpHandChildren("left hand",  leftHand);

                // Also dump the Player_Ring state so we can see if the ring is
                // toggled by a component.
                Component ring = playerRoot.GetComponent("Player_Ring");
                if (ring != null)
                {
                    DiagnosticLog.Info("  Player_Ring found:");
                    DumpGameObjectMember(ring, ring.GetType(), Flags, "ring");
                }
                else
                {
                    DiagnosticLog.Info("  Player_Ring component not on GameController/Player.");
                }

                DiagnosticLog.Info("[HELD-ITEM] ─────────────────────────────");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("HeldItemProbe failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Field OR property resolution (IL2CPP proxy types expose
        //    what look like fields as properties) ─────────────────────────────

        private static object ReadMember(object instance, Type t,
                                         BindingFlags flags, string name,
                                         out string kind)
        {
            kind = null;
            try
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null)
                {
                    kind = "field";
                    return f.GetValue(instance);
                }
            }
            catch (Exception) { }

            try
            {
                PropertyInfo p = t.GetProperty(name, flags);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    kind = "property";
                    return p.GetValue(instance, null);
                }
            }
            catch (Exception) { }

            return null;
        }

        private static void DumpGameObjectMember(object instance, Type t,
                                                 BindingFlags flags, string name)
        {
            string kind;
            object val = ReadMember(instance, t, flags, name, out kind);
            if (kind == null)
            {
                DiagnosticLog.Info("  " + name + " = <field/property not found>");
                return;
            }

            GameObject go = val as GameObject;
            if (go == null)
            {
                DiagnosticLog.Info("  " + name + " = <null>  (" + kind + ")");
                return;
            }

            string path = LocalPlayerLocator.GetPath(go.transform);
            string parentName = go.transform.parent != null
                ? go.transform.parent.name : "<no parent>";

            DiagnosticLog.Info("  " + name + " = '" + go.name + "'  (" + kind + ")" +
                               "  activeInHierarchy=" + go.activeInHierarchy +
                               "  activeSelf=" + go.activeSelf +
                               "  parent='" + parentName + "'" +
                               "  path=" + path);

            int childCount = go.transform.childCount;
            DiagnosticLog.Info("      children=" + childCount);
            for (int i = 0; i < childCount && i < 8; i++)
            {
                Transform c = go.transform.GetChild(i);
                if (c == null) continue;
                DiagnosticLog.Info("        [" + i + "] '" + c.name +
                                   "' activeSelf=" + c.gameObject.activeSelf);
            }

            Component[] comps = go.GetComponents<Component>();
            DiagnosticLog.Info("      components=" + comps.Length);
            for (int i = 0; i < comps.Length && i < 12; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                try { DiagnosticLog.Info("        [" + i + "] " + c.GetType().Name); }
                catch (Exception) { }
            }
        }

        private static void DumpBoolMember(object instance, Type t,
                                           BindingFlags flags, string name)
        {
            string kind;
            object val = ReadMember(instance, t, flags, name, out kind);
            if (kind == null) return;
            DiagnosticLog.Info("  " + name + " = " +
                (val == null ? "<null>" : val.ToString()) + "  (" + kind + ")");
        }

        private static void DumpStringMember(object instance, Type t,
                                             BindingFlags flags, string name)
        {
            string kind;
            object val = ReadMember(instance, t, flags, name, out kind);
            if (kind == null) return;
            DiagnosticLog.Info("  " + name + " = '" +
                (val == null ? "<null>" : val.ToString()) + "'  (" + kind + ")");
        }

        private static Transform InvokeTransformMethod(object instance, Type t,
                                                       BindingFlags flags, string name)
        {
            try
            {
                MethodInfo m = t.GetMethod(name, flags);
                if (m == null) return null;
                object result = m.Invoke(instance, null);
                return result as Transform;
            }
            catch (Exception) { return null; }
        }

        private static void DumpHandChildren(string label, Transform hand)
        {
            if (hand == null)
            {
                DiagnosticLog.Info("  " + label + ": <no hand transform>");
                return;
            }

            DiagnosticLog.Info("  " + label + " '" + hand.name + "' direct children:");
            int count = hand.childCount;
            for (int i = 0; i < count; i++)
            {
                Transform c = hand.GetChild(i);
                if (c == null) continue;
                DiagnosticLog.Info("    [" + i + "] '" + c.name +
                                   "' activeSelf=" + c.gameObject.activeSelf);
            }
        }
    }
}