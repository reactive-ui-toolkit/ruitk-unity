using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public interface IElementAdapter
    {
        VisualElement Create();
        void ApplyProperties(VisualElement element, IReadOnlyDictionary<string, object> props);
        void ApplyPropertiesDiff(
            VisualElement element,
            IReadOnlyDictionary<string, object> prev,
            IReadOnlyDictionary<string, object> next
        );
        VisualElement ResolveChildHost(VisualElement element);
    }
}
