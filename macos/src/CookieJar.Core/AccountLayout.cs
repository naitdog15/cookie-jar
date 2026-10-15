namespace CookieJar.Core
{
    /// <summary>A window rectangle on a specific monitor, used as a manual tiling override.</summary>
    public class AccountLayout
    {
        public int MonitorIndex;
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public AccountLayout Clone()
        {
            AccountLayout l = new AccountLayout();
            l.MonitorIndex = MonitorIndex;
            l.X = X;
            l.Y = Y;
            l.Width = Width;
            l.Height = Height;
            return l;
        }
    }
}
