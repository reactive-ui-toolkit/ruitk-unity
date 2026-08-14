using System.Collections.Generic;
using Ruitk.Props;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public sealed class LabelElementAdapter : BaseElementAdapter
    {
        public override VisualElement Create()
        {
            return new Label();
        }

        public override void ApplyProperties(
            VisualElement element,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (element is Label labelElement && properties != null)
            {
                TryApplyProp<string>(
                    properties,
                    "text",
                    value =>
                    {
                        var newVal = value ?? string.Empty;
                        labelElement.text = newVal;
                    }
                );
                TryApplyProp<bool>(
                    properties,
                    "enableRichText",
                    value =>
                    {
                        labelElement.enableRichText = value;
                    }
                );
            }
            PropsApplier.Apply(element, properties);
        }

        public override void ApplyPropertiesDiff(
            VisualElement element,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (element is Label labelElement)
            {
                TryDiffProp<string>(
                    previous,
                    next,
                    "text",
                    value =>
                    {
                        labelElement.text = value ?? string.Empty;
                    }
                );
                TryDiffProp<bool>(
                    previous,
                    next,
                    "enableRichText",
                    value =>
                    {
                        labelElement.enableRichText = value;
                    }
                );
            }
            PropsApplier.ApplyDiff(element, previous, next);
        }

        public override void ApplyTypedFull(VisualElement element, BaseProps props)
        {
            if (element is Label label && props is LabelProps lp)
            {
                if (lp.Text != null)
                    label.text = lp.Text;
                if (lp.EnableRichText != null)
                    label.enableRichText = lp.EnableRichText.Value;
            }
            base.ApplyTypedFull(element, props);
        }

        public override void ApplyTypedDiff(VisualElement element, BaseProps prev, BaseProps next)
        {
            if (element is Label label && prev is LabelProps lp && next is LabelProps ln)
            {
                if (lp.Text != ln.Text)
                    label.text = ln.Text ?? string.Empty;
                if (lp.EnableRichText != ln.EnableRichText)
                    label.enableRichText = ln.EnableRichText ?? true;
            }
            base.ApplyTypedDiff(element, prev, next);
        }
    }
}
