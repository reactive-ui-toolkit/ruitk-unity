#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// POC pointer affordances (<c>#canvasWrap { cursor: grab }</c>,
    /// <c>.lib-item { cursor: grab }</c>, <c>.card-title { cursor: move }</c>,
    /// <c>.jsx-row { cursor: pointer }</c>, <c>.code-island { cursor: text }</c>).
    /// UI Toolkit exposes <c>IStyle.cursor</c> but the editor's built-in cursor
    /// ids live on an internal member of <see cref="Cursor"/>, so the id is set
    /// through that member; a Unity build that renames it simply leaves the
    /// default arrow rather than throwing.
    /// </summary>
    internal static class BuilderCursor
    {
        private static readonly PropertyInfo s_defaultCursorIdProperty =
            typeof(Cursor).GetProperty(
                "defaultCursorId", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo s_defaultCursorIdField =
            typeof(Cursor).GetField(
                "m_DefaultCursorId", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void Set(VisualElement element, MouseCursor cursor)
        {
            if (element == null)
                return;
            object boxed = new Cursor();
            if (s_defaultCursorIdProperty != null && s_defaultCursorIdProperty.CanWrite)
                s_defaultCursorIdProperty.SetValue(boxed, (int)cursor);
            else if (s_defaultCursorIdField != null)
                s_defaultCursorIdField.SetValue(boxed, (int)cursor);
            else
                return;
            element.style.cursor = new StyleCursor((Cursor)boxed);
        }
    }
}
#endif
