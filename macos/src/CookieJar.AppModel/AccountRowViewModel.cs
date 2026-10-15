using System;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>One account in the account list.</summary>
    public sealed class AccountRowViewModel : ObservableBase
    {
        private bool _isSelected;
        private bool _isRunning;

        public AccountRowViewModel(string accountId, string displayName, IPlatform platform)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            AccountId = accountId;
            DisplayName = displayName;
            Placement = FeatureState.From(platform, PlatformCapabilities.WindowPlacement, "Window placement");
        }

        public string AccountId { get; }
        public string DisplayName { get; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            set { Set(ref _isRunning, value, nameof(IsRunning)); }
        }

        public FeatureState Placement { get; }
    }
}
