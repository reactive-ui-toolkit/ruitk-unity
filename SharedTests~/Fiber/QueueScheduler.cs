using System;
using System.Collections.Generic;
using Ruitk.Core;

namespace Ruitk.Shared.Tests.Fiber
{
    /// <summary>
    /// A scheduler that queues and drains only when told to, so a test can put a
    /// slice of render work in flight and decide what happens before it runs.
    ///
    /// The reconciler's other tests use no scheduler at all, which makes every
    /// render synchronous - and a synchronous render can never be interrupted by
    /// a teardown, so the whole class of "the tree went away while work was
    /// queued" is invisible to them.
    /// </summary>
    internal sealed class QueueScheduler : IScheduler
    {
        private readonly Queue<Action> _work = new Queue<Action>();
        private readonly Queue<Action> _effects = new Queue<Action>();

        public int Pending => _work.Count;

        public void Enqueue(Action action, IScheduler.Priority priority = IScheduler.Priority.Normal)
        {
            if (action != null)
            {
                _work.Enqueue(action);
            }
        }

        public void EnqueueBatchedEffect(Action effect)
        {
            if (effect != null)
            {
                _effects.Enqueue(effect);
            }
        }

        public void BeginBatch() { }

        public void EndBatch() { }

        public void PumpNow()
        {
            while (_work.Count > 0)
            {
                _work.Dequeue()();
            }
            while (_effects.Count > 0)
            {
                _effects.Dequeue()();
            }
        }
    }
}
