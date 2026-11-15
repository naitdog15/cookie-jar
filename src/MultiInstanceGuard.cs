using System;
using System.Threading;

namespace RbxMulti
{
    /// <summary>
    /// Keeps Roblox from recreating its single-instance guards between launches. The event
    /// name is deliberately reserved by a mutex, so it cannot be opened as a routing event.
    /// Mutex ownership belongs to one dedicated thread for the whole launcher lifetime.
    /// </summary>
    internal sealed class MultiInstanceGuard : IDisposable
    {
        internal static readonly MultiInstanceGuard Shared = new MultiInstanceGuard(
            "ROBLOX_singletonMutex", "ROBLOX_singletonEvent");

        private readonly object _gate = new object();
        private readonly string _mutexName;
        private readonly string _eventName;
        private readonly Action<Thread> _startThread;
        private Holder _holder;

        // Separate names let regression tests use isolated kernel objects, never real Roblox.
        internal MultiInstanceGuard(string mutexName, string eventName)
            : this(mutexName, eventName, delegate(Thread thread) { thread.Start(); })
        {
        }

        // The starter seam lets tests reproduce resource failures without exhausting the machine.
        internal MultiInstanceGuard(string mutexName, string eventName, Action<Thread> startThread)
        {
            _mutexName = mutexName;
            _eventName = eventName;
            _startThread = startThread;
        }

        internal bool EnsureReady(out string error)
        {
            lock (_gate)
            {
                error = null;
                if (_holder != null && _holder.Ready) return true;
                if (_holder != null && !StopHolder())
                {
                    error = "The multi-instance guard is still preparing. Try again in a moment.";
                    return false;
                }

                _holder = new Holder(_mutexName, _eventName);
                try { _startThread(_holder.Thread); }
                catch (Exception)
                {
                    StopHolder();
                    error = "Could not start the multi-instance guard. Try again when more system resources are available.";
                    return false;
                }
                if (_holder.Prepared.WaitOne(5000) && _holder.Ready) return true;

                error = _holder.Error ?? "The multi-instance guard did not become ready.";
                _holder.Stop.Set();
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate) { StopHolder(); }
        }

        private bool StopHolder()
        {
            if (_holder == null) return true;
            _holder.Stop.Set();
            if ((_holder.Thread.ThreadState & ThreadState.Unstarted) == 0
                && !_holder.Thread.Join(1000)) return false;
            _holder.Prepared.Close();
            _holder.Stop.Close();
            _holder = null;
            return true;
        }

        private sealed class Holder
        {
            internal readonly ManualResetEvent Prepared = new ManualResetEvent(false);
            internal readonly ManualResetEvent Stop = new ManualResetEvent(false);
            internal readonly Thread Thread;
            internal volatile bool Ready;
            internal string Error;
            private readonly string _mutexName;
            private readonly string _eventName;

            internal Holder(string mutexName, string eventName)
            {
                _mutexName = mutexName;
                _eventName = eventName;
                Thread = new Thread(Run);
                Thread.IsBackground = true;
                Thread.Name = "RobloxMultiInstanceGuard";
            }

            private static bool Own(Mutex mutex)
            {
                try { return mutex.WaitOne(0); }
                catch (AbandonedMutexException) { return true; }
            }

            private void Run()
            {
                Mutex singleton = null;
                Mutex routing = null;
                bool ownsSingleton = false;
                bool ownsRouting = false;
                try
                {
                    singleton = new Mutex(false, _mutexName);
                    ownsSingleton = Own(singleton);
                    if (!ownsSingleton) throw new InvalidOperationException();
                    routing = new Mutex(false, _eventName);
                    ownsRouting = Own(routing);
                    if (!ownsRouting) throw new InvalidOperationException();
                    Ready = true;
                    Prepared.Set();
                    Stop.WaitOne();
                }
                catch (Exception)
                {
                    Error = "Could not reserve Roblox's multi-instance guards. Close Roblox completely "
                        + "(including its tray icon) and other multi-instance tools, then retry. "
                        + "Existing games were left running.";
                }
                finally
                {
                    Ready = false;
                    if (ownsRouting) routing.ReleaseMutex();
                    if (routing != null) routing.Close();
                    if (ownsSingleton) singleton.ReleaseMutex();
                    if (singleton != null) singleton.Close();
                    Prepared.Set();
                }
            }
        }
    }
}
