using System;
using System.Collections.Generic;

namespace DayZRoadBuilder.Core
{
    /// <summary>Parameters of the automatic part selection.</summary>
    public sealed class BuildSettings
    {
        /// <summary>Number of candidates kept per chainage step. Higher = more accurate, slower.</summary>
        public int BeamWidth { get; set; } = 12;
        /// <summary>Chainage bin size in metres.</summary>
        public double BinSize { get; set; } = 0.5;
        /// <summary>Cost per placed part (higher = prefer long parts, slightly more deviation).</summary>
        public double PiecePenalty { get; set; } = 1.0;
        /// <summary>Cost per degree of heading change (prevents needless wiggling).</summary>
        public double TurnPenalty { get; set; } = 0.05;
        /// <summary>Weight of the heading error at the end of each part (per degree²).</summary>
        public double HeadingWeight { get; set; } = 0.02;
        /// <summary>Weight of the distance to the end of the line (per m²).</summary>
        public double EndWeight { get; set; } = 4.0;
        /// <summary>Maximum distance to the end of the line at which the road counts as finished.</summary>
        public double EndTolerance { get; set; } = 3.5;
        /// <summary>Spacing of the sample points along a part.</summary>
        public double SampleStep { get; set; } = 1.0;
        /// <summary>true = object position is the bounding box centre (autocenter), false = model origin.</summary>
        public bool UseBoundingCenter { get; set; } = true;
        /// <summary>Additional start headings tested within ± this many degrees (0 = exactly the line direction).</summary>
        public double StartHeadingRange { get; set; } = 0.0;
        public double StartHeadingStep { get; set; } = 0.5;
        /// <summary>Place end pieces reversed.</summary>
        public bool FlipEndCaps { get; set; }
        /// <summary>Place an end piece at the start (only if an end piece is selected).</summary>
        public bool EndCapAtStart { get; set; } = true;
        /// <summary>Place an end piece at the end (only if an end piece is selected).</summary>
        public bool EndCapAtEnd { get; set; } = true;
    }

    /// <summary>A placed object.</summary>
    public sealed class PlacedPart
    {
        public RoadPart Part;
        public bool Reversed;
        /// <summary>World position of the reference point (this is what gets exported).</summary>
        public Vec2 Position;
        /// <summary>Object yaw (compass degrees, clockwise).</summary>
        public double Yaw;
        /// <summary>Reference point in the model (bounding box centre or 0,0).</summary>
        public Vec2 ModelReference;
        public Vec2 Entry;
        public Vec2 Exit;
        public double EntryHeading;
        public double ExitHeading;
        public int RoadIndex;

        /// <summary>Converts a model point to world coordinates.</summary>
        public Vec2 ToWorld(Vec2 local)
        {
            return Position + Geo.Rotate(local - ModelReference, Yaw);
        }

        public Vec2[] GetWorldOutline(int segments)
        {
            Vec2[] pts = Part.GetLocalOutline(segments);
            for (int i = 0; i < pts.Length; i++) pts[i] = ToWorld(pts[i]);
            return pts;
        }
    }

    public sealed class BuildResult
    {
        public RoadPath Path;
        public List<PlacedPart> Parts = new List<PlacedPart>();
        public List<string> Warnings = new List<string>();
        public bool ReachedEnd;
        /// <summary>Largest lateral deviation of the road centreline from the line.</summary>
        public double MaxDeviation;
        public double RmsDeviation;
        /// <summary>Distance between the end of the road and the end of the line.</summary>
        public double EndGap;
        public double RoadLength;
        /// <summary>Largest gap between two parts (sanity check, should be ~0).</summary>
        public double MaxJointGap;
        public TimeSpan Duration;
    }
}
