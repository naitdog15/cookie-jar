using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>Main window state: instances, accounts, the RAM slider and platform feature gates.</summary>
    public sealed class MainViewModel : ObservableBase
    {
        private readonly List<InstanceCardViewModel> _watched = new List<InstanceCardViewModel>();
        private Restrictor _restrictor;
        private int _ramIndex;

        public MainViewModel(IPlatform platform)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            Instances = new ObservableCollection<InstanceCardViewModel>();
            Accounts = new ObservableCollection<AccountRowViewModel>();
            Instances.CollectionChanged += OnInstancesChanged;

            MemoryCap = FeatureState.From(platform, PlatformCapabilities.MemoryCap, "Memory cap");
            BackgroundPriority = FeatureState.From(platform, PlatformCapabilities.BackgroundPriority, "Background priority");
            WindowPlacement = FeatureState.From(platform, PlatformCapabilities.WindowPlacement, "Window placement");
            InputInjection = FeatureState.From(platform, PlatformCapabilities.InputInjection, "Input injection");
            StartAtLogin = FeatureState.From(platform, PlatformCapabilities.StartAtLogin, "Start at login");

            if (platform.Processes != null && platform.Windows != null)
            {
                Efficiency = new BackgroundEfficiency(platform.Processes);
                Efficiency.ForegroundPid = BackgroundEfficiency.ForegroundFrom(platform);
                Focus = new FocusMode(platform.Windows, Efficiency);
            }
        }

        /// <summary>The background-priority writer (also used by Focus). Null when the platform has no process or window service.</summary>
        public BackgroundEfficiency Efficiency { get; }

        /// <summary>Shared Focus mode. Null when the platform has no process or window service.</summary>
        public FocusMode Focus { get; }

        public ObservableCollection<InstanceCardViewModel> Instances { get; }
        public ObservableCollection<AccountRowViewModel> Accounts { get; }

        public FeatureState MemoryCap { get; }
        public FeatureState BackgroundPriority { get; }
        public FeatureState WindowPlacement { get; }
        public FeatureState InputInjection { get; }
        public FeatureState StartAtLogin { get; }

        /// <summary>Instances with a status other than Stopped, as InstanceStatus counts them.</summary>
        public int ActiveCount
        {
            get { return InstanceStatus.CountActive(Instances.Select(c => c.Instance)); }
        }

        public string StatLine { get { return StatusText(); } }
        public string StatusLine { get { return StatusText(); } }

        public void SetRestrictor(Restrictor restrictor)
        {
            _restrictor = restrictor;
            RaiseCountChanged();
        }

        /// <summary>0 is Auto (no cap); 1..RamScale.Steps are the capped positions.</summary>
        public int RamIndex
        {
            get { return _ramIndex; }
            set
            {
                int index = Math.Max(0, Math.Min(RamScale.Steps, value));
                if (Set(ref _ramIndex, index, nameof(RamIndex)))
                {
                    Raise(nameof(RamMb));
                    Raise(nameof(RamLabel));
                }
            }
        }

        public int RamMb
        {
            get { return RamScale.MbFromIndex(_ramIndex); }
            set { RamIndex = RamScale.IndexFromMb(value); }
        }

        public string RamLabel { get { return RamScale.Label(RamMb); } }

        private string StatusText()
        {
            return _restrictor == null ? string.Empty : _restrictor.StatusLine(ActiveCount);
        }

        private void RaiseCountChanged()
        {
            Raise(nameof(ActiveCount));
            Raise(nameof(StatLine));
            Raise(nameof(StatusLine));
        }

        private void OnInstancesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Re-subscribe to the whole list: a Reset carries no old items to unhook.
            foreach (InstanceCardViewModel card in _watched) card.PropertyChanged -= OnCardChanged;
            _watched.Clear();
            foreach (InstanceCardViewModel card in Instances)
            {
                card.AttachFocus(Focus, AllInstances);
                card.PropertyChanged += OnCardChanged;
                _watched.Add(card);
            }
            RaiseCountChanged();
        }

        private IList<RobloxInstance> AllInstances()
        {
            return Instances.Select(c => c.Instance).ToList();
        }

        private void OnCardChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null || e.PropertyName == nameof(InstanceCardViewModel.IsActive))
            {
                RaiseCountChanged();
            }
        }
    }
}
