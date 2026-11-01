using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Captures and applies MitaLifeEmotion's runtime state. IL2CPP game
    /// versions expose some of these values as properties and others as
    /// backing fields, so both forms are supported here.
    /// </summary>
    internal static class MitaLifeEmotionSync
    {
        private const string ComponentName = "MitaLifeEmotion";
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class MemberAccessor
        {
            public PropertyInfo Property;
            public FieldInfo Field;

            public Type ValueType
            {
                get { return Property != null ? Property.PropertyType : Field != null ? Field.FieldType : null; }
            }
        }

        private static readonly Dictionary<Type, Dictionary<string, MemberAccessor>> Members =
            new Dictionary<Type, Dictionary<string, MemberAccessor>>();
        private static readonly HashSet<Type> LoggedTypes = new HashSet<Type>();
        private static readonly HashSet<int> MissingComponentActorsLogged = new HashSet<int>();

        public static MitaLifeEmotionState Capture(GameObject actor)
        {
            MitaLifeEmotion component = FindComponent(actor);
            if (component == null)
            {
                if (actor != null)
                {
                    int instanceId = 0;
                    try { instanceId = actor.GetInstanceID(); }
                    catch { }
                    if (instanceId == 0 || MissingComponentActorsLogged.Add(instanceId))
                        DiagnosticLog.Warning("MitaLifeEmotionSync: optional state omitted for '" +
                                              actor.name + "'; no MitaLifeEmotion component exists " +
                                              "on the actor, its parents, or its visual hierarchy.");
                }
                return null;
            }

            MitaLifeEmotionState state = new MitaLifeEmotionState
            {
                active = Read(component, "active", false),
                timeDontActive = Read(component, "timeDontActive", 0f),
                timeShy = Read(component, "timeShy", 0f),
                levelShy = Read(component, "levelShy", 0),
                indexDialogueShy = Read(component, "indexDialogueShy", 0),
                timeIgnoryActivation = Read(component, "timeIgnoryActivation", 0f),
                typeEmotionLongSee = ReadEnum(component, "typeEmotionLongSee"),
                emotionStart = Read(component, "emotionStart", string.Empty),
                emotionStop = Read(component, "emotionStop", string.Empty),
                randomEmotion = Read(component, "randomEmotion", false),
                timeRanomEmotion = Read(component, "timeRanomEmotion", 0f),
                lookFullPlayer = Read(component, "lookFullPlayer", false),
                timeLookFullPlayer = Read(component, "timeLookFullPlayer", 0f),
                orderLookFullPlayer = Read(component, "orderLookFullPlayer", 0),
                dialogueFarAwayIndex = Read(component, "dialogueFarAwayIndex", 0),
                dialogueCloselyIndex = Read(component, "dialogueCloselyIndex", 0),
                dialogueFarAwayTime = Read(component, "dialogueFarAwayTime", 0f),
                dialogueCloselyTime = Read(component, "dialogueCloselyTime", 0f),
                speedTimeOtherDiaolgue = Read(component, "speedTimeOtherDiaolgue", 0f),
                lookHead = Read(component, "lookHead", false)
            };
            LogResolvedMembers(typeof(MitaLifeEmotion));
            return state;
        }

        public static void Apply(GameObject actor, MitaLifeEmotionState state)
        {
            if (actor == null || state == null) return;
            MitaLifeEmotion component = FindComponent(actor);
            if (component == null) return;

            Write(component, "active", state.active);
            Write(component, "timeDontActive", state.timeDontActive);
            Write(component, "timeShy", state.timeShy);
            Write(component, "levelShy", state.levelShy);
            Write(component, "indexDialogueShy", state.indexDialogueShy);
            Write(component, "timeIgnoryActivation", state.timeIgnoryActivation);
            Write(component, "typeEmotionLongSee", state.typeEmotionLongSee);
            Write(component, "emotionStart", state.emotionStart ?? string.Empty);
            Write(component, "emotionStop", state.emotionStop ?? string.Empty);
            Write(component, "randomEmotion", state.randomEmotion);
            Write(component, "timeRanomEmotion", state.timeRanomEmotion);
            Write(component, "lookFullPlayer", state.lookFullPlayer);
            Write(component, "timeLookFullPlayer", state.timeLookFullPlayer);
            Write(component, "orderLookFullPlayer", state.orderLookFullPlayer);
            Write(component, "dialogueFarAwayIndex", state.dialogueFarAwayIndex);
            Write(component, "dialogueCloselyIndex", state.dialogueCloselyIndex);
            Write(component, "dialogueFarAwayTime", state.dialogueFarAwayTime);
            Write(component, "dialogueCloselyTime", state.dialogueCloselyTime);
            Write(component, "speedTimeOtherDiaolgue", state.speedTimeOtherDiaolgue);
            Write(component, "lookHead", state.lookHead);
        }

        private static MitaLifeEmotion FindComponent(GameObject actor)
        {
            if (actor == null) return null;
            try
            {
                // The name-based IL2CPP lookup returns a base Component wrapper
                // on some builds. Use the generated game type so reflection
                // does not cache UnityEngine.Component's empty member set.
                MitaLifeEmotion direct = actor.GetComponent<MitaLifeEmotion>();
                if (direct != null) return direct;

                // Some quest prefabs put MitaLifeEmotion on a parent logic
                // object while MitaPerson is attached to a child visual root.
                Transform parent = actor.transform.parent;
                while (parent != null)
                {
                    MitaLifeEmotion match = parent.GetComponent<MitaLifeEmotion>();
                    if (match != null) return match;
                    parent = parent.parent;
                }

                MitaLifeEmotion[] children = actor.GetComponentsInChildren<MitaLifeEmotion>(true);
                return children != null && children.Length > 0 ? children[0] : null;
            }
            catch { return null; }
        }

        private static MemberAccessor GetMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            Dictionary<string, MemberAccessor> byName;
            if (!Members.TryGetValue(type, out byName))
            {
                byName = new Dictionary<string, MemberAccessor>(StringComparer.Ordinal);
                Members[type] = byName;
            }

            MemberAccessor accessor;
            if (byName.TryGetValue(name, out accessor)) return accessor;

            accessor = new MemberAccessor();
            for (Type current = type; current != null; current = current.BaseType)
            {
                try
                {
                    PropertyInfo property = current.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (property != null && (property.GetGetMethod(true) != null || property.GetSetMethod(true) != null))
                    {
                        accessor.Property = property;
                        break;
                    }
                }
                catch { }
                try
                {
                    FieldInfo field = current.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        accessor.Field = field;
                        break;
                    }
                }
                catch { }
            }
            byName[name] = accessor;
            return accessor;
        }

        private static object Read(MitaLifeEmotion component, string name)
        {
            if (component == null) return null;
            MemberAccessor accessor = GetMember(typeof(MitaLifeEmotion), name);
            if (accessor == null) return null;
            try
            {
                if (accessor.Property != null)
                {
                    MethodInfo getter = accessor.Property.GetGetMethod(true);
                    if (getter != null) return getter.Invoke(component, null);
                }
                if (accessor.Field != null) return accessor.Field.GetValue(component);
            }
            catch { }
            return null;
        }

        private static T Read<T>(MitaLifeEmotion component, string name, T fallback)
        {
            try
            {
                object value = Read(component, name);
                if (value is T) return (T)value;
                if (value != null) return (T)Convert.ChangeType(value, typeof(T));
            }
            catch { }
            return fallback;
        }

        private static int ReadEnum(MitaLifeEmotion component, string name)
        {
            try
            {
                object value = Read(component, name);
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch { return 0; }
        }

        private static void Write(MitaLifeEmotion component, string name, object value)
        {
            if (component == null) return;
            MemberAccessor accessor = GetMember(typeof(MitaLifeEmotion), name);
            if (accessor == null) return;
            try
            {
                Type targetType = accessor.ValueType;
                if (targetType == null) return;
                object converted = ConvertValue(value, targetType);
                if (accessor.Property != null)
                {
                    MethodInfo setter = accessor.Property.GetSetMethod(true);
                    if (setter != null)
                    {
                        setter.Invoke(component, new object[] { converted });
                        return;
                    }
                }
                if (accessor.Field != null && !accessor.Field.IsInitOnly)
                    accessor.Field.SetValue(component, converted);
            }
            catch { }
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum) return Enum.ToObject(enumType, value);
            if (targetType.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, enumType);
        }

        private static void LogResolvedMembers(Type type)
        {
            if (type == null || !LoggedTypes.Add(type)) return;
            string[] names =
            {
                "active", "timeDontActive", "timeShy", "levelShy", "indexDialogueShy",
                "timeIgnoryActivation", "typeEmotionLongSee", "emotionStart", "emotionStop",
                "randomEmotion", "timeRanomEmotion", "lookFullPlayer", "timeLookFullPlayer",
                "orderLookFullPlayer", "dialogueFarAwayIndex", "dialogueCloselyIndex",
                "dialogueFarAwayTime", "dialogueCloselyTime", "speedTimeOtherDiaolgue", "lookHead"
            };
            int found = 0;
            int writable = 0;
            for (int i = 0; i < names.Length; i++)
            {
                MemberAccessor accessor = GetMember(type, names[i]);
                if (accessor.ValueType != null) found++;
                if ((accessor.Property != null && accessor.Property.GetSetMethod(true) != null) ||
                    (accessor.Field != null && !accessor.Field.IsInitOnly))
                    writable++;
            }
            DiagnosticLog.Info("MitaLifeEmotionSync: resolved " + found + "/" + names.Length +
                               " members; " + writable + " writable on " +
                               type.FullName +
                               " (property/backing-field access enabled).");
        }
    }
}
