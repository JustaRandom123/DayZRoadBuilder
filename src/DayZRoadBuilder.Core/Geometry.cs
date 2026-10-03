using System;
using System.Globalization;

namespace DayZRoadBuilder.Core
{
    /// <summary>
    /// 2D-Vektor in Karten-/Modellkoordinaten.
    /// X = Ost (Modell-X), Z = Nord (Modell-Z). Die Höhe (Y) spielt für den Straßenbau keine Rolle.
    /// </summary>
    public readonly struct Vec2
    {
        public readonly double X;
        public readonly double Z;

        public Vec2(double x, double z)
        {
            X = x;
            Z = z;
        }

        public static readonly Vec2 Zero = new Vec2(0, 0);

        public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.X + b.X, a.Z + b.Z); }
        public static Vec2 operator -(Vec2 a, Vec2 b) { return new Vec2(a.X - b.X, a.Z - b.Z); }
        public static Vec2 operator *(Vec2 a, double f) { return new Vec2(a.X * f, a.Z * f); }

        public double Length { get { return Math.Sqrt(X * X + Z * Z); } }
        public double LengthSquared { get { return X * X + Z * Z; } }

        public static double Distance(Vec2 a, Vec2 b) { return (a - b).Length; }

        public static Vec2 Lerp(Vec2 a, Vec2 b, double t) { return new Vec2(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t); }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F3}; {1:F3})", X, Z);
        }
    }

    /// <summary>
    /// Winkelhilfen. Alle Winkel in Grad als Kompasskurs: 0° = Nord (+Z), 90° = Ost (+X), im Uhrzeigersinn.
    /// Das entspricht der Richtungs-/Yaw-Konvention von Arma/DayZ-Terrain-Builder.
    /// </summary>
    public static class Geo
    {
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        /// <summary>Dreht einen Vektor um <paramref name="deg"/> Grad im Uhrzeigersinn (Kompasskonvention).</summary>
        public static Vec2 Rotate(Vec2 v, double deg)
        {
            double a = deg * Deg2Rad;
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            return new Vec2(v.X * c + v.Z * s, -v.X * s + v.Z * c);
        }

        /// <summary>Kompasskurs eines Richtungsvektors.</summary>
        public static double Bearing(Vec2 d)
        {
            return Math.Atan2(d.X, d.Z) * Rad2Deg;
        }

        /// <summary>Einheitsvektor für einen Kompasskurs.</summary>
        public static Vec2 Direction(double bearingDeg)
        {
            double a = bearingDeg * Deg2Rad;
            return new Vec2(Math.Sin(a), Math.Cos(a));
        }

        /// <summary>Normalisiert auf (-180, 180].</summary>
        public static double WrapDeg(double a)
        {
            a %= 360.0;
            if (a > 180.0) a -= 360.0;
            else if (a <= -180.0) a += 360.0;
            return a;
        }

        /// <summary>Normalisiert auf [0, 360).</summary>
        public static double Norm360(double a)
        {
            a %= 360.0;
            if (a < 0) a += 360.0;
            if (a >= 360.0) a -= 360.0;
            return a;
        }

        /// <summary>Punkt-in-Polygon-Test (Even-Odd).</summary>
        public static bool PointInPolygon(Vec2[] poly, Vec2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vec2 a = poly[i];
                Vec2 b = poly[j];
                if ((a.Z > p.Z) != (b.Z > p.Z))
                {
                    double x = (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X;
                    if (p.X < x) inside = !inside;
                }
            }
            return inside;
        }
    }
}
