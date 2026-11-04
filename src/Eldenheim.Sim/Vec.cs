using System;

namespace Eldenheim.Sim
{
    /// <summary>
    /// glam's Vec3 as the sandbox uses it: f32, same operation order, so a
    /// replay of the sandbox's inputs lands on the same numbers. Sim space is
    /// the sandbox's: +Y up, yaw 0 faces +Z, and +X is the character's left
    /// when facing +Z.
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 UnitY = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 UnitZ = new Vec3(0f, 0f, 1f);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(s * a.X, s * a.Y, s * a.Z);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);
        public static bool operator ==(Vec3 a, Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(Vec3 a, Vec3 b) => !(a == b);

        public float Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public float LengthSquared() => Dot(this);
        public float Length() => F.Sqrt(Dot(this));
        public float Distance(Vec3 b) => (this - b).Length();

        public Vec3 Normalize() => this * (1f / Length());

        public Vec3 NormalizeOrZero()
        {
            float rcp = 1f / Length();
            return !float.IsInfinity(rcp) && !float.IsNaN(rcp) && rcp > 0f ? this * rcp : Zero;
        }

        public Vec3 ClampLengthMax(float max)
        {
            float lengthSq = LengthSquared();
            return lengthSq > max * max ? (this / F.Sqrt(lengthSq)) * max : this;
        }

        public bool Equals(Vec3 other) => this == other;
        public override bool Equals(object obj) => obj is Vec3 v && v == this;
        public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode() * 31 ^ Z.GetHashCode() * 17;
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    public struct Vec2
    {
        public float X, Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float Length() => F.Sqrt(X * X + Y * Y);
    }

    /// <summary>f32 maths as Rust does it (to_radians, rem_euclid, the libm calls).</summary>
    public static class F
    {
        public const float PI = 3.14159265358979323846264338327950288f;
        public const float TAU = 6.28318530717958647692528676655900577f;
        public const float FRAC_PI_2 = 1.57079632679489661923132169163975144f;
        public const float FRAC_PI_4 = 0.785398163397448309615660845819875721f;
        private const float RadsPerDeg = PI / 180f;

        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float ToRadians(float deg) => deg * RadsPerDeg;
        public static float Abs(float x) => Math.Abs(x);
        public static float Min(float a, float b) => a < b ? a : (b < a ? b : (float.IsNaN(a) ? b : a));
        public static float Max(float a, float b) => a > b ? a : (b > a ? b : (float.IsNaN(a) ? b : a));
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Floor(float x) => (float)Math.Floor(x);

        public static float RemEuclid(float a, float b)
        {
            float r = a % b;
            return r < 0f ? r + Math.Abs(b) : r;
        }

        /// <summary>f32::fract: x - trunc(x).</summary>
        public static float Fract(float x) => x - (float)Math.Truncate(x);
    }
}
