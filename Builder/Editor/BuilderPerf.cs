#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// What the builder actually spent its time on, reported once a second while
    /// anything is happening and silent when nothing is.
    ///
    /// The owner's report was "every little change, even a card drag, causes a
    /// compile - insanely slow". Three different things can produce that feeling
    /// and they have three different fixes: a real recompile, a canvas re-render
    /// of every card, or an analyzer pass per keystroke. Guessing between them
    /// cost a round already. Each is counted here with its own clock, so the
    /// answer is one console line rather than an argument.
    ///
    /// Cost of the instrument itself is a counter increment and a
    /// <see cref="System.Diagnostics.Stopwatch"/> per scope; the flush allocates
    /// one string a second, and only when a bucket moved.
    /// </summary>
    internal static class BuilderPerf
    {
        /// <summary>Off by default. The window turns it on with the Trace toggle,
        /// so a normal session pays nothing.</summary>
        public static bool Enabled;

        private sealed class Bucket
        {
            public int Count;
            public double Ms;
            public double WorstMs;
        }

        private static readonly Dictionary<string, Bucket> s_buckets =
            new Dictionary<string, Bucket>(StringComparer.Ordinal);

        private static double s_nextFlush;
        private static bool s_hooked;

        /// <summary>Times one occurrence of <paramref name="bucket"/>. Use with
        /// <c>using</c>; a zero-cost no-op when tracing is off.</summary>
        public struct Scope : IDisposable
        {
            private readonly string _bucket;
            private readonly System.Diagnostics.Stopwatch _sw;

            public Scope(string bucket)
            {
                _bucket = Enabled ? bucket : null;
                _sw = Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            }

            public void Dispose()
            {
                if (_bucket == null || _sw == null)
                    return;
                _sw.Stop();
                Record(_bucket, _sw.Elapsed.TotalMilliseconds);
            }
        }

        public static Scope Measure(string bucket) => new Scope(bucket);

        public static void Record(string bucket, double ms)
        {
            if (!Enabled)
                return;
            if (!s_buckets.TryGetValue(bucket, out var b))
            {
                b = new Bucket();
                s_buckets[bucket] = b;
            }
            b.Count++;
            b.Ms += ms;
            if (ms > b.WorstMs)
                b.WorstMs = ms;
            Hook();
        }

        private static void Hook()
        {
            if (s_hooked)
                return;
            s_hooked = true;
            s_nextFlush = EditorApplication.timeSinceStartup + 1.0;
            EditorApplication.update += Flush;
        }

        private static void Flush()
        {
            if (EditorApplication.timeSinceStartup < s_nextFlush)
                return;
            s_nextFlush = EditorApplication.timeSinceStartup + 1.0;

            if (s_buckets.Count == 0)
            {
                EditorApplication.update -= Flush;
                s_hooked = false;
                return;
            }

            var sb = new System.Text.StringBuilder("[RUITK Builder] perf (last second):");
            foreach (var pair in s_buckets)
            {
                sb.Append("\n    ").Append(pair.Key)
                    .Append("  x").Append(pair.Value.Count)
                    .Append("  total ").Append(pair.Value.Ms.ToString("0.0")).Append("ms")
                    .Append("  worst ").Append(pair.Value.WorstMs.ToString("0.0")).Append("ms");
            }
            s_buckets.Clear();
            Debug.Log(sb.ToString());
        }

        /// <summary>Drops anything counted but not yet reported - called when
        /// tracing is switched off so the next session starts clean.</summary>
        public static void Reset()
        {
            s_buckets.Clear();
            if (s_hooked)
            {
                EditorApplication.update -= Flush;
                s_hooked = false;
            }
        }
    }
}
#endif
