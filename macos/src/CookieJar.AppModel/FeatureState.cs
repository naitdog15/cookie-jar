using System;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>Whether a control is shown and usable, and why it is disabled. Immutable.</summary>
    public sealed class FeatureState
    {
        public FeatureState(bool visible, bool enabled, string reason)
        {
            Visible = visible;
            Enabled = enabled;
            Reason = reason ?? string.Empty;
        }

        public bool Visible { get; }
        public bool Enabled { get; }
        public string Reason { get; }

        /// <summary>Enabled when the platform supports the flag; otherwise shown but disabled with a reason.</summary>
        public static FeatureState From(IPlatform platform, PlatformCapabilities flag, string label)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            if (platform.Supports(flag))
            {
                return new FeatureState(true, true, string.Empty);
            }
            return new FeatureState(true, false, label + " is not supported on " + platform.Name + ".");
        }
    }
}
