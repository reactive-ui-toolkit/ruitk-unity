#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// Live preview of the focused component (plan §2.1 mount mechanics):
    /// its own budgeted scheduler and HostContext (never the global editor
    /// scheduler), generated-type resolution via <c>[UitkxSource]</c> with a
    /// canonicalized path compare (the stored path is the authoring-machine
    /// absolute path), <c>RunModuleConstructor</c> BEFORE delegate resolution
    /// (family registration lives in a module initializer reflection does not
    /// trigger), and per-compile delegate re-resolution from the hot-swap
    /// assembly (a cached delegate mounts a stale body on remount paths).
    /// Knobs: primitive properties of the nested generated props class.
    /// </summary>
    internal sealed class BuilderPreviewPane : IDisposable
    {
        private static bool s_refreshProviderRegistered;

        private readonly BuilderRenderScheduler _scheduler = new BuilderRenderScheduler();
        private VisualElement _container;
        private VisualElement _previewHost;
        private VisualElement _knobsHost;
        private Label _statusLabel;
        private VNodeHostRenderer _renderer;
        private HostContext _hostContext;
        private Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> _renderDelegate;
        private Type _componentType;
        private IProps _knobProps;
        private string _filePath;

        public event Action<string> ComponentPicked;

        public BuilderPreviewPane()
        {
            if (!s_refreshProviderRegistered)
            {
                Ruitk.Refresh.RefreshRuntime.RegisterRootRendererProvider(
                    MountRegistry.EnumerateRootFibers);
                s_refreshProviderRegistered = true;
            }
        }

        public void Attach(VisualElement container)
        {
            _container = container;
            container.Clear();

            _statusLabel = new Label
            {
                style =
                {
                    marginTop = 8f, marginLeft = 8f, marginRight = 8f,
                    color = new Color(0.545f, 0.545f, 0.588f),
                    unityFontStyleAndWeight = FontStyle.Italic,
                    whiteSpace = WhiteSpace.Normal,
                },
            };
            container.Add(_statusLabel);

            _previewHost = new VisualElement
            {
                name = "builder-preview-host",
                style = { flexGrow = 1f, borderTopWidth = 1f, borderTopColor = new Color(0.2f, 0.2f, 0.23f) },
            };
            _previewHost.RegisterCallback<PointerDownEvent>(OnPreviewPicked, TrickleDown.TrickleDown);
            container.Add(_previewHost);

            container.Add(new Label("PROPS — AUTO-GENERATED KNOBS")
            {
                style =
                {
                    color = new Color(0.545f, 0.545f, 0.588f),
                    fontSize = 10f,
                    letterSpacing = 1f,
                    marginTop = 8f, marginLeft = 8f, marginBottom = 6f,
                    borderTopWidth = 1f,
                    borderTopColor = new Color(0.227f, 0.227f, 0.267f),
                    paddingTop = 8f,
                },
            });
            _knobsHost = new VisualElement
            {
                name = "builder-preview-knobs",
                style = { flexShrink = 0f, maxHeight = 220f, paddingLeft = 4f },
            };
            container.Add(_knobsHost);

            container.Add(new Label("STATE — LIVE HOOK VALUES")
            {
                style =
                {
                    color = new Color(0.545f, 0.545f, 0.588f),
                    fontSize = 10f,
                    letterSpacing = 1f,
                    marginTop = 8f, marginLeft = 8f, marginBottom = 6f,
                },
            });
            _stateHost = new VisualElement
            {
                style = { flexShrink = 0f, maxHeight = 140f, paddingLeft = 8f, paddingBottom = 4f },
            };
            container.Add(_stateHost);
            EditorApplication.update += TickStatePanel;
        }

        private VisualElement _stateHost;
        private double _nextStateRefresh;

        private void TickStatePanel()
        {
            if (EditorApplication.timeSinceStartup < _nextStateRefresh)
                return;
            _nextStateRefresh = EditorApplication.timeSinceStartup + 0.5;
            RefreshStatePanel();
        }

        /// <summary>The POC's live-state panel: hook values read straight from
        /// the mounted fiber tree's FunctionComponentState (useState cells shown
        /// per component, refreshed twice a second).</summary>
        private void RefreshStatePanel()
        {
            if (_stateHost == null || _stateHost.panel == null)
                return;
            var focused = _stateHost.panel.focusController?.focusedElement as VisualElement;
            for (var el = focused; el != null; el = el.parent)
                if (el == _stateHost)
                    return;
            _stateHost.Clear();
            var root = _renderer?.Fiber?.Root?.Current;
            if (root == null)
                return;
            int shown = 0;
            CollectHookRows(root, ref shown);
        }

        private void CollectHookRows(FiberNode fiber, ref int shown)
        {
            while (fiber != null && shown < 12)
            {
                var states = fiber.ComponentState?.HookStates;
                if (states != null && states.Count > 0)
                {
                    string owner = fiber.Family?.Id ?? "component";
                    int dot = owner.LastIndexOf('.');
                    if (dot >= 0)
                        owner = owner.Substring(dot + 1);
                    for (int i = 0; i < states.Count && shown < 12; i++)
                    {
                        var field = BuildStateField(owner + "[" + i + "]", states[i]);
                        if (field == null)
                            continue;
                        _stateHost.Add(field);
                        shown++;
                    }
                }
                if (fiber.Child != null)
                    CollectHookRows(fiber.Child, ref shown);
                fiber = fiber.Sibling;
            }
        }

        /// <summary>POC §7: state fields are EDITABLE — an int/float/string/bool
        /// hook cell renders as a live input writing straight back into the
        /// fiber's state store, then re-renders the preview.</summary>
        private VisualElement BuildStateField(string label, object state)
        {
            var (holder, member, value) = ResolveStateCell(state);
            if (holder == null)
                return null;

            void Write(object next)
            {
                try
                {
                    if (member is PropertyInfo prop)
                        prop.SetValue(holder, next);
                    else if (member is FieldInfo field)
                        field.SetValue(holder, next);
                    Mount();
                }
                catch (Exception)
                {
                }
            }

            var rowStyle = new Color(0.545f, 0.545f, 0.588f);
            switch (value)
            {
                case int i:
                {
                    var field = new IntegerField(label) { value = i };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return field;
                }
                case float f:
                {
                    var field = new FloatField(label) { value = f };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return field;
                }
                case bool b:
                {
                    var field = new Toggle(label) { value = b };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return field;
                }
                case string s:
                {
                    var field = new TextField(label) { value = s };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return field;
                }
                case null:
                    return null;
                default:
                    return new Label(label + " = " + value)
                    {
                        style = { color = rowStyle, fontSize = 10f },
                    };
            }
        }

        private static (object Holder, MemberInfo Member, object Value) ResolveStateCell(object state)
        {
            if (state == null)
                return (null, null, null);
            var type = state.GetType();
            foreach (string name in new[] { "Value", "Current", "State" })
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead && prop.CanWrite)
                    return (state, prop, prop.GetValue(state));
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                    return (state, field, field.GetValue(state));
            }
            return (null, null, null);
        }

        public void ShowFile(string uitkxPath, string bufferText, Assembly assemblyHint)
        {
            _filePath = uitkxPath;
            var type = ResolveComponentType(uitkxPath, assemblyHint);
            if (type == null)
            {
                SetStatus(NoPreviewText(uitkxPath));
                UnmountPreview();
                return;
            }

            RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);

            var render = type.GetMethod(
                "Render",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(IProps), typeof(IReadOnlyList<VirtualNode>) },
                null);
            if (render == null)
            {
                SetStatus(type.Name + " has no Render entry point");
                UnmountPreview();
                return;
            }

            bool sameComponent = _componentType == type && _renderDelegate != null;
            _componentType = type;
            _renderDelegate = (Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>)
                Delegate.CreateDelegate(
                    typeof(Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>), render);

            if (!sameComponent)
            {
                _knobProps = CreateDefaultProps(type);
                BuildKnobs();
            }

            string badge = bufferText != null && bufferText.Contains("<Portal") ? "  [portals]" : "";
            SetStatus(type.Name + badge);
            Mount();
        }

        /// <summary>Called after a preview compile of the shown file — re-resolve
        /// from the freshly loaded assembly so remounts use the new body.</summary>
        public void OnRecompiled(Assembly loadedAssembly, string bufferText)
        {
            if (_filePath != null && loadedAssembly != null)
                ShowFile(_filePath, bufferText, loadedAssembly);
        }

        public void ShowError(string message)
        {
            SetStatus(message);
        }

        public void Dispose()
        {
            EditorApplication.update -= TickStatePanel;
            UnmountPreview();
            _scheduler.Dispose();
            _container = null;
            _stateHost = null;
        }

        /// <summary>VE-15 Family-exact click-through: Ctrl+Click maps the picked
        /// element to its owning COMPONENT by walking the live fiber tree —
        /// each fiber with a Family names a generated type whose
        /// <c>[UitkxSource]</c> is the file; host elements inherit the nearest
        /// enclosing family's file. Directive-generated subtrees carry their
        /// component's file (the documented limitation).</summary>
        private void OnPreviewPicked(PointerDownEvent evt)
        {
            if (!evt.ctrlKey || _renderer == null)
                return;
            var picked = evt.target as VisualElement;
            if (picked == null)
                return;

            var ownerByElement = new Dictionary<object, string>();
            var root = _renderer.Fiber?.Root?.Current;
            if (root != null)
                MapOwners(root, Canon(_filePath), ownerByElement);

            for (var el = picked; el != null && el != _previewHost; el = el.parent)
            {
                if (ownerByElement.TryGetValue(el, out string file) && file != null)
                {
                    evt.StopPropagation();
                    ComponentPicked?.Invoke(file);
                    return;
                }
            }
        }

        private static void MapOwners(
            FiberNode fiber, string currentFile, Dictionary<object, string> ownerByElement)
        {
            while (fiber != null)
            {
                string file = currentFile;
                var declaring = fiber.Family?.Current?.Method?.DeclaringType;
                var src = declaring?.GetCustomAttribute<UitkxSourceAttribute>();
                if (src != null)
                    file = Canon(src.SourcePath);
                if (fiber.HostElement != null && !ownerByElement.ContainsKey(fiber.HostElement))
                    ownerByElement[fiber.HostElement] = file;
                if (fiber.Child != null)
                    MapOwners(fiber.Child, file, ownerByElement);
                fiber = fiber.Sibling;
            }
        }

        private void Mount()
        {
            if (_previewHost == null || _renderDelegate == null)
                return;
            if (_renderer == null)
            {
                _hostContext = RuitkBootstrap.CreateHostContext(
                    ElementRegistryProvider.GetDefaultRegistry(), null, _scheduler, true);
                _renderer = new VNodeHostRenderer(_hostContext, _previewHost);
            }
            _renderer.Render(V.Func(_renderDelegate, _knobProps));
        }

        private void UnmountPreview()
        {
            if (_renderer != null)
            {
                _renderer.Unmount();
                _scheduler.PumpNow();
                _renderer = null;
                _hostContext = null;
            }
            _renderDelegate = null;
            _componentType = null;
        }

        /// <summary>POC ".nopreview" copy, one per module kind — a style / hook /
        /// util module has no visual of its own.</summary>
        private static string NoPreviewText(string uitkxPath)
        {
            string name = Path.GetFileName(uitkxPath) ?? "";
            if (name.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase))
                return "Hook module — no visual. It exposes its hooks to the components that "
                    + "import it (green edge). Double-click its code island to edit the body.";
            if (name.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase))
                return "Style module — no visual of its own. Zoom to L2 and click an entry to "
                    + "edit it. Tip: select the component that imports it first, then edit an "
                    + "entry's BackgroundColor hex — the live preview repaints.";
            return "Util module — no visual. Its exported functions land on the file's __Exports "
                + "class; drag the module onto a component to import them by name.";
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null)
                _statusLabel.text = text;
        }

        private static string Canon(string path)
        {
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch
            {
                return path?.Replace('\\', '/') ?? "";
            }
        }

        private static Type ResolveComponentType(string uitkxPath, Assembly hint)
        {
            string wanted = Canon(uitkxPath);
            string stem = Path.GetFileNameWithoutExtension(uitkxPath);

            Type Scan(Assembly asm)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                Type byName = null;
                foreach (var type in types)
                {
                    if (type == null)
                        continue;
                    var src = type.GetCustomAttribute<UitkxSourceAttribute>();
                    if (src != null && string.Equals(Canon(src.SourcePath), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(type.Name, stem, StringComparison.Ordinal))
                            return type;
                        byName ??= type;
                    }
                }
                return byName;
            }

            if (hint != null)
            {
                var fromHint = Scan(hint);
                if (fromHint != null)
                    return fromHint;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic)
                    continue;
                string name = asm.GetName().Name ?? "";
                if (name.StartsWith("System", StringComparison.Ordinal)
                    || name.StartsWith("Unity", StringComparison.Ordinal)
                    || name.StartsWith("mscorlib", StringComparison.Ordinal)
                    || name.StartsWith("netstandard", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft", StringComparison.Ordinal))
                    continue;
                var found = Scan(asm);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static IProps CreateDefaultProps(Type componentType)
        {
            var propsType = componentType.GetNestedType(componentType.Name + "Props");
            if (propsType == null || !typeof(IProps).IsAssignableFrom(propsType))
                return null;
            try
            {
                return (IProps)Activator.CreateInstance(propsType);
            }
            catch
            {
                return null;
            }
        }

        private void BuildKnobs()
        {
            if (_knobsHost == null)
                return;
            _knobsHost.Clear();
            if (_knobProps == null)
                return;

            foreach (var prop in _knobProps.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || !prop.CanRead)
                    continue;
                var p = prop;
                var t = p.PropertyType;
                if (t == typeof(string))
                {
                    var field = new TextField(p.Name) { value = (string)p.GetValue(_knobProps) ?? "" };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(field);
                }
                else if (t == typeof(int))
                {
                    var field = new IntegerField(p.Name) { value = (int)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(field);
                }
                else if (t == typeof(float))
                {
                    var field = new FloatField(p.Name) { value = (float)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(field);
                }
                else if (t == typeof(bool))
                {
                    var field = new Toggle(p.Name) { value = (bool)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(field);
                }
            }
        }
    }
}
#endif
