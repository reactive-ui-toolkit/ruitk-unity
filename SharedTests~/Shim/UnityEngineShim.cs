// Minimal stand-ins for the UnityEngine surface that Shared/Core touches.
//
// Why a shim instead of referencing Unity's real assemblies: those DLLs reference
// native ECall implementations that only exist inside the Unity runtime. Probed on
// 6000.5.6f1 outside the editor, Debug.Log / Debug.LogWarning / Debug.LogError,
// Time.realtimeSinceStartup, GUID.Generate and `new VisualElement()` all throw
// SecurityException: "ECall methods must be packaged into a system module". The
// reconciler logs warnings on exactly the paths these tests exercise, so referencing
// the real assemblies would make the suite unrunnable.
//
// The compile-time check against the REAL Unity assemblies is a separate harness
// (UnityCompileCheck~) that compiles but never executes.
//
// Debug here CAPTURES instead of no-opping, so tests can assert on what the
// reconciler logged - warnings that used to be invisible are now assertable.

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public enum LogKind
    {
        Log,
        Warning,
        Error,
    }

    public readonly struct LogEntry
    {
        public readonly LogKind Kind;
        public readonly string Message;

        public LogEntry(LogKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public override string ToString() => $"[{Kind}] {Message}";
    }

    public static class Debug
    {
        private static readonly List<LogEntry> s_entries = new List<LogEntry>();

        public static IReadOnlyList<LogEntry> Entries => s_entries;

        public static void Clear() => s_entries.Clear();

        public static void Log(object message) =>
            s_entries.Add(new LogEntry(LogKind.Log, message?.ToString() ?? ""));

        public static void LogWarning(object message) =>
            s_entries.Add(new LogEntry(LogKind.Warning, message?.ToString() ?? ""));

        public static void LogError(object message) =>
            s_entries.Add(new LogEntry(LogKind.Error, message?.ToString() ?? ""));

        public static void LogException(Exception exception) =>
            s_entries.Add(new LogEntry(LogKind.Error, exception?.ToString() ?? ""));
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;

        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        public static float Max(float a, float b) => a > b ? a : b;

        public static float Min(float a, float b) => a < b ? a : b;

        public static float Pow(float f, float p) => (float)Math.Pow(f, p);

        public static float Sin(float f) => (float)Math.Sin(f);

        public static float Cos(float f) => (float)Math.Cos(f);

        public static float Abs(float f) => Math.Abs(f);

        public static float Sqrt(float f) => (float)Math.Sqrt(f);
    }

    public static class Time
    {
        // Tests drive time explicitly rather than reading a wall clock, so animation
        // and scheduling assertions stay deterministic.
        public static float realtimeSinceStartup { get; set; }

        public static double realtimeSinceStartupAsDouble { get; set; }

        public static float deltaTime { get; set; }

        public static float unscaledDeltaTime { get; set; }

        public static void Reset()
        {
            realtimeSinceStartup = 0f;
            realtimeSinceStartupAsDouble = 0d;
            deltaTime = 0f;
            unscaledDeltaTime = 0f;
        }
    }

    public struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new Vector2(0f, 0f);

        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);

        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
    }

    public struct Rect
    {
        public float x;
        public float y;
        public float width;
        public float height;

        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }

        public float xMax => x + width;
        public float yMax => y + height;

        public static Rect zero => new Rect(0f, 0f, 0f, 0f);
    }

    // Only the members the linked sources name. Values match Unity's.
    public enum KeyCode
    {
        None = 0,
        Backspace = 8,
        Tab = 9,
        Return = 13,
        Escape = 27,
        Space = 32,
        Delete = 127,
        UpArrow = 273,
        DownArrow = 274,
        RightArrow = 275,
        LeftArrow = 276,
    }

    public static class Screen
    {
        // Settable so SafeAreaUtility-based assertions are deterministic.
        public static int width { get; set; } = 1920;

        public static int height { get; set; } = 1080;

        public static Rect safeArea { get; set; } = new Rect(0f, 0f, 1920f, 1080f);
    }
}

namespace UnityEngine.Profiling
{
    public static class Profiler
    {
        public static void BeginSample(string name) { }

        public static void EndSample() { }
    }
}

namespace UnityEngine.Profiling
{
    public sealed class CustomSampler
    {
        private CustomSampler() { }

        public static CustomSampler Create(string name, bool collectGpuData = false) =>
            new CustomSampler();

        public void Begin() { }

        public void End() { }
    }
}

namespace UnityEngine
{
    // Present only so linked sources that mention these types compile. Nothing in the
    // core under test constructs or drives them; the mock host uses POCO handles.
    public class Object
    {
        public string name { get; set; } = "";

        public static void Destroy(Object obj) { }

        public static void DestroyImmediate(Object obj) { }

        public static void DontDestroyOnLoad(Object target) { }
    }

    public class Component : Object
    {
        private GameObject _go;

        public GameObject gameObject => _go ??= new GameObject(name);
    }

    public class MonoBehaviour : Component { }
}

namespace UnityEngine.UIElements
{
    // Structural stand-ins only. The reconciler never touches these in the tests -
    // the mock FiberHostConfig hands it POCO handles - but the linked sources name
    // them in signatures and event-wrapper constructors, so they must exist to
    // compile. Shapes mirror the real UIElements API; fidelity against the real
    // assemblies is enforced by the separate compile-only harness
    // (scripts/unity-compile-check.mjs), so drift here cannot mask a real break.
    public interface IEventHandler { }

    public interface IPanel { }

    public class Focusable : IEventHandler { }

    public class VisualElement : Focusable
    {
        public string name { get; set; } = "";

        public VisualElement parent { get; internal set; }

        private readonly System.Collections.Generic.List<VisualElement> _children =
            new System.Collections.Generic.List<VisualElement>();

        public int childCount => _children.Count;

        public VisualElement ElementAt(int index) => _children[index];

        public void Add(VisualElement child)
        {
            child.parent = this;
            _children.Add(child);
        }

        public void Remove(VisualElement child)
        {
            if (_children.Remove(child))
                child.parent = null;
        }

        public void Clear()
        {
            foreach (var c in _children)
                c.parent = null;
            _children.Clear();
        }
    }

    public abstract class EventBase
    {
        public IEventHandler target { get; set; }

        public IEventHandler currentTarget { get; set; }

        public long timestamp { get; set; }

        public bool isPropagationStopped { get; private set; }

        public void StopPropagation() => isPropagationStopped = true;
    }

    public interface IPointerEvent
    {
        int pointerId { get; }
        Vector3 position { get; }
        Vector3 deltaPosition { get; }
        int button { get; }
        int clickCount { get; }
        bool altKey { get; }
        bool ctrlKey { get; }
        bool shiftKey { get; }
        bool commandKey { get; }
        float pressure { get; }
        float tangentialPressure { get; }
        float altitudeAngle { get; }
        float azimuthAngle { get; }
        float twist { get; }
        Vector2 radius { get; }
        Vector2 radiusVariance { get; }
    }

    public interface IMouseEvent
    {
        Vector2 mousePosition { get; }
        Vector2 mouseDelta { get; }
        int button { get; }
        int clickCount { get; }
        bool altKey { get; }
        bool ctrlKey { get; }
        bool shiftKey { get; }
        bool commandKey { get; }
    }

    public interface IKeyboardEvent
    {
        KeyCode keyCode { get; }
        char character { get; }
        bool altKey { get; }
        bool ctrlKey { get; }
        bool shiftKey { get; }
        bool commandKey { get; }
    }

    public interface IFocusEvent
    {
        Focusable relatedTarget { get; }
    }

    public class WheelEvent : EventBase, IMouseEvent
    {
        public Vector3 delta { get; set; }
        public Vector2 mousePosition { get; set; }
        public Vector2 mouseDelta { get; set; }
        public int button { get; set; }
        public int clickCount { get; set; }
        public bool altKey { get; set; }
        public bool ctrlKey { get; set; }
        public bool shiftKey { get; set; }
        public bool commandKey { get; set; }
    }

    public class FocusEvent : EventBase, IFocusEvent
    {
        public Focusable relatedTarget { get; set; }
    }

    public class BlurEvent : EventBase, IFocusEvent
    {
        public Focusable relatedTarget { get; set; }
    }

    public class FocusInEvent : EventBase, IFocusEvent
    {
        public Focusable relatedTarget { get; set; }
    }

    public class FocusOutEvent : EventBase, IFocusEvent
    {
        public Focusable relatedTarget { get; set; }
    }

    public class GeometryChangedEvent : EventBase
    {
        public Rect oldRect { get; set; }
        public Rect newRect { get; set; }
    }

    public class AttachToPanelEvent : EventBase
    {
        public IPanel destinationPanel { get; set; }
    }

    public class DetachFromPanelEvent : EventBase
    {
        public IPanel originPanel { get; set; }
    }

    public class ChangeEvent<T> : EventBase
    {
        public T previousValue { get; set; }
        public T newValue { get; set; }
    }

    public class Tab : VisualElement { }

    public class DropdownMenu { }

    public struct TreeViewExpansionChangedArgs
    {
        public int id;
        public bool isExpanded;
    }

    public enum SortDirection
    {
        Ascending,
        Descending,
    }

    public class MeshGenerationContext { }
}

namespace UnityEngine
{
    [System.Flags]
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61,
    }

    public class GameObject : Object
    {
        public GameObject() { }

        public GameObject(string name) => this.name = name;

        public HideFlags hideFlags { get; set; }

        public T AddComponent<T>() where T : Component, new() => new T();
    }
}
