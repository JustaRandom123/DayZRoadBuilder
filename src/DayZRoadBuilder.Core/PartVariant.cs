using System;

namespace DayZRoadBuilder.Core
{
    /// <summary>
    /// A road part in a specific orientation, precomputed in "driving coordinates":
    /// the entry is at the origin, driving direction = +Z. A curve placed backwards gives the opposite curve
    /// (this is also how the Terrain Builder road tool does it).
    /// </summary>
    public sealed class PartVariant
    {
        public RoadPart Part { get; private set; }
        public bool Reversed { get; private set; }
        /// <summary>Object yaw = current heading + YawOffset.</summary>
        public double YawOffset { get; private set; }
        /// <summary>Exit point relative to the entry.</summary>
        public Vec2 Exit { get; private set; }
        /// <summary>Heading change when driving through (+ = right).</summary>
        public double HeadingDelta { get; private set; }
        /// <summary>Object reference point (the exported position) relative to the entry.</summary>
        public Vec2 Center { get; private set; }
        /// <summary>Reference point in model coordinates (bounding box centre or origin).</summary>
        public Vec2 ModelReference { get; private set; }
        /// <summary>Centreline sample points (without entry, including exit).</summary>
        public Vec2[] Samples { get; private set; }
        public double SampleStep { get; private set; }
        public double Length { get { return Part.Length; } }
        public double AbsTurn { get { return Math.Abs(Part.TurnAngle); } }

        public static PartVariant Create(RoadPart part, bool reversed, bool useBoundingCenter, double sampleStep)
        {
            var v = new PartVariant();
            v.Part = part;
            v.Reversed = reversed;
            v.ModelReference = useBoundingCenter ? part.BoundingCenter : Vec2.Zero;

            int n = Math.Max(2, (int)Math.Ceiling(part.Length / Math.Max(0.1, sampleStep)));
            v.SampleStep = part.Length / n;
            var local = new Vec2[n + 1];
            for (int i = 0; i <= n; i++)
                local[i] = part.CenterlineLocal((double)i / n);

            double rot;
            Vec2 origin;
            Vec2 exitLocal;
            v.Samples = new Vec2[n];
            if (!reversed)
            {
                rot = -part.StartHeading;
                origin = part.StartCenter;
                exitLocal = part.EndCenter;
                v.HeadingDelta = part.TurnAngle;
                for (int i = 1; i <= n; i++)
                    v.Samples[i - 1] = Geo.Rotate(local[i] - origin, rot);
            }
            else
            {
                rot = -(part.EndHeading + 180.0);
                origin = part.EndCenter;
                exitLocal = part.StartCenter;
                v.HeadingDelta = -part.TurnAngle;
                for (int i = n - 1; i >= 0; i--)
                    v.Samples[n - 1 - i] = Geo.Rotate(local[i] - origin, rot);
            }

            v.YawOffset = rot;
            v.Exit = Geo.Rotate(exitLocal - origin, rot);
            v.Center = Geo.Rotate(v.ModelReference - origin, rot);
            return v;
        }

        public override string ToString()
        {
            return Part.Name + (Reversed ? " (reversed)" : "");
        }
    }
}
