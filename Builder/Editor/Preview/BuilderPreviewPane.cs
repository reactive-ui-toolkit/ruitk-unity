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
        private bool _showingNoPreview;

        public event Action<string> ComponentPicked;

        /// <summary>POC firstUsageProps + collectExprs: the window hands the pane
        /// (a) the card that first instantiates this component and the attribute
        /// pairs of that usage, and (b) the component's own expression blob, which
        /// is what object-prop knobs are mined from.</summary>
        public Func<string, (string Owner, string UsageAttrs, string Blob)> UsageProvider;

        /// <summary>POC ".nopreview" for a hook module names the exposed signature
        /// and the components that consume it ("Exposes <b>useCart(gold, setGold) →
        /// (Count, Buy)</b>. Consumed by <b>ShopScreen</b>"). Both live on the real
        /// graph, so the window supplies them per file.</summary>
        public Func<string, (string Signature, string Consumers)> ModuleInfoProvider;

        private string _seededFrom;

        /// <summary>POC ".knobs .krow { gap:8px; margin-bottom:6px; font-size:12px }"
        /// with ".knobs label { width:70px }" and a fixed 110px value box painted on
        /// the ".knobs input" ground (var(--panel2) #2a2a31 — NOT the #17171b the
        /// library/context search boxes use) — never Unity's grey inspector field.</summary>
        internal static T Field<T>(T field) where T : VisualElement
        {
            field.style.marginLeft = 4f;
            field.style.marginBottom = 6f;
            if (field is Toggle toggleField)
            {
                toggleField.labelElement.style.color = new Color(0.545f, 0.545f, 0.588f);
                toggleField.labelElement.style.fontSize = 12f;
            }
            var label = field.Q<Label>(className: "unity-base-field__label");
            if (label != null)
            {
                label.style.color = new Color(0.545f, 0.545f, 0.588f);
                label.style.minWidth = 70f;
                label.style.width = 70f;
                label.style.fontSize = 12f;
            }
            var input = field.Q(className: "unity-base-field__input");
            if (input != null)
            {
                StyleInput(input, KnobGround);
                input.style.fontSize = 12f;
                // POC ".knobs input[type=text|number] { width: 110px }": the value box
                // is a fixed plate, not a stretch — only input[type=range] is flex:1.
                if (!(field is Toggle) && !(field is Slider) && !(field is SliderInt))
                {
                    input.style.flexGrow = 0f;
                    input.style.flexShrink = 0f;
                    input.style.width = 110f;
                }
            }
            return field;
        }

        /// <summary>POC "#lib-search { background: #17171b }" — the search ground.</summary>
        internal static readonly Color SearchGround = new Color(0.090f, 0.090f, 0.106f);

        /// <summary>POC ".knobs input { background: var(--panel2) }" = #2a2a31.</summary>
        internal static readonly Color KnobGround = new Color(0.165f, 0.165f, 0.192f);

        /// <summary>POC "#lib-search / .ctx-search / .knobs input" box chrome. The two
        /// grounds differ (#17171b for the search boxes, var(--panel2) for the knob and
        /// state fields), so the caller names the one it wants.</summary>
        internal static void StyleInput(VisualElement input) => StyleInput(input, SearchGround);

        internal static void StyleInput(VisualElement input, Color ground)
        {
            input.style.backgroundColor = ground;
            input.style.color = new Color(0.839f, 0.839f, 0.863f);
            input.style.borderTopWidth = 1f;
            input.style.borderBottomWidth = 1f;
            input.style.borderLeftWidth = 1f;
            input.style.borderRightWidth = 1f;
            var line = new Color(0.227f, 0.227f, 0.267f);
            input.style.borderTopColor = line;
            input.style.borderBottomColor = line;
            input.style.borderLeftColor = line;
            input.style.borderRightColor = line;
            input.style.borderTopLeftRadius = 4f;
            input.style.borderTopRightRadius = 4f;
            input.style.borderBottomLeftRadius = 4f;
            input.style.borderBottomRightRadius = 4f;
            input.style.paddingTop = 2f;
            input.style.paddingBottom = 2f;
            input.style.paddingLeft = 6f;
            input.style.paddingRight = 6f;
            // Styling only the outer element leaves Unity's own inner input box
            // painted on top of it, so the field read as two nested outlines. When
            // the outer element owns the chrome the inner one must be flat.
            var inner = input.Q(className: "unity-base-text-field__input");
            if (inner != null && inner != input)
            {
                inner.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
                inner.style.color = new Color(0.839f, 0.839f, 0.863f);
                inner.style.borderTopWidth = 0f;
                inner.style.borderBottomWidth = 0f;
                inner.style.borderLeftWidth = 0f;
                inner.style.borderRightWidth = 0f;
                inner.style.paddingTop = 0f;
                inner.style.paddingBottom = 0f;
                inner.style.paddingLeft = 0f;
                inner.style.paddingRight = 0f;
                inner.style.marginTop = 0f;
                inner.style.marginBottom = 0f;
                inner.style.marginLeft = 0f;
                inner.style.marginRight = 0f;
            }
            // POC "#lib-search:focus / .ctx-search:focus { border-color: accent }".
            var accent = new Color(0.310f, 0.765f, 0.969f);
            input.RegisterCallback<FocusInEvent>(_ => BuilderWindow.SetBorderColor(input, accent));
            input.RegisterCallback<FocusOutEvent>(_ => BuilderWindow.SetBorderColor(input, line));
        }

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
                // POC ".nopreview" bolds the nouns that name the thing.
                enableRichText = true,
                style =
                {
                    marginTop = 8f, marginLeft = 8f, marginRight = 8f,
                    color = new Color(0.545f, 0.545f, 0.588f),
                    unityFontStyleAndWeight = FontStyle.Italic,
                    whiteSpace = WhiteSpace.Normal,
                },
            };
            container.Add(_statusLabel);

            // POC ".gp-root": every render sits inside a framed stage —
            // background #101014, 1px var(--line), 8px radius, 10px padding.
            // "#preview" itself carries no rule of its own.
            // The stage is CONTENT-sized in the POC (".gp-root" declares no
            // flex-grow), so the knobs/STATE strip always sits directly under the
            // render. flexGrow:1 here stretched the stage over the whole 380px
            // body and pushed the strip off the bottom of the pane entirely.
            _previewHost = new VisualElement
            {
                name = "builder-preview-host",
                style =
                {
                    flexGrow = 0f,
                    flexShrink = 0f,
                    flexDirection = FlexDirection.Column,
                    backgroundColor = new Color(0.063f, 0.063f, 0.078f),
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopColor = Line, borderBottomColor = Line,
                    borderLeftColor = Line, borderRightColor = Line,
                    borderTopLeftRadius = 8f, borderTopRightRadius = 8f,
                    borderBottomLeftRadius = 8f, borderBottomRightRadius = 8f,
                    paddingTop = 10f, paddingBottom = 10f,
                    paddingLeft = 10f, paddingRight = 10f,
                },
            };
            _previewHost.RegisterCallback<PointerDownEvent>(OnPreviewPicked, TrickleDown.TrickleDown);
            container.Add(_previewHost);

            // POC ".knobs": ONE block with the dashed top rule, emitted whenever
            // there is a preview — props header + knobs, then the state header +
            // state rows, then the two dim notes LAST.
            _knobsBlock = new VisualElement
            {
                name = "builder-preview-knobs-block",
                style =
                {
                    flexShrink = 0f,
                    marginTop = 10f,
                    display = DisplayStyle.None,
                },
            };
            container.Add(_knobsBlock);

            // POC ".knobs { border-top: 1px DASHED var(--line) }". UI Toolkit has no
            // dashed border style at all, so a solid rule was standing in; the rule
            // is painted as a dash run on its own 1px strip instead.
            var dashRule = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style = { height = 1f, flexShrink = 0f, marginBottom = 8f },
            };
            dashRule.generateVisualContent += BuilderCanvasDrawing.DrawDashedRule;
            _knobsBlock.Add(dashRule);

            // POC: the section headers are emitted only `if (rows.length)` /
            // `if (stateRows.length)` — a style/hook/util module shows neither.
            _knobsHeader = new Label("PROPS — AUTO-GENERATED KNOBS")
            {
                style =
                {
                    color = new Color(0.545f, 0.545f, 0.588f),
                    fontSize = 10f,
                    letterSpacing = 1f,
                    marginLeft = 8f, marginBottom = 6f,
                    display = DisplayStyle.None,
                },
            };
            _knobsBlock.Add(_knobsHeader);
            _knobsHost = new VisualElement
            {
                name = "builder-preview-knobs",
                style = { flexShrink = 0f, maxHeight = 220f, paddingLeft = 4f },
            };
            _knobsBlock.Add(_knobsHost);

            _stateHeader = new Label("STATE — LIVE HOOK VALUES")
            {
                style =
                {
                    color = new Color(0.545f, 0.545f, 0.588f),
                    fontSize = 10f,
                    letterSpacing = 1f,
                    marginTop = 8f, marginLeft = 8f, marginBottom = 6f,
                    display = DisplayStyle.None,
                },
            };
            _knobsBlock.Add(_stateHeader);
            _stateHost = new VisualElement
            {
                // 4px here plus Field's own 4px margin lands the rows on the same
                // 8px gutter the section headers use — the POC's krows sit flush
                // under "STATE — LIVE HOOK VALUES", not indented past it.
                style = { flexShrink = 0f, maxHeight = 140f, paddingLeft = 4f, paddingBottom = 4f },
            };
            _knobsBlock.Add(_stateHost);
            _notesHost = new VisualElement { style = { flexShrink = 0f } };
            _knobsBlock.Add(_notesHost);
            EditorApplication.update += TickStatePanel;
        }

        private static readonly Color Line = new Color(0.227f, 0.227f, 0.267f);

        private VisualElement _knobsBlock;
        private VisualElement _notesHost;
        private Label _knobsHeader;
        private Label _stateHeader;
        private VisualElement _stateHost;
        private double _nextStateRefresh;
        private readonly List<string> _slotNames = new List<string>();

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
            var target = root == null ? null : FindFocusedFiber(root);
            var states = target?.ComponentState?.HookStates;
            int shown = 0;
            if (states != null)
            {
                // POC stateRows are built from node.hooks of the SELECTED component
                // only — a child's internals never surface here.
                for (int i = 0; i < states.Count && i < _slotNames.Count && shown < 12; i++)
                {
                    // POC stateRows are built FROM the declared names, so a slot with
                    // no useState name (a useMemo/useRef cell) simply has no row.
                    if (string.IsNullOrEmpty(_slotNames[i]))
                        continue;
                    var field = BuildStateField(_slotNames[i], states, i);
                    if (field == null)
                        continue;
                    _stateHost.Add(field);
                    shown++;
                }
            }
            if (_stateHeader != null)
                _stateHeader.style.display = shown > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>The preview mounts the component through V.Func(delegate,…),
        /// NOT the Family overload, so every fiber it owns has a null Family and the
        /// render method is the identity. A tree that somehow reports no match still
        /// has exactly one top-most stateful fiber — the component this pane
        /// mounted — so that is the fallback rather than showing nothing.</summary>
        private FiberNode FindFocusedFiber(FiberNode fiber)
        {
            FiberNode firstStateful = null;

            FiberNode Walk(FiberNode node)
            {
                while (node != null)
                {
                    var hookStates = node.ComponentState?.HookStates;
                    if (hookStates != null && hookStates.Count > 0)
                    {
                        if (_renderDelegate != null && node.TypedRender != null
                            && node.TypedRender.Method == _renderDelegate.Method)
                            return node;
                        firstStateful ??= node;
                    }
                    var found = Walk(node.Child);
                    if (found != null)
                        return found;
                    node = node.Sibling;
                }
                return null;
            }

            return Walk(fiber) ?? firstStateful;
        }

        private static readonly System.Text.RegularExpressions.Regex s_hookCall =
            new System.Text.RegularExpressions.Regex(
                @"(?:var\s*(?:\(\s*(?<n>[A-Za-z_]\w*)\s*,[^)]*\)|(?<n>[A-Za-z_]\w*))\s*=\s*)?"
                + @"\b(?<hook>use[A-Z]\w*)\s*(?:<[^>\n]*>)?\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Hooks that take one HookStates slot but carry no useState name
        /// (their row is skipped, their SLOT is not — the list is positional).
        /// useEffect/useLayoutEffect run off a separate effect index and take no
        /// slot at all.</summary>
        private static readonly HashSet<string> s_slotHooks = new HashSet<string>(StringComparer.Ordinal)
        {
            "useState", "useReducer", "useMemo", "useCallback", "useRef",
            "useStableFunc", "useSafeArea",
        };

        private static readonly HashSet<string> s_noSlotHooks = new HashSet<string>(StringComparer.Ordinal)
        {
            "useEffect", "useLayoutEffect",
        };

        /// <summary>One entry per HookStates slot in declaration order — the
        /// useState name, or null for a slot-consuming hook that has no state row.
        /// Indexing the fiber's HookStates by useState ORDINAL was wrong the moment
        /// a useMemo/useRef sat between two useStates, and the walk stops at the
        /// first custom hook because its own slot count is unknowable from here.</summary>
        private void CollectStateNames(string bufferText)
        {
            _slotNames.Clear();
            if (string.IsNullOrEmpty(bufferText))
                return;
            foreach (System.Text.RegularExpressions.Match m in s_hookCall.Matches(bufferText))
            {
                string hook = m.Groups["hook"].Value;
                if (s_noSlotHooks.Contains(hook))
                    continue;
                if (hook == "useState")
                {
                    _slotNames.Add(m.Groups["n"].Success ? m.Groups["n"].Value : null);
                    continue;
                }
                if (s_slotHooks.Contains(hook))
                {
                    _slotNames.Add(null);
                    continue;
                }
                return;
            }
        }

        /// <summary>POC §7: state fields are EDITABLE — an int/float/string/bool
        /// hook cell renders as a live input writing straight back into the
        /// fiber's state store, then re-renders the preview.
        /// A useState slot holds the VALUE ITSELF (Hooks.UseState stores `initial`
        /// straight into HookStates), not a wrapper with a Value/Current property —
        /// so the wrapper probe rejected every primitive cell, BuildStateField
        /// returned null for all of them and the STATE section never appeared. The
        /// wrapper probe is kept for the cells that really are wrappers
        /// (useRef's Ref&lt;T&gt;, useReducer's ReducerHookState).</summary>
        private VisualElement BuildStateField(string label, IList<object> states, int slot)
        {
            object cell = states[slot];
            var (holder, member, wrapped) = ResolveStateCell(cell);
            object value = holder == null ? cell : wrapped;
            if (value == null)
                return null;

            void Write(object next)
            {
                try
                {
                    if (holder == null)
                        states[slot] = next;
                    else if (member is PropertyInfo prop)
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
                    return Field(field);
                }
                case float f:
                {
                    var field = new FloatField(label) { value = f };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return Field(field);
                }
                case bool b:
                {
                    var field = new Toggle(label) { value = b };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return Field(field);
                }
                case string s:
                {
                    var field = new TextField(label) { value = s };
                    field.labelElement.style.color = rowStyle;
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    return Field(field);
                }
                case null:
                    return null;
                default:
                {
                    // A non-primitive cell used to render as one bare full-width
                    // Label carrying "name = <ToString()>", which ran straight off
                    // the pane. It goes through the same 70px-label + boxed-value
                    // shape as every other row, read-only and ellipsised.
                    var row = new VisualElement
                    {
                        style =
                        {
                            flexDirection = FlexDirection.Row,
                            alignItems = Align.Center,
                            marginLeft = 4f,
                            marginBottom = 6f,
                        },
                    };
                    row.Add(new Label(label)
                    {
                        style =
                        {
                            color = rowStyle, minWidth = 70f, width = 70f,
                            flexShrink = 0f, fontSize = 12f,
                        },
                    });
                    var box = new Label(value.ToString())
                    {
                        tooltip = value.ToString(),
                        style =
                        {
                            flexGrow = 0f, flexShrink = 0f, width = 110f, minWidth = 0f,
                            fontSize = 12f,
                            whiteSpace = WhiteSpace.NoWrap,
                            overflow = Overflow.Hidden,
                            textOverflow = TextOverflow.Ellipsis,
                        },
                    };
                    StyleInput(box, KnobGround);
                    row.Add(box);
                    return row;
                }
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
            CollectStateNames(bufferText);

            // POC ".nopreview": a style / hook / util module never resolves to a
            // renderable component — classify by file name FIRST, because the
            // generator stamps [UitkxSource] on their __Exports class too and the
            // byName fallback would otherwise surface "__Exports".
            var type = IsModuleFile(uitkxPath) ? null : ResolveComponentType(uitkxPath, assemblyHint);
            Type owner = type;
            var render = type == null ? null : type.GetMethod(
                "Render",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(IProps), typeof(IReadOnlyList<VirtualNode>) },
                null);
            if (owner != null && render != null)
                RuntimeHelpers.RunModuleConstructor(owner.Module.ModuleHandle);
            if (type == null || render == null)
            {
                _showingNoPreview = true;
                SetStatus(NoPreviewText(uitkxPath));
                UnmountPreview();
                if (_knobsHost != null)
                    _knobsHost.Clear();
                if (_notesHost != null)
                    _notesHost.Clear();
                if (_knobsHeader != null)
                    _knobsHeader.style.display = DisplayStyle.None;
                // POC ".nopreview" replaces the whole stage — no gp-root frame and
                // no knobs block for a module that renders nothing.
                if (_knobsBlock != null)
                    _knobsBlock.style.display = DisplayStyle.None;
                if (_previewHost != null)
                    _previewHost.style.display = DisplayStyle.None;
                return;
            }
            _showingNoPreview = false;
            if (_previewHost != null)
                _previewHost.style.display = DisplayStyle.Flex;
            if (_knobsBlock != null)
                _knobsBlock.style.display = DisplayStyle.Flex;

            // POC pvGeneric's per-component store: a recompile hands back a NEW
            // type object from the swap assembly, so identity is the source file
            // plus the props shape — never reference equality, which would wipe
            // every knob the user typed on each 300 ms recompile.
            bool sameComponent = _renderDelegate != null
                && string.Equals(SourceOf(_componentType), SourceOf(type), StringComparison.OrdinalIgnoreCase)
                && PropsSignature(_componentType) == PropsSignature(type);
            var previousProps = _knobProps;
            _componentType = type;
            _renderDelegate = (Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>)
                Delegate.CreateDelegate(
                    typeof(Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>), render);

            if (!sameComponent)
            {
                _knobProps = CreateDefaultProps(type);
                CarryOver(previousProps, _knobProps);
                BuildKnobs();
            }
            else if (_knobProps == null)
            {
                _knobProps = CreateDefaultProps(type);
                BuildKnobs();
            }

            // POC: the component's identity lives in the pane title's right slot
            // (<SHOPSCREEN>), never as a line above the render.
            SetStatus(null);
            Mount();
        }

        private static bool IsModuleFile(string uitkxPath)
        {
            string name = Path.GetFileName(uitkxPath) ?? "";
            return name.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase);
        }

        private static string SourceOf(Type type)
        {
            var src = type?.GetCustomAttribute<UitkxSourceAttribute>();
            return src == null ? type?.FullName ?? "" : Canon(src.SourcePath);
        }

        private static string PropsSignature(Type componentType)
        {
            var propsType = componentType?.GetNestedType(componentType.Name + "Props");
            if (propsType == null)
                return "";
            var names = new List<string>();
            foreach (var p in propsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                names.Add(p.Name + ":" + p.PropertyType.Name);
            names.Sort(StringComparer.Ordinal);
            return string.Join(",", names);
        }

        /// <summary>Knob values the user typed survive a recompile: matching
        /// property names copy across onto the fresh props instance.</summary>
        private static void CarryOver(IProps from, IProps to)
        {
            if (from == null || to == null)
                return;
            foreach (var target in to.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!target.CanWrite)
                    continue;
                var source = from.GetType().GetProperty(target.Name, BindingFlags.Public | BindingFlags.Instance);
                if (source == null || !source.CanRead || source.PropertyType != target.PropertyType)
                    continue;
                try
                {
                    target.SetValue(to, source.GetValue(from));
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>The graph loads asynchronously, so the first ShowFile for a
        /// module runs while ModuleInfoProvider still has no nodes and both
        /// clauses fall back to the generic phrasing — which then contradicts the
        /// signature line and the green edge the canvas draws a moment later. The
        /// window calls this once the graph is in, so the copy names the real
        /// export signature and the real consumers.</summary>
        public void RefreshModuleNotes()
        {
            if (_showingNoPreview && !string.IsNullOrEmpty(_filePath))
                SetStatus(NoPreviewText(_filePath));
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
            if (_renderer == null)
                return;
            var picked = evt.target as VisualElement;
            if (picked == null)
                return;
            // POC: a plain left click selects the owning component; interactive
            // controls (`ev.target.tagName !== "BUTTON"`) keep their own clicks.
            for (var el = picked; el != null && el != _previewHost; el = el.parent)
                if (el is Button || el is TextField || el is Toggle || el is Slider)
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
        private string NoPreviewText(string uitkxPath)
        {
            string name = Path.GetFileName(uitkxPath) ?? "";
            if (name.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase))
            {
                string signature = "";
                string consumers = "";
                if (ModuleInfoProvider != null)
                {
                    try
                    {
                        var info = ModuleInfoProvider(uitkxPath);
                        signature = info.Signature ?? "";
                        consumers = info.Consumers ?? "";
                    }
                    catch (Exception)
                    {
                    }
                }
                // The graph already carries the parsed export header and the
                // incoming hook edges; the generic clause is only the fallback for
                // a module nothing imports yet.
                string exposes = signature.Length > 0
                    ? "Exposes <b>" + signature + "</b>. "
                    : "It <b>exposes its hooks</b> to the components that import it. ";
                string consumed = consumers.Length > 0
                    ? "Consumed by <b>" + consumers + "</b> (green edge). "
                    : "No component imports it yet (green edge). ";
                return "Hook module — no visual. " + exposes + consumed
                    + "Double-click its code island to edit the body.";
            }
            if (name.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase))
                return "Style module — no visual of its own. Zoom to L2 and click an entry to "
                    + "edit it. Tip: select <b>the component that imports it</b> first, then edit "
                    + "<b>an entry</b>'s BackgroundColor hex — the live preview repaints.";
            return "Util module — no visual. Its exported functions land on the file's "
                + "<b>__Exports</b> class; drag the module onto a component to import them by name.";
        }

        private void SetStatus(string text)
        {
            if (_statusLabel == null)
                return;
            _statusLabel.text = text ?? "";
            _statusLabel.style.display = string.IsNullOrEmpty(text)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
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

        private static readonly System.Text.RegularExpressions.Regex s_numberish =
            new System.Text.RegularExpressions.Regex(
                @"price|count|stock|gold|amount|size|width|height|hp|level|score|id$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        private void BuildKnobs()
        {
            if (_knobsHost == null)
                return;
            _knobsHost.Clear();
            _seededFrom = null;
            if (_knobsHeader != null)
                _knobsHeader.style.display = DisplayStyle.None;

            string usageAttrs = "";
            string blob = "";
            if (UsageProvider != null && _filePath != null)
            {
                try
                {
                    var usage = UsageProvider(_filePath);
                    _seededFrom = usage.Owner;
                    usageAttrs = usage.UsageAttrs ?? "";
                    blob = usage.Blob ?? "";
                }
                catch (Exception)
                {
                }
            }

            // A component with no generated Props type still gets the note rows —
            // the POC appends them unconditionally as the last rows of the block,
            // so the early return that used to sit above this loop dropped them.
            foreach (var prop in _knobProps == null
                ? System.Array.Empty<PropertyInfo>()
                : _knobProps.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || !prop.CanRead)
                    continue;
                var p = prop;
                var t = p.PropertyType;
                if (typeof(Delegate).IsAssignableFrom(t))
                    continue;
                if (t == typeof(string))
                {
                    string seeded = SeededLiteral(usageAttrs, p.Name);
                    if (seeded != null)
                        p.SetValue(_knobProps, seeded);
                    var field = new TextField(p.Name) { value = (string)p.GetValue(_knobProps) ?? "" };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(Field(field));
                }
                else if (t == typeof(int))
                {
                    string seeded = SeededLiteral(usageAttrs, p.Name);
                    if (seeded != null && int.TryParse(seeded, out int seededInt))
                        p.SetValue(_knobProps, seededInt);
                    var field = new IntegerField(p.Name) { value = (int)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(Field(field));
                }
                else if (t == typeof(float))
                {
                    string seeded = SeededLiteral(usageAttrs, p.Name);
                    if (seeded != null && float.TryParse(seeded, out float seededFloat))
                        p.SetValue(_knobProps, seededFloat);
                    var field = new FloatField(p.Name) { value = (float)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(Field(field));
                }
                else if (t == typeof(bool))
                {
                    string seeded = SeededLiteral(usageAttrs, p.Name);
                    if (seeded != null && bool.TryParse(seeded, out bool seededBool))
                        p.SetValue(_knobProps, seededBool);
                    var field = new Toggle(p.Name) { value = (bool)p.GetValue(_knobProps) };
                    field.RegisterValueChangedCallback(e => { p.SetValue(_knobProps, e.newValue); Mount(); });
                    _knobsHost.Add(Field(field));
                }
                else
                {
                    BuildMemberKnobs(p, blob);
                }
            }

            if (_knobsHeader != null && _knobsHost.childCount > 0)
                _knobsHeader.style.display = DisplayStyle.Flex;
            // POC pvGeneric: the seeded-from line names the card the defaults came
            // from, then the generic rendering note — both AFTER the state rows,
            // as the LAST rows of the knobs block.
            if (_notesHost == null)
                return;
            _notesHost.Clear();
            if (!string.IsNullOrEmpty(_seededFrom))
                _notesHost.Add(Note("knob defaults taken from its usage in " + _seededFrom));
            _notesHost.Add(Note("rendered from the real component — every edit re-renders"));
        }

        private static Label Note(string text) => new Label(text)
        {
            style =
            {
                color = new Color(0.545f, 0.545f, 0.588f),
                fontSize = 11f,
                whiteSpace = WhiteSpace.Normal,
                marginTop = 4f, marginLeft = 78f,
            },
        };

        /// <summary>POC firstUsageProps: a usage attribute whose value is a plain
        /// literal ("SOLD OUT", 100, true) seeds the matching knob; an expression
        /// ({item}) is skipped exactly like the POC's <c>typeof v !== "object"</c>
        /// guard.</summary>
        private static string SeededLiteral(string usageAttrs, string propName)
        {
            if (string.IsNullOrEmpty(usageAttrs))
                return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                usageAttrs, "\\b" + System.Text.RegularExpressions.Regex.Escape(propName)
                + "=(\\{[^}]*\\}|\"[^\"]*\")");
            if (!m.Success)
                return null;
            string raw = m.Groups[1].Value;
            if (raw.StartsWith("\"", StringComparison.Ordinal))
                return raw.Substring(1, raw.Length - 2);
            string inner = raw.Substring(1, raw.Length - 2).Trim();
            bool literal = inner.Length > 0
                && (char.IsDigit(inner[0]) || inner == "true" || inner == "false"
                    || inner[0] == '-' || inner[0] == '"');
            return literal ? inner.Trim('"') : null;
        }

        /// <summary>POC: a non-primitive prop gets ONE knob per member the card's
        /// expressions actually reach (<c>item.Name</c>, <c>item.Price</c>), typed
        /// number/text by the POC's name heuristic.</summary>
        private void BuildMemberKnobs(PropertyInfo prop, string blob)
        {
            if (string.IsNullOrEmpty(blob))
                return;
            object holder;
            try
            {
                holder = prop.GetValue(_knobProps);
                if (holder == null)
                {
                    holder = Activator.CreateInstance(prop.PropertyType);
                    prop.SetValue(_knobProps, holder);
                }
            }
            catch (Exception)
            {
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(
                    blob, "\\b" + System.Text.RegularExpressions.Regex.Escape(prop.Name)
                    + "\\.([A-Za-z_]\\w*)"))
            {
                string member = m.Groups[1].Value;
                if (!seen.Add(member))
                    continue;
                var target = prop.PropertyType.GetProperty(
                    member, BindingFlags.Public | BindingFlags.Instance);
                MemberInfo info = target;
                Type memberType = target?.PropertyType;
                if (target == null || !target.CanWrite)
                {
                    var fieldInfo = prop.PropertyType.GetField(
                        member, BindingFlags.Public | BindingFlags.Instance);
                    if (fieldInfo == null)
                        continue;
                    info = fieldInfo;
                    memberType = fieldInfo.FieldType;
                }
                string label = prop.Name + "." + member;
                var owner = holder;
                void Seed(object value)
                {
                    try
                    {
                        if (info is PropertyInfo pi)
                            pi.SetValue(owner, value);
                        else if (info is FieldInfo fi)
                            fi.SetValue(owner, value);
                    }
                    catch (Exception)
                    {
                    }
                }
                void Write(object value)
                {
                    Seed(value);
                    Mount();
                }
                bool numberish = s_numberish.IsMatch(member);
                if (memberType == typeof(int))
                {
                    Seed(1);
                    var field = new IntegerField(label) { value = 1 };
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    _knobsHost.Add(Field(field));
                }
                else if (memberType == typeof(float))
                {
                    Seed(1f);
                    var field = new FloatField(label) { value = 1f };
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    _knobsHost.Add(Field(field));
                }
                else if (memberType == typeof(bool))
                {
                    var field = new Toggle(label) { value = false };
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    _knobsHost.Add(Field(field));
                }
                else if (memberType == typeof(string))
                {
                    // POC getKnobDefault: a number-ish member seeds 1, everything
                    // else seeds its own member name ("Name").
                    string seed = numberish ? "1" : member;
                    Seed(seed);
                    var field = new TextField(label) { value = seed };
                    field.RegisterValueChangedCallback(e => Write(e.newValue));
                    _knobsHost.Add(Field(field));
                }
            }
        }
    }
}
#endif
