using System.Collections.Generic;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Lightweight per-subsystem frame cost probe. Wrap a Tick() call with
    /// Begin() / End("name") and this accumulates per-second summaries. Only
    /// logs when total per-frame cost exceeds a threshold, so it's silent in
    /// normal play.
    /// </summary>
    public static class PerfProbe
    {
        public static bool Enabled = true;
        public static float LogThresholdMs = 4f;

        private sealed class Entry
        {
            public double TotalSeconds;
            public int    Samples;
            public double MaxSeconds;
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>();
        private static readonly Stack<double> _openStarts = new Stack<double>();

        private static float _nextFlushTime;
        private static int   _framesThisWindow;
        private const float FlushInterval = 1f;

        public static void Begin()
        {
            if (!Enabled) return;
            _openStarts.Push(Time.realtimeSinceStartupAsDouble);
        }

        public static void End(string name)
        {
            if (!Enabled || _openStarts.Count == 0) return;

            double start = _openStarts.Pop();
            double elapsed = Time.realtimeSinceStartupAsDouble - start;

            Entry e;
            if (!_entries.TryGetValue(name, out e))
            {
                e = new Entry();
                _entries[name] = e;
            }
            e.TotalSeconds += elapsed;
            e.Samples++;
            if (elapsed > e.MaxSeconds) e.MaxSeconds = elapsed;
        }

        public static void FrameDone()
        {
            if (!Enabled) return;

            _framesThisWindow++;

            float now = Time.unscaledTime;
            if (_nextFlushTime == 0f)
            {
                _nextFlushTime = now + FlushInterval;
                return;
            }
            if (now < _nextFlushTime) return;
            _nextFlushTime = now + FlushInterval;

            if (_entries.Count == 0)
            {
                _framesThisWindow = 0;
                return;
            }

            double totalMs = 0;
            foreach (var kv in _entries)
                totalMs += kv.Value.TotalSeconds * 1000.0 /
                           System.Math.Max(1, _framesThisWindow);

            if (totalMs < LogThresholdMs)
            {
                Reset();
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("PerfProbe: ").Append(_framesThisWindow).Append(" frames, total ")
              .Append(totalMs.ToString("F2")).Append("ms/frame — ");

            bool first = true;
            foreach (var kv in _entries)
            {
                double ms = kv.Value.TotalSeconds * 1000.0 /
                            System.Math.Max(1, _framesThisWindow);
                double maxMs = kv.Value.MaxSeconds * 1000.0;
                if (!first) sb.Append(" | ");
                first = false;
                sb.Append(kv.Key).Append("=").Append(ms.ToString("F2")).Append("ms")
                  .Append("(max ").Append(maxMs.ToString("F1")).Append(")");
            }

            DiagnosticLog.Info(sb.ToString());
            Reset();
        }

        private static void Reset()
        {
            _entries.Clear();
            _framesThisWindow = 0;
        }
    }
}