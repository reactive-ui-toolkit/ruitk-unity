#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ruitk.Core;
using UnityEditor;

namespace Ruitk.Builder
{
    /// <summary>
    /// The preview's own scheduler (plan §2.1): EditorRenderScheduler's queues
    /// are process-global statics with no time budget, so a preview component
    /// scheduling unbounded work would starve every other editor mount. This
    /// one is per-pane, budgeted per update tick, and dedupes an action that
    /// is already pending (same delegate instance re-enqueued = one run).
    /// </summary>
    internal sealed class BuilderRenderScheduler : IScheduler, IDisposable
    {
        private const double TickBudgetMs = 4.0;

        private readonly List<Action>[] _queues =
        {
            new List<Action>(), new List<Action>(), new List<Action>(), new List<Action>(),
        };
        private readonly List<Action> _batchedEffects = new List<Action>();
        private int _batchDepth;
        private bool _hooked;
        private bool _disposed;

        public void Enqueue(Action action, IScheduler.Priority priority = IScheduler.Priority.Normal)
        {
            if (action == null || _disposed)
                return;
            var queue = _queues[(int)priority];
            if (!queue.Contains(action))
                queue.Add(action);
            EnsureHooked();
        }

        public void EnqueueBatchedEffect(Action effect)
        {
            if (effect == null || _disposed)
                return;
            if (_batchDepth > 0)
            {
                if (!_batchedEffects.Contains(effect))
                    _batchedEffects.Add(effect);
                return;
            }
            Enqueue(effect, IScheduler.Priority.High);
        }

        public void BeginBatch() => _batchDepth++;

        public void EndBatch()
        {
            if (_batchDepth > 0)
                _batchDepth--;
            if (_batchDepth == 0 && _batchedEffects.Count > 0)
            {
                foreach (var effect in _batchedEffects)
                    Enqueue(effect, IScheduler.Priority.High);
                _batchedEffects.Clear();
            }
        }

        public void PumpNow()
        {
            while (RunOne())
            {
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var queue in _queues)
                queue.Clear();
            _batchedEffects.Clear();
            if (_hooked)
            {
                EditorApplication.update -= Tick;
                _hooked = false;
            }
        }

        private void EnsureHooked()
        {
            if (_hooked || _disposed)
                return;
            EditorApplication.update += Tick;
            _hooked = true;
        }

        private void Tick()
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < TickBudgetMs)
            {
                if (!RunOne())
                    break;
            }
            if (!HasPending() && _hooked)
            {
                EditorApplication.update -= Tick;
                _hooked = false;
            }
        }

        private bool HasPending()
        {
            foreach (var queue in _queues)
                if (queue.Count > 0)
                    return true;
            return false;
        }

        private bool RunOne()
        {
            foreach (var queue in _queues)
            {
                if (queue.Count == 0)
                    continue;
                var action = queue[0];
                queue.RemoveAt(0);
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
                return true;
            }
            return false;
        }
    }
}
#endif
