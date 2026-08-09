using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruitk.Core;
using Ruitk.Core.AnimationComponents;
using Ruitk.Core.Util;
using Ruitk.Props.Typed;
using Ruitk.Router;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk
{
    public static partial class V
    {
        // ═══════════════════════════════════════════════════════════════════
        //  Private helpers — rent from pool and set common fields
        // ═══════════════════════════════════════════════════════════════════

        private static VirtualNode RentElement(
            string elementTypeName,
            string key,
            BaseProps hostProps
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Element;
            v._elementTypeName = elementTypeName;
            v._key = key;
            v._hostProps = hostProps;
            return v;
        }

        private static VirtualNode RentElementWithChildren(
            string elementTypeName,
            string key,
            BaseProps hostProps,
            VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Element;
            v._elementTypeName = elementTypeName;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._hostProps = hostProps;
            return v;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Container host elements (with children)
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode VisualElement(
            VisualElementProps props = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("VisualElement", key, props, children);
        }

        public static VirtualNode TemplateContainer(
            TemplateContainerProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("TemplateContainer", key, props, children);
        }

        public static VirtualNode ToggleButtonGroup(
            ToggleButtonGroupProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("ToggleButtonGroup", key, props, children);
        }

        public static VirtualNode Toolbar(
            ToolbarProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("Toolbar", key, props, children);
        }

#if UNITY_EDITOR
        public static VirtualNode TwoPaneSplitView(
            TwoPaneSplitViewProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("TwoPaneSplitView", key, props, children);
        }
#endif

        public static VirtualNode Box(
            BoxProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("Box", key, props, children);
        }

        public static VirtualNode GroupBox(
            GroupBoxProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("GroupBox", key, props, children);
        }

        public static VirtualNode RadioButtonGroup(
            RadioButtonGroupProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("RadioButtonGroup", key, props, children);
        }

        public static VirtualNode ScrollView(
            ScrollViewProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("ScrollView", key, props, children);
        }

        public static VirtualNode Foldout(
            FoldoutProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("Foldout", key, props, children);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Leaf host elements (no children)
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Button(ButtonProps props, string key = null) =>
            RentElement("Button", key, props);

        public static VirtualNode Tab(TabProps props, string key = null) =>
            RentElement("Tab", key, props);

        public static VirtualNode TabView(TabViewProps props, string key = null) =>
            RentElement("TabView", key, props);

        public static VirtualNode TextField(TextFieldProps props, string key = null) =>
            RentElement("TextField", key, props);

        public static VirtualNode EnumField(EnumFieldProps props, string key = null) =>
            RentElement("EnumField", key, props);

        public static VirtualNode ObjectField(ObjectFieldProps props, string key = null) =>
            RentElement("ObjectField", key, props);

        public static VirtualNode Scroller(ScrollerProps props, string key = null) =>
            RentElement("Scroller", key, props);

        public static VirtualNode TextElement(TextElementProps props, string key = null) =>
            RentElement("TextElement", key, props);

        public static VirtualNode IMGUIContainer(IMGUIContainerProps props, string key = null) =>
            RentElement("IMGUIContainer", key, props);

        public static VirtualNode Vector2IntField(Vector2IntFieldProps props, string key = null) =>
            RentElement("Vector2IntField", key, props);

        public static VirtualNode Vector3IntField(Vector3IntFieldProps props, string key = null) =>
            RentElement("Vector3IntField", key, props);

        public static VirtualNode RectField(RectFieldProps props, string key = null) =>
            RentElement("RectField", key, props);

        public static VirtualNode RectIntField(RectIntFieldProps props, string key = null) =>
            RentElement("RectIntField", key, props);

        public static VirtualNode BoundsField(BoundsFieldProps props, string key = null) =>
            RentElement("BoundsField", key, props);

        public static VirtualNode MinMaxSlider(MinMaxSliderProps props, string key = null) =>
            RentElement("MinMaxSlider", key, props);

        public static VirtualNode BoundsIntField(BoundsIntFieldProps props, string key = null) =>
            RentElement("BoundsIntField", key, props);

        public static VirtualNode EnumFlagsField(EnumFlagsFieldProps props, string key = null) =>
            RentElement("EnumFlagsField", key, props);

#if UNITY_6000_4_OR_NEWER
        public static VirtualNode GUIDField(GUIDFieldProps props, string key = null) =>
            RentElement("GUIDField", key, props);
#endif

#if UNITY_6000_5_OR_NEWER
        public static VirtualNode MaskField(MaskFieldProps props, string key = null) =>
            RentElement("MaskField", key, props);

        public static VirtualNode Mask64Field(Mask64FieldProps props, string key = null) =>
            RentElement("Mask64Field", key, props);
#endif

        public static VirtualNode Hash128Field(Hash128FieldProps props, string key = null) =>
            RentElement("Hash128Field", key, props);

        public static VirtualNode ToolbarButton(ToolbarButtonProps props, string key = null) =>
            RentElement("ToolbarButton", key, props);

        public static VirtualNode ToolbarToggle(ToolbarToggleProps props, string key = null) =>
            RentElement("ToolbarToggle", key, props);

        public static VirtualNode ToolbarMenu(ToolbarMenuProps props, string key = null) =>
            RentElement("ToolbarMenu", key, props);

        public static VirtualNode ToolbarBreadcrumbs(
            ToolbarBreadcrumbsProps props,
            string key = null
        ) => RentElement("ToolbarBreadcrumbs", key, props);

        public static VirtualNode ToolbarPopupSearchField(
            ToolbarPopupSearchFieldProps props,
            string key = null
        ) => RentElement("ToolbarPopupSearchField", key, props);

        public static VirtualNode ToolbarSearchField(
            ToolbarSearchFieldProps props,
            string key = null
        ) => RentElement("ToolbarSearchField", key, props);

        public static VirtualNode ToolbarSpacer(ToolbarSpacerProps props, string key = null) =>
            RentElement("ToolbarSpacer", key, props);

        public static VirtualNode PropertyField(PropertyFieldProps props, string key = null) =>
            RentElement("PropertyField", key, props);

        public static VirtualNode InspectorElement(
            InspectorElementProps props,
            string key = null
        ) => RentElement("InspectorElement", key, props);

        public static VirtualNode FloatField(FloatFieldProps props, string key = null) =>
            RentElement("FloatField", key, props);

        public static VirtualNode IntegerField(IntegerFieldProps props, string key = null) =>
            RentElement("IntegerField", key, props);

        public static VirtualNode LongField(LongFieldProps props, string key = null) =>
            RentElement("LongField", key, props);

        public static VirtualNode DoubleField(DoubleFieldProps props, string key = null) =>
            RentElement("DoubleField", key, props);

        public static VirtualNode UnsignedIntegerField(
            UnsignedIntegerFieldProps props,
            string key = null
        ) => RentElement("UnsignedIntegerField", key, props);

        public static VirtualNode UnsignedLongField(
            UnsignedLongFieldProps props,
            string key = null
        ) => RentElement("UnsignedLongField", key, props);

        public static VirtualNode Vector2Field(Vector2FieldProps props, string key = null) =>
            RentElement("Vector2Field", key, props);

        public static VirtualNode Vector3Field(Vector3FieldProps props, string key = null) =>
            RentElement("Vector3Field", key, props);

        public static VirtualNode Vector4Field(Vector4FieldProps props, string key = null) =>
            RentElement("Vector4Field", key, props);

        public static VirtualNode ColorField(ColorFieldProps props, string key = null) =>
            RentElement("ColorField", key, props);

        public static VirtualNode ListView(ListViewProps props, string key = null) =>
            RentElement("ListView", key, props);

        public static VirtualNode TreeView(TreeViewProps props, string key = null) =>
            RentElement("TreeView", key, props);

        public static VirtualNode MultiColumnTreeView(
            MultiColumnTreeViewProps props,
            string key = null
        ) => RentElement("MultiColumnTreeView", key, props);

        public static VirtualNode MultiColumnListView(
            MultiColumnListViewProps props,
            string key = null
        ) => RentElement("MultiColumnListView", key, props);

        public static VirtualNode Label(LabelProps props, string key = null) =>
            RentElement("Label", key, props);

        public static VirtualNode Toggle(ToggleProps props, string key = null) =>
            RentElement("Toggle", key, props);

        public static VirtualNode RadioButton(RadioButtonProps props, string key = null) =>
            RentElement("RadioButton", key, props);

        public static VirtualNode ProgressBar(ProgressBarProps props, string key = null) =>
            RentElement("ProgressBar", key, props);

        public static VirtualNode RepeatButton(RepeatButtonProps props, string key = null) =>
            RentElement("RepeatButton", key, props);

        public static VirtualNode Image(ImageProps props, string key = null) =>
            RentElement("Image", key, props);

        public static VirtualNode HelpBox(HelpBoxProps props, string key = null) =>
            RentElement("HelpBox", key, props);

        public static VirtualNode Slider(SliderProps props, string key = null) =>
            RentElement("Slider", key, props);

        public static VirtualNode SliderInt(SliderIntProps props, string key = null) =>
            RentElement("SliderInt", key, props);

        public static VirtualNode DropdownField(DropdownFieldProps props, string key = null) =>
            RentElement("DropdownField", key, props);

        // ═══════════════════════════════════════════════════════════════════
        //  VisualElementSafe — legacy dict-based path, not pooled
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode VisualElementSafe(
            object elementPropsOrStyle = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            Dictionary<string, object> props;
            Style userStyle = null;

            switch (elementPropsOrStyle)
            {
                case null:
                    props = new Dictionary<string, object>();
                    break;

                case Style style:
                    props = new Dictionary<string, object>();
                    userStyle = style;
                    break;

                case BaseProps baseProps:
                    props = new Dictionary<string, object>(baseProps.ToDictionary());
                    if (
                        props.TryGetValue("style", out var basePropStyleObj)
                        && basePropStyleObj is Style basePropStyle
                    )
                    {
                        userStyle = basePropStyle;
                    }
                    break;

                case IReadOnlyDictionary<string, object> dictionary:
                    props = new Dictionary<string, object>(dictionary);
                    if (
                        props.TryGetValue("style", out var styleObj)
                        && styleObj is Style existingStyle
                    )
                    {
                        userStyle = existingStyle;
                    }
                    break;

                default:
                    throw new ArgumentException(
                        "VisualElementSafe expects either a Style, a BaseProps, or a props dictionary as the first argument.",
                        nameof(elementPropsOrStyle)
                    );
            }

            props["style"] = BuildSafeAreaStyle(userStyle);
            IReadOnlyDictionary<string, object> finalProps = props;
            // Uses public constructor (generation=0) — dict-based path is not pooled
            return new VirtualNode(
                VirtualNodeType.Element,
                elementTypeName: "VisualElement",
                textContent: null,
                key: key,
                properties: finalProps ?? EmptyProps(),
                children: children ?? EmptyChildren()
            );
        }

        private static Style BuildSafeAreaStyle(Style originalStyle)
        {
            var insets = SafeAreaUtility.GetInsets();

            float GetUserPadding(int bit, float fallback)
            {
                if (originalStyle != null && originalStyle.HasBit(bit))
                {
                    var sl = bit switch
                    {
                        Style.BIT_PADDING_LEFT => originalStyle._paddingLeft,
                        Style.BIT_PADDING_RIGHT => originalStyle._paddingRight,
                        Style.BIT_PADDING_TOP => originalStyle._paddingTop,
                        Style.BIT_PADDING_BOTTOM => originalStyle._paddingBottom,
                        _ => default,
                    };
                    return sl.value.value;
                }
                return fallback;
            }

            var merged = new Style();

            // Copy all original style properties first
            if (originalStyle != null)
                originalStyle.CopyTo(merged);

            // Override paddings with safe-area-aware values
            merged.PaddingLeft = Mathf.Max(GetUserPadding(Style.BIT_PADDING_LEFT, 0f), insets.Left);
            merged.PaddingRight = Mathf.Max(
                GetUserPadding(Style.BIT_PADDING_RIGHT, 0f),
                insets.Right
            );
            merged.PaddingTop = Mathf.Max(GetUserPadding(Style.BIT_PADDING_TOP, 0f), insets.Top);
            merged.PaddingBottom = Mathf.Max(
                GetUserPadding(Style.BIT_PADDING_BOTTOM, 0f),
                insets.Bottom
            );

            return merged;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Host — dict-based legacy path, not pooled
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Host(
            VisualElementProps hostProps = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            var propsDict =
                hostProps != null
                    ? hostProps.ToDictionary()
                    : (IReadOnlyDictionary<string, object>)EmptyProps();
            // Uses public constructor (generation=0) — Host uses dict-based props
            return new VirtualNode(
                VirtualNodeType.Host,
                elementTypeName: null,
                textContent: null,
                key: key,
                properties: propsDict,
                children: children ?? EmptyChildren()
            );
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Router (delegates to Func<TProps>)
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Router(
            IRouterHistory history = null,
            string initialPath = null,
            string basename = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func<RouterFuncProps>(
                RouterFunc.Render,
                new RouterFuncProps
                {
                    History = history,
                    InitialPath = initialPath,
                    Basename = basename,
                },
                key,
                children
            );
        }

        public static VirtualNode Route(
            string path = null,
            bool exact = false,
            VirtualNode element = null,
            bool index = false,
            bool caseSensitive = false,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func<RouteFuncProps>(
                RouteFunc.Render,
                new RouteFuncProps
                {
                    Path = path,
                    Exact = exact,
                    Element = element,
                    Index = index,
                    CaseSensitive = caseSensitive,
                },
                key,
                children
            );
        }

        public static VirtualNode Link(
            string to,
            string label = null,
            bool replace = false,
            Style style = null,
            string key = null,
            object state = null
        )
        {
            return Func<LinkFuncProps>(
                LinkFunc.Render,
                new LinkFuncProps
                {
                    To = to,
                    Label = label,
                    Replace = replace,
                    Style = style,
                    State = state,
                },
                key
            );
        }

        public static VirtualNode Outlet(
            object context = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func<OutletFuncProps>(
                OutletFunc.Render,
                new OutletFuncProps { Context = context },
                key,
                children
            );
        }

        public static VirtualNode Routes(string key = null, params VirtualNode[] children)
        {
            return Func(RoutesFunc.Render, Core.EmptyProps.Instance, key, children);
        }

        public static VirtualNode NavLink(
            string to,
            string label = null,
            bool replace = false,
            bool end = false,
            bool caseSensitive = false,
            Style style = null,
            Style activeStyle = null,
            object state = null,
            string key = null
        )
        {
            return Func<NavLinkFuncProps>(
                NavLinkFunc.Render,
                new NavLinkFuncProps
                {
                    To = to,
                    Label = label,
                    Replace = replace,
                    End = end,
                    CaseSensitive = caseSensitive,
                    Style = style,
                    ActiveStyle = activeStyle,
                    State = state,
                },
                key
            );
        }

        public static VirtualNode Navigate(
            string to,
            bool replace = true,
            object state = null,
            string key = null
        )
        {
            return Func<NavigateFuncProps>(
                NavigateFunc.Render,
                new NavigateFuncProps
                {
                    To = to,
                    Replace = replace,
                    State = state,
                },
                key
            );
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Animation (delegates to Func<TProps>)
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Animate(
            AnimateProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func<AnimateProps>(AnimateFunc.Render, props, key, children);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Media — <Audio> / <Video> (delegate to Func<TProps>)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Hidden, side-effect-only Func-Component that rents a pooled
        /// <see cref="UnityEngine.AudioSource"/> from
        /// <see cref="Ruitk.Core.Media.MediaHost"/>, configures it
        /// from <see cref="AudioProps"/>, and returns it on unmount.
        /// Renders a <c>Fragment</c> (no visual output).
        /// </summary>
        public static VirtualNode Audio(
            AudioProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func<AudioProps>(Ruitk.Core.Media.AudioFunc.Render, props, key, children);
        }

        /// <summary>
        /// Real positionable Func-Component that rents a pooled
        /// <see cref="UnityEngine.Video.VideoPlayer"/> and a pooled
        /// <see cref="UnityEngine.RenderTexture"/> from
        /// <see cref="Ruitk.Core.Media.MediaHost"/> and renders the
        /// video into a <c>VisualElement</c>'s <c>backgroundImage</c>.
        /// Accepts overlay children (e.g. play/pause buttons).
        /// </summary>
        public static VirtualNode Video(
            VideoProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            return RentElementWithChildren("Video", key, props, children);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Shared helpers
        // ═══════════════════════════════════════════════════════════════════

        private static IReadOnlyDictionary<string, object> EmptyProps() => VirtualNode.EmptyProps;

        private static IReadOnlyDictionary<string, object> CloneStyleDictionary(
            IReadOnlyDictionary<string, object> source
        )
        {
            if (source == null)
            {
                return null;
            }
            if (!source.ContainsKey("style"))
            {
                return source;
            }
            if (source["style"] is not IDictionary<string, object> styleMap)
            {
                return source;
            }
            var outer = new Dictionary<string, object>(source.Count);
            foreach (var kv in source)
            {
                outer[kv.Key] = kv.Value;
            }
            var styleClone = new Dictionary<string, object>(styleMap.Count);
            foreach (var kv in styleMap)
            {
                styleClone[kv.Key] = kv.Value;
            }
            outer["style"] = styleClone;
            return outer;
        }
    }
}
