using System;

namespace CookieJar.AppModel
{
    public enum TimedWarningState
    {
        Pending,
        Accepted,
        Declined,
        TimedOut
    }

    /// <summary>
    /// Countdown behind a resource warning. Port of TimedWarnDialog: one tick per second, and running out of
    /// time counts as Yes so a profile launch keeps its stagger. The UI owns the timer and calls Tick.
    /// </summary>
    public sealed class TimedWarning
    {
        public const int DefaultSeconds = 6;

        private int _remaining;

        public TimedWarning(int seconds)
        {
            // Same clamp as the dialog: a zero or negative countdown still shows for one second.
            _remaining = Math.Max(1, seconds);
            State = TimedWarningState.Pending;
        }

        public int RemainingSeconds
        {
            get { return _remaining; }
        }

        public TimedWarningState State { get; private set; }

        /// <summary>False only when the user declined; accepting and timing out both continue.</summary>
        public bool Result
        {
            get { return State != TimedWarningState.Declined; }
        }

        /// <summary>Counts one second down. Reaching zero while still pending times the warning out.</summary>
        public void Tick()
        {
            if (State != TimedWarningState.Pending) return;
            _remaining--;
            if (_remaining <= 0)
            {
                _remaining = 0;
                State = TimedWarningState.TimedOut;
            }
        }

        public void Accept()
        {
            if (State == TimedWarningState.Pending) State = TimedWarningState.Accepted;
        }

        public void Decline()
        {
            if (State == TimedWarningState.Pending) State = TimedWarningState.Declined;
        }
    }
}
