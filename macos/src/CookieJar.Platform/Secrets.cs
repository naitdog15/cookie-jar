namespace CookieJar.Platform
{
    /// <summary>
    /// Stores secrets (cookies, tokens) under string keys, protected by the OS where SecureStorage is set.
    /// Thread-safe. Get returns null for a missing key. Failures return false/null, never throw.
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>Stores or replaces the value. Returns false on failure.</summary>
        bool Set(string key, string value);
        /// <summary>Returns the value or null if missing/unreadable.</summary>
        string Get(string key);
        /// <summary>Removes the key. Returns true if it existed and was removed.</summary>
        bool Delete(string key);
    }
}
