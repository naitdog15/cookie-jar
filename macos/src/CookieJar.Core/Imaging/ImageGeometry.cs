using System;
using CookieJar.Platform;

namespace CookieJar.Core.Imaging
{
    /// <summary>A point in capture pixels, not desktop coordinates.</summary>
    public struct ImagePoint
    {
        public ImagePoint(int x, int y) { X = x; Y = y; }
        public int X { get; }
        public int Y { get; }
    }

    /// <summary>A rectangle in capture pixels. Right and Bottom are exclusive.</summary>
    public struct ImageRect : IEquatable<ImageRect>
    {
        public ImageRect(int x, int y, int width, int height)
        { X = x; Y = y; Width = width; Height = height; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public static ImageRect Empty => default(ImageRect);
        public static ImageRect FromLTRB(int left, int top, int right, int bottom)
            => new ImageRect(left, top, right - left, bottom - top);
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        public bool Contains(ImagePoint p) => Contains(p.X, p.Y);
        public bool IntersectsWith(ImageRect other)
            => !IsEmpty && !other.IsEmpty && Left < other.Right && other.Left < Right
                && Top < other.Bottom && other.Top < Bottom;
        public void Intersect(ImageRect other)
        {
            int left = Math.Max(Left, other.Left), top = Math.Max(Top, other.Top);
            int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
            this = right >= left && bottom >= top ? FromLTRB(left, top, right, bottom) : Empty;
        }
        public void Inflate(int x, int y) { X -= x; Y -= y; Width += 2 * x; Height += 2 * y; }
        public static ImageRect Union(ImageRect a, ImageRect b)
            => FromLTRB(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
        public ScreenRect ToScreenRect() => new ScreenRect(X, Y, Width, Height);
        public bool Equals(ImageRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        public override bool Equals(object obj) => obj is ImageRect other && Equals(other);
        public override int GetHashCode() => ((X * 397 ^ Y) * 397 ^ Width) * 397 ^ Height;
        public override string ToString() => X + "," + Y + "," + Width + "," + Height;
    }
}
