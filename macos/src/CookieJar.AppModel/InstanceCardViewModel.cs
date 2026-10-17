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

        private FocusMode _focus;
        private Func<IList<RobloxInstance>> _all;

        private readonly RobloxInstance _instance;
        private bool _disposed;

        public InstanceCardViewModel(RobloxInstance instance, IPlatform platform)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            _instance = instance;
            CanCapMemory = FeatureState.From(platform, PlatformCapabilities.MemoryCap, "Memory cap");
            CanThrottle = FeatureState.From(platform, PlatformCapabilities.BackgroundPriority, "Background priority");
            FocusFeature = platform.Supports(PlatformCapabilities.WindowPlacement)
                ? FeatureState.From(platform, PlatformCapabilities.BackgroundPriority, "Focus (needs background priority)")
                : FeatureState.From(platform, PlatformCapabilities.WindowPlacement, "Focus (needs window placement)");
            FocusCommand = new RelayCommand(ToggleFocus, delegate { return FocusFeature.Enabled && _focus != null; });
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

        /// <summary>Focus needs window placement and background priority; disabled with a reason otherwise.</summary>
        public FeatureState FocusFeature { get; }

        public RelayCommand FocusCommand { get; }

        /// <summary>True while this client is the one in Focus mode (fullscreen, the others slowed).</summary>
        public bool IsFocused { get { return _focus != null && _focus.IsFocused(_instance.Id); } }

        public string FocusLabel { get { return IsFocused ? "Unfocus" : "Focus"; } }

        /// <summary>Connects the shared Focus mode. all lists every instance (the others are slowed while this one is focused).</summary>
        public void AttachFocus(FocusMode focus, Func<IList<RobloxInstance>> all)
        {
            if (_focus != null) _focus.Changed -= OnFocusChanged;
            _focus = focus;
            _all = all;
            if (_focus != null) _focus.Changed += OnFocusChanged;
            OnFocusChanged();
        }

        private IList<RobloxInstance> AllInstances()
        {
            return _all != null ? _all() : new List<RobloxInstance> { _instance };
        }

        private void ToggleFocus()
        {
            if (_focus == null) return;
            if (_focus.IsFocused(_instance.Id)) _focus.Stop(AllInstances());
            else _focus.Start(_instance, AllInstances());
        }

        private void OnFocusChanged()
        {
            Raise(nameof(IsFocused));
            Raise(nameof(FocusLabel));
            FocusCommand.RaiseCanExecuteChanged();
        }

        private void OnInstanceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            // The focused client stopping ends Focus, so the others are not left slowed.
            if ((e.PropertyName == null || e.PropertyName == "StatusKind" || e.PropertyName == "Status")
                && _instance.StatusKind == "Stopped" && _focus != null && _focus.IsFocused(_instance.Id))
            {
                _focus.Stop(AllInstances());
            }
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
            if (_focus != null) _focus.Changed -= OnFocusChanged;
            _instance.PropertyChanged -= OnInstanceChanged;
        }
    }
}
