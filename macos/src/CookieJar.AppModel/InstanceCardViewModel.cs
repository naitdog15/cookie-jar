using System;
using System.Collections.Generic;
using System.ComponentModel;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>
    /// One row in the instance list. Forwards the wrapped instance's changes so the UI binds to this object only.
    /// Dispose detaches from the instance.
    /// </summary>
    public sealed class InstanceCardViewModel : ObservableBase, IDisposable
    {
        private static readonly HashSet<string> Forwarded = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "AccountName", "Status", "StatusKind", "MemoryText", "Uptime", "IsActive"
        };

        private readonly RobloxInstance _instance;
        private bool _disposed;

        public InstanceCardViewModel(RobloxInstance instance, IPlatform platform)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            _instance = instance;
            CanCapMemory = FeatureState.From(platform, PlatformCapabilities.MemoryCap, "Memory cap");
            CanThrottle = FeatureState.From(platform, PlatformCapabilities.BackgroundPriority, "Background priority");
            _instance.PropertyChanged += OnInstanceChanged;
        }

        public RobloxInstance Instance { get { return _instance; } }

        public string Name { get { return _instance.Name; } }
        public string AccountName { get { return _instance.AccountName; } }
        public string Status { get { return _instance.Status; } }
        public string StatusKind { get { return _instance.StatusKind; } }
        public string MemoryText { get { return _instance.MemoryText; } }

        /// <summary>Already formatted by InstanceStatus.UptimeText when the instance is sampled.</summary>
        public string Uptime { get { return _instance.Uptime; } }

        public bool IsActive { get { return _instance.IsActive; } }

        public FeatureState CanCapMemory { get; }
        public FeatureState CanThrottle { get; }

        private void OnInstanceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (e.PropertyName == null)
            {
                foreach (string name in Forwarded) Raise(name);
            }
            else if (Forwarded.Contains(e.PropertyName))
            {
                Raise(e.PropertyName);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _instance.PropertyChanged -= OnInstanceChanged;
        }
    }
}
