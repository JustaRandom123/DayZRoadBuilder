using System;
using System.Collections.Generic;

namespace DayZRoadBuilder.Core
{
    /// <summary>Parameter der automatischen Teile-Auswahl.</summary>
    public sealed class BuildSettings
    {
        /// <summary>Anzahl der Kandidaten, die pro Stationierungs-Schritt weiterverfolgt werden. Höher = genauer, langsamer.</summary>
        public int BeamWidth { get; set; } = 12;
        /// <summary>Raster der Stationierung in Metern.</summary>
        public double BinSize { get; set; } = 0.5;
        /// <summary>Kosten pro verbautem Teil (größer = lieber lange Teile, etwas mehr Abweichung).</summary>
        public double PiecePenalty { get; set; } = 1.0;
        /// <summary>Kosten pro Grad Richtungsänderung (verhindert unnötiges Hin- und Herschlängeln).</summary>
        public double TurnPenalty { get; set; } = 0.05;
        /// <summary>Gewicht des Richtungsfehlers am Ende jedes Teils (pro Grad²).</summary>
        public double HeadingWeight { get; set; } = 0.02;
        /// <summary>Gewicht des Abstands zum Linienende (pro m²).</summary>
        public double EndWeight { get; set; } = 4.0;
        /// <summary>Maximaler Abstand zum Linienende, ab dem die Straße als fertig gilt.</summary>
        public double EndTolerance { get; set; } = 3.5;
        /// <summary>Abstand der Prüfpunkte entlang eines Teils.</summary>
        public double SampleStep { get; set; } = 1.0;
        /// <summary>true = Objektposition ist die Bounding-Box-Mitte (Autocenter), false = Modellursprung.</summary>
        public bool UseBoundingCenter { get; set; } = true;
        /// <summary>Zusätzlich getestete Startrichtungen ± dieser Gradzahl (0 = genau Linienrichtung).</summary>
        public double StartHeadingRange { get; set; } = 0.0;
        public double StartHeadingStep { get; set; } = 0.5;
        /// <summary>Endstücke umgedreht einbauen.</summary>
        public bool FlipEndCaps { get; set; }
        /// <summary>Endstück am Anfang setzen (nur wenn ein Endstück-Teil gewählt ist).</summary>
        public bool EndCapAtStart { get; set; } = true;
        /// <summary>Endstück am Ende setzen (nur wenn ein Endstück-Teil gewählt ist).</summary>
        public bool EndCapAtEnd { get; set; } = true;
    }

    /// <summary>Ein platziertes Objekt.</summary>
    public sealed class PlacedPart
    {
        public RoadPart Part;
        public bool Reversed;
        /// <summary>Weltposition des Referenzpunkts (das, was exportiert wird).</summary>
        public Vec2 Position;
        /// <summary>Objekt-Yaw (Kompassgrad, im Uhrzeigersinn).</summary>
        public double Yaw;
        /// <summary>Referenzpunkt im Modell (BBox-Mitte oder 0,0).</summary>
        public Vec2 ModelReference;
        public Vec2 Entry;
        public Vec2 Exit;
        public double EntryHeading;
        public double ExitHeading;
        public int RoadIndex;

        /// <summary>Wandelt einen Modellpunkt in Weltkoordinaten um.</summary>
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
        /// <summary>Größte seitliche Abweichung der Straßenmitte von der Linie.</summary>
        public double MaxDeviation;
        public double RmsDeviation;
        /// <summary>Abstand zwischen Straßenende und Linienende.</summary>
        public double EndGap;
        public double RoadLength;
        /// <summary>Größte Fuge zwischen zwei Teilen (Kontrollwert, sollte ~0 sein).</summary>
        public double MaxJointGap;
        public TimeSpan Duration;
    }
}
