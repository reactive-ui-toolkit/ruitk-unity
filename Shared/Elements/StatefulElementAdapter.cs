using System;
using System.Runtime.CompilerServices;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public abstract class StatefulElementAdapter<TElement, TState> : BaseElementAdapter
        where TElement : VisualElement
        where TState : class, new()
    {
        private static readonly ConditionalWeakTable<TElement, TState> StateTable = new();

        protected static TState GetState(TElement element)
        {
            return StateTable.GetValue(element, _ => new TState());
        }

        protected static bool TryGetState(TElement element, out TState state)
        {
            return StateTable.TryGetValue(element, out state);
        }

        protected static void RemoveState(TElement element)
        {
            StateTable.Remove(element);
        }
    }
}
