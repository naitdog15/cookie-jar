using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RbxMulti
{
    /// <summary>
    /// Focus mode CPU side: the focused client gets High priority and every logical processor; the other clients are
    /// held at Below Normal on only the last quarter of the processors (at least one), so the focused client has the
    /// rest to itself. Original priority and affinity are remembered and put back by Release / RestoreAll.
    /// UI thread only (not thread safe). Never throws.
    /// </summary>
    public sealed class FocusBoost
    {
        private sealed class Saved
        {
            public ProcessPriorityClass Priority;
            public IntPtr Affinity;
        }

        private readonly Dictionary<int, Saved> _saved = new Dictionary<int, Saved>();
        private readonly HashSet<int> _failed = new HashSet<int>();
        private int _focusPid;

        /// <summary>Optional log sink (called on the UI thread).</summary>
        public Action<string> Log;

        public int FocusPid { get { return _focusPid; } }

        public bool Active { get { return _focusPid > 0; } }

        /// <summary>Mask of the processors the other clients may use: the last quarter, at least one.</summary>
        public static long BackgroundMask(int cores)
        {
            if (cores < 2) return 1L;
            if (cores > 62) cores = 62;
            int n = Math.Max(1, cores / 4);
            long mask = 0;
            for (int i = 0; i < n; i++) mask |= 1L << (cores - 1 - i);
            return mask;
        }

        /// <summary>Boosts focusPid and squeezes the others. Re-applying with the same pids only repairs drift.</summary>
        public void Apply(int focusPid, IList<int> otherPids)
        {
            try
            {
                if (_focusPid != focusPid) Release();
                _focusPid = focusPid;
                int cores = Environment.ProcessorCount;
                long all = cores >= 62 ? long.MaxValue : (1L << cores) - 1;
                long bg = BackgroundMask(cores);

                Set(focusPid, ProcessPriorityClass.High, cores > 1 ? new IntPtr(all) : IntPtr.Zero);
                if (otherPids == null) return;
                for (int i = 0; i < otherPids.Count; i++)
                {
                    int pid = otherPids[i];
                    if (pid <= 0 || pid == focusPid) continue;
                    Set(pid, ProcessPriorityClass.BelowNormal, cores > 1 ? new IntPtr(bg) : IntPtr.Zero);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Puts every touched client back to the priority and affinity it had before Focus.</summary>
        public void Release()
        {
            foreach (KeyValuePair<int, Saved> kv in _saved)
            {
                try
                {
                    using (Process p = Process.GetProcessById(kv.Key))
                    {
                        p.PriorityClass = kv.Value.Priority;
                        if (kv.Value.Affinity != IntPtr.Zero) p.ProcessorAffinity = kv.Value.Affinity;
                    }
                }
                catch (Exception)
                {
                    // the client already exited
                }
            }
            _saved.Clear();
            _failed.Clear();
            _focusPid = 0;
        }

        private void Set(int pid, ProcessPriorityClass priority, IntPtr affinity)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!_saved.ContainsKey(pid))
                    {
                        Saved s = new Saved();
                        s.Priority = p.PriorityClass;
                        s.Affinity = p.ProcessorAffinity;
                        _saved[pid] = s;
                    }
                    if (p.PriorityClass != priority) p.PriorityClass = priority;
                    if (affinity != IntPtr.Zero && p.ProcessorAffinity != affinity) p.ProcessorAffinity = affinity;
                }
            }
            catch (Exception ex)
            {
                if (_failed.Add(pid) && Log != null) Log("Focus could not adjust process " + pid + ": " + ex.Message);
            }
        }
    }
}
