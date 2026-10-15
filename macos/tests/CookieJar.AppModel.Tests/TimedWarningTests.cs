using CookieJar.AppModel;

namespace CookieJar.Tests
{
    public static class TimedWarningTests
    {
        public static void Run()
        {
            T.Check("DefaultSeconds is 6 like TimedWarnDialog", TimedWarning.DefaultSeconds == 6, "");

            TimedWarning w = new TimedWarning(6);
            T.Check("starts pending with the full countdown", w.State == TimedWarningState.Pending && w.RemainingSeconds == 6, "remaining=" + w.RemainingSeconds);
            for (int i = 0; i < 5; i++) w.Tick();
            T.Check("5 ticks keep it pending", w.State == TimedWarningState.Pending, "state=" + w.State);
            T.Check("5 ticks leave 1 second", w.RemainingSeconds == 1, "remaining=" + w.RemainingSeconds);
            w.Tick();
            T.Check("6th tick times out", w.State == TimedWarningState.TimedOut, "state=" + w.State);
            T.Check("timeout counts as Yes", w.Result, "");
            T.Check("remaining stays at 0 after timeout", w.RemainingSeconds == 0, "remaining=" + w.RemainingSeconds);
            w.Tick();
            T.Check("tick after timeout changes nothing", w.State == TimedWarningState.TimedOut && w.RemainingSeconds == 0, "");

            TimedWarning declined = new TimedWarning(6);
            declined.Decline();
            T.Check("Decline sets Declined", declined.State == TimedWarningState.Declined, "state=" + declined.State);
            T.Check("Decline gives Result false", !declined.Result, "");
            declined.Accept();
            T.Check("Accept after Decline is ignored", declined.State == TimedWarningState.Declined && !declined.Result, "state=" + declined.State);
            declined.Tick();
            T.Check("Tick after Decline does not count", declined.RemainingSeconds == 6, "remaining=" + declined.RemainingSeconds);

            TimedWarning accepted = new TimedWarning(6);
            accepted.Accept();
            T.Check("Accept sets Accepted with Result true", accepted.State == TimedWarningState.Accepted && accepted.Result, "");
            accepted.Decline();
            T.Check("Decline after Accept is ignored", accepted.State == TimedWarningState.Accepted, "state=" + accepted.State);

            TimedWarning zero = new TimedWarning(0);
            T.Check("TimedWarning(0) shows one second", zero.RemainingSeconds == 1, "remaining=" + zero.RemainingSeconds);
            zero.Tick();
            T.Check("TimedWarning(0) times out after one tick", zero.State == TimedWarningState.TimedOut && zero.Result, "state=" + zero.State);

            TimedWarning negative = new TimedWarning(-5);
            T.Check("negative seconds clamp to 1", negative.RemainingSeconds == 1, "remaining=" + negative.RemainingSeconds);
        }
    }
}
