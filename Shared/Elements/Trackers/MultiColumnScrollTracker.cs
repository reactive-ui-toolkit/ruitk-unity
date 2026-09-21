using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    internal sealed class MultiColumnScrollTracker<TView, TState>
        : IElementStateTracker<TView, TState>
        where TView : VisualElement
        where TState : class, IScrollState
    {
        private readonly IScrollOps<TView> _ops;
        private readonly Action<
            TView,
            TState,
            IReadOnlyDictionary<string, object>,
            IReadOnlyDictionary<string, object>
        > _flush;

        public MultiColumnScrollTracker(
            IScrollOps<TView> ops,
            Action<
                TView,
                TState,
                IReadOnlyDictionary<string, object>,
                IReadOnlyDictionary<string, object>
            > flush
        )
        {
            _ops = ops;
            _flush = flush;
        }

        public void Attach(TView view, TState state, IReadOnlyDictionary<string, object> props)
        {
            if (view == null || state == null || _ops == null)
            {
                return;
            }
            if (state.ScrollWired)
            {
                return;
            }
            var sv = _ops.GetScrollView(view);
            if (sv == null)
            {
                return;
            }
            state.ScrollWired = true;

            try
            {
                if (sv.verticalScroller != null)
                {
                    sv.verticalScroller.valueChanged += v =>
                    {
                        state.ScrollY = sv.scrollOffset.y;
                        state.IsScrolling = true;
                        BumpAndArmIdle(view, state);
                    };
                }
            }
            catch { }
            try
            {
                if (sv.horizontalScroller != null)
                {
                    sv.horizontalScroller.valueChanged += v =>
                    {
                        state.ScrollX = sv.scrollOffset.x;
                        state.IsScrolling = true;
                        BumpAndArmIdle(view, state);
                    };
                }
            }
            catch { }

            sv.RegisterCallback<WheelEvent>(
                _ =>
                {
                    state.IsScrolling = true;
                    BumpAndArmIdle(view, state);
                },
                TrickleDown.TrickleDown
            );

            void End()
            {
                if (!state.IsScrolling)
                {
                    return;
                }
                state.IsScrolling = false;
                var prev = state.PendingPrev;
                var next = state.PendingNext;
                state.PendingPrev = null;
                state.PendingNext = null;
                if (_flush == null)
                {
                    return;
                }
                try
                {
                    _flush(view, state, prev, next);
                }
                catch { }
            }

            view.RegisterCallback<PointerUpEvent>(_ => End(), TrickleDown.TrickleDown);
            view.RegisterCallback<PointerCancelEvent>(_ => End(), TrickleDown.TrickleDown);
            view.RegisterCallback<MouseCaptureOutEvent>(_ => End());

            void BumpAndArmIdle(TView v, TState s)
            {
                try
                {
                    int token = ++s.ScrollActivityId;
                    v.schedule?.Execute(() =>
                        {
                            if (s.ScrollActivityId == token)
                            {
                                End();
                            }
                        })
                        ?.ExecuteLater(150);
                }
                catch { }
            }
        }

        public void Detach(TView view, TState state)
        {
            if (state == null)
            {
                return;
            }
            try
            {
                state.IsScrolling = false;
            }
            catch { }
            try
            {
                state.ScrollWired = false;
            }
            catch { }
            try
            {
                state.PendingPrev = null;
            }
            catch { }
            try
            {
                state.PendingNext = null;
            }
            catch { }
        }

        public void Reapply(
            TView view,
            TState state,
            IReadOnlyDictionary<string, object> previousProps,
            IReadOnlyDictionary<string, object> nextProps
        )
        {
            if (view == null || state == null)
            {
                return;
            }
            if (state.IsScrolling)
            {
                state.PendingPrev = previousProps;
                state.PendingNext = nextProps;
                return;
            }

            var sv = _ops.GetScrollView(view);
            if (sv == null)
            {
                return;
            }
            try
            {
                var off = sv.scrollOffset;
                bool needX = state.ScrollX != off.x;
                bool needY = state.ScrollY != off.y;
                if (needX || needY)
                {
                    sv.scrollOffset = new UnityEngine.Vector2(
                        needX ? state.ScrollX : off.x,
                        needY ? state.ScrollY : off.y
                    );
                }
            }
            catch { }
        }
    }
}
