using System.Collections.Generic;

namespace Ruitk.Elements
{
    public sealed class ElementRegistry
    {
        private readonly Dictionary<string, IElementAdapter> adaptersByType = new();

        public IReadOnlyCollection<string> RegisteredNames => adaptersByType.Keys;

        public void Register(string elementTypeName, IElementAdapter adapter)
        {
            if (string.IsNullOrWhiteSpace(elementTypeName))
            {
                return;
            }
            if (adapter == null)
            {
                return;
            }
            if (!adaptersByType.ContainsKey(elementTypeName))
            {
                adaptersByType.Add(elementTypeName, adapter);
                return;
            }
            adaptersByType[elementTypeName] = adapter;
        }

        public IElementAdapter Resolve(string elementTypeName)
        {
            if (string.IsNullOrWhiteSpace(elementTypeName))
            {
                return null;
            }

            if (adaptersByType.TryGetValue(elementTypeName, out IElementAdapter adapter))
            {
                return adapter;
            }

            return null;
        }
    }
}
