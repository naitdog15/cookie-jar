namespace CookieJar.Platform
{
    public enum MouseButton { Left = 0, Right = 1, Middle = 2 }

    /// <summary>Whether an OS-gated feature (input injection, screen capture) is allowed.</summary>
    public enum PermissionState
    {
        /// <summary>No permission needed or permission granted.</summary>
        Granted,
        /// <summary>Permission required and not granted (macOS Accessibility / Screen Recording).</summary>
        Denied,
        /// <summary>Cannot tell.</summary>
        Unknown,
        /// <summary>Feature not available at all on this platform.</summary>
        Unsupported
    }

    /// <summary>
    /// Synthetic mouse/keyboard input in screen pixels (same space as IWindowService). Thread-safe by internal lock.
    /// Without InputInjection every action returns false. Key codes are platform-neutral virtual keys
    /// (ASCII uppercase letters/digits, 0x20 space, 0x0D enter, 0x1B escape); implementations translate.
    /// </summary>
    public interface IInputService
    {
        /// <summary>Accessibility / injection permission.</summary>
        PermissionState GetPermissionState();
        /// <summary>Asks the OS to prompt or opens the settings pane. Returns the state after the request.</summary>
        PermissionState RequestPermission();

        bool TryGetCursorPosition(out int x, out int y);
        bool MoveCursor(int x, int y);
        /// <summary>Presses and releases at the current cursor position, holding for holdMs.</summary>
        bool Click(MouseButton button, int holdMs);
        bool ButtonDown(MouseButton button);
        bool ButtonUp(MouseButton button);
        /// <summary>Wheel notches; positive scrolls up.</summary>
        bool Wheel(int notches);
        bool KeyDown(int virtualKey);
        bool KeyUp(int virtualKey);

        /// <summary>
        /// Focus-and-restore click: remembers the foreground window and cursor, brings <paramref name="windowId"/> to the front
        /// (restoring if minimised, waiting up to restoreWaitMs), moves to (x, y) in screen pixels, clicks, then puts the
        /// cursor, foreground window and minimised state back. False if the window could not be focused or the click failed.
        /// </summary>
        bool ClickInWindow(long windowId, int x, int y, MouseButton button, int holdMs, int restoreWaitMs);

        /// <summary>Same focus-and-restore semantics for tapping a key in the window.</summary>
        bool TapKeyInWindow(long windowId, int virtualKey, int holdMs, int restoreWaitMs);
    }
}
