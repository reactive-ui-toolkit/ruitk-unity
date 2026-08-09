using UnityEngine;

namespace Ruitk.Ugui
{
    /// <summary>
    /// Applies the shared RectTransform/GameObject prop block. Application
    /// order: anchor preset first, then explicit anchor/pivot overrides, then
    /// offsets (OffsetMin/OffsetMax win over AnchoredPosition/SizeDelta when
    /// both encodings are present, matching how the Inspector's fields
    /// interact).
    /// </summary>
    internal static class UguiRectApplier
    {
#if UNITY_EDITOR
#if UNITY_6000_5_OR_NEWER
        private static readonly System.Collections.Generic.HashSet<EntityId> s_drivenHintShown =
            new System.Collections.Generic.HashSet<EntityId>();
#else
        private static readonly System.Collections.Generic.HashSet<int> s_drivenHintShown =
            new System.Collections.Generic.HashSet<int>();
#endif

        private static void HintIfDriven(RectTransform rt, UguiBaseProps props)
        {
            if (rt.drivenByObject == null)
                return;
            bool writesPositional =
                props.AnchoredPosition.HasValue
                || props.SizeDelta.HasValue
                || props.OffsetMin.HasValue
                || props.OffsetMax.HasValue
                || props.Anchors.HasValue
                || props.AnchorMin.HasValue
                || props.AnchorMax.HasValue;
            if (!writesPositional)
                return;
            // 6.5 makes GetInstanceID an error-level obsolete (CS0619) in favour
            // of GetEntityId - and EntityId's int conversion is error-obsolete
            // too, so the dedup set is keyed by the version's native id type.
#if UNITY_6000_5_OR_NEWER
            var id = rt.GetEntityId();
#else
            int id = rt.GetInstanceID();
#endif
            if (!s_drivenHintShown.Add(id))
                return;
            Debug.LogWarning(
                $"[Ruitk.Ugui] '{rt.name}': rect props are driven by "
                    + $"{rt.drivenByObject.GetType().Name} — the written values will be "
                    + "overridden on the next layout pass. Control this element through "
                    + "layoutElement (min/preferred/flexible) or the parent group's "
                    + "settings instead.",
                rt
            );
        }
#endif

        internal static void ApplyFull(GameObject go, UguiBaseProps props)
        {
            if (props == null)
                return;

            if (props.Name != null)
                go.name = props.Name;
            if (props.Layer.HasValue)
                go.layer = props.Layer.Value;
            if (props.Tag != null)
                go.tag = props.Tag;

            var rt = go.transform as RectTransform;
            if (rt != null)
            {
#if UNITY_EDITOR
                HintIfDriven(rt, props);
#endif
                if (props.Anchors.HasValue)
                {
                    UguiAnchorPresets.Resolve(
                        props.Anchors.Value,
                        out var min,
                        out var max,
                        out var pivot
                    );
                    rt.anchorMin = min;
                    rt.anchorMax = max;
                    rt.pivot = pivot;
                }
                if (props.AnchorMin.HasValue)
                    rt.anchorMin = props.AnchorMin.Value;
                if (props.AnchorMax.HasValue)
                    rt.anchorMax = props.AnchorMax.Value;
                if (props.Pivot.HasValue)
                    rt.pivot = props.Pivot.Value;

                if (props.AnchoredPosition.HasValue)
                    rt.anchoredPosition = props.AnchoredPosition.Value;
                if (props.SizeDelta.HasValue)
                    rt.sizeDelta = props.SizeDelta.Value;
                if (props.OffsetMin.HasValue)
                    rt.offsetMin = props.OffsetMin.Value;
                if (props.OffsetMax.HasValue)
                    rt.offsetMax = props.OffsetMax.Value;

                if (props.Rotation.HasValue)
                    rt.localRotation = Quaternion.Euler(props.Rotation.Value);
                else if (props.RotationZ.HasValue)
                    rt.localRotation = Quaternion.Euler(0f, 0f, props.RotationZ.Value);
                if (props.Scale.HasValue)
                    rt.localScale = props.Scale.Value;
                if (props.LocalPositionZ.HasValue)
                {
                    var lp = rt.localPosition;
                    lp.z = props.LocalPositionZ.Value;
                    rt.localPosition = lp;
                }
            }

            if (props.Active.HasValue)
                go.SetActive(props.Active.Value);
        }

        internal static void ApplyDiff(GameObject go, UguiBaseProps prev, UguiBaseProps next)
        {
            if (next == null)
                return;
            if (prev == null)
            {
                ApplyFull(go, next);
                return;
            }

            if (next.Name != prev.Name && next.Name != null)
                go.name = next.Name;
            if (next.Layer != prev.Layer && next.Layer.HasValue)
                go.layer = next.Layer.Value;
            if (next.Tag != prev.Tag && next.Tag != null)
                go.tag = next.Tag;

            var rt = go.transform as RectTransform;
            if (rt != null)
            {
#if UNITY_EDITOR
                HintIfDriven(rt, next);
#endif
                bool anchorsChanged = next.Anchors != prev.Anchors;
                if (anchorsChanged && next.Anchors.HasValue)
                {
                    UguiAnchorPresets.Resolve(
                        next.Anchors.Value,
                        out var min,
                        out var max,
                        out var pivot
                    );
                    rt.anchorMin = min;
                    rt.anchorMax = max;
                    rt.pivot = pivot;
                }
                if ((next.AnchorMin != prev.AnchorMin || anchorsChanged) && next.AnchorMin.HasValue)
                    rt.anchorMin = next.AnchorMin.Value;
                if ((next.AnchorMax != prev.AnchorMax || anchorsChanged) && next.AnchorMax.HasValue)
                    rt.anchorMax = next.AnchorMax.Value;
                if ((next.Pivot != prev.Pivot || anchorsChanged) && next.Pivot.HasValue)
                    rt.pivot = next.Pivot.Value;

                if (next.AnchoredPosition != prev.AnchoredPosition && next.AnchoredPosition.HasValue)
                    rt.anchoredPosition = next.AnchoredPosition.Value;
                if (next.SizeDelta != prev.SizeDelta && next.SizeDelta.HasValue)
                    rt.sizeDelta = next.SizeDelta.Value;
                if (next.OffsetMin != prev.OffsetMin && next.OffsetMin.HasValue)
                    rt.offsetMin = next.OffsetMin.Value;
                if (next.OffsetMax != prev.OffsetMax && next.OffsetMax.HasValue)
                    rt.offsetMax = next.OffsetMax.Value;

                if (next.Rotation != prev.Rotation && next.Rotation.HasValue)
                    rt.localRotation = Quaternion.Euler(next.Rotation.Value);
                else if (next.RotationZ != prev.RotationZ && next.RotationZ.HasValue)
                    rt.localRotation = Quaternion.Euler(0f, 0f, next.RotationZ.Value);
                if (next.Scale != prev.Scale && next.Scale.HasValue)
                    rt.localScale = next.Scale.Value;
                if (next.LocalPositionZ != prev.LocalPositionZ && next.LocalPositionZ.HasValue)
                {
                    var lp = rt.localPosition;
                    lp.z = next.LocalPositionZ.Value;
                    rt.localPosition = lp;
                }
            }

            if (next.Active != prev.Active)
                go.SetActive(next.Active ?? true);
        }
    }
}
