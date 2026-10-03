# DayZ Road Builder

Baut DayZ-Straßen automatisch entlang einer Polyline (Shapefile) aus den originalen Straßenteilen
(`asf1_25`, `asf1_10 100`, `city_6`, `mud_60 10`, …) – lückenlos aneinandergesetzt wie mit dem
Road-Tool im Terrain Builder, nur ohne Handarbeit. Ergebnis ist eine Objektliste, die direkt in den
Terrain Builder importiert werden kann.

## Öffnen & Kompilieren

1. `DayZRoadBuilder.sln` in **Visual Studio Community 2022 (oder neuer)** öffnen.
   Benötigt wird der Workload **„.NET-Desktopentwicklung“** (enthält das .NET-8-SDK).
2. `DayZRoadBuilder.App` ist das Startprojekt → **F5** bzw. *Erstellen → Projektmappe erstellen*.
3. Die fertige EXE liegt unter `src\DayZRoadBuilder.App\bin\Debug\net8.0-windows\DayZRoadBuilder.exe`.

| Projekt | Inhalt |
|---|---|
| `DayZRoadBuilder.Core` | P3D-Leser (MLOD), SHP-Leser, Teilebibliothek, Bau-Algorithmus, TB-Export |
| `DayZRoadBuilder.App`  | Windows-Oberfläche mit Vorschau (WinForms) |
| `DayZRoadBuilder.Cli`  | Kommandozeilenversion für Batch-Läufe |

## Benutzung

1. **Teile-Ordner** wählen (z.B. `Road Parts` oder `P:\DZ\structures\roads\parts`). Unterordner werden mitgelesen.
   Die P3Ds müssen **unbinarisiert (MLOD)** sein – so wie sie auf P:\ liegen. Binarisierte Dateien (ODOL, z.B. die Sidewalks) werden übersprungen.
2. **Polyline (.shp)** wählen. Unterstützt: PolyLine, PolyLineZ/M, Polygon; mehrere Datensätze/Linien → mehrere Straßen.
3. **Straßentyp** wählen (asf1, asf2, asf3, city, grav, mud, asf1enoch, mudsakhal, …) und ggf. einzelne Teile abhaken
   (z.B. `60 10` bei Hauptstraßen, wenn keine so engen Kurven gewollt sind). Zebrastreifen sind standardmäßig aus.
4. Optional ein **Endstück** (`*_6konec`) wählen, das am Anfang/Ende gesetzt wird.
5. **Straße bauen** → Vorschau prüfen (Mausrad = Zoom, ziehen = verschieben, Doppelklick = alles zeigen,
   Mauszeiger über einem Teil zeigt Name/Position/Yaw).
6. **Export für Terrain Builder** → `.txt` speichern und im Terrain Builder importieren
   (Objekt-Layer → *Import objects…* / *File → Import → Objects*).

Exportformat pro Zeile (Terrain-Builder-Standard):

```
"asf1_25";204774.083;7865.479;48.2331;0.0000;0.0000;1.0000;0.000;
 Name     X          Y        Yaw     Pitch  Roll   Scale  Z (relativ)
```

Koordinaten werden 1:1 aus der Shapefile übernommen. Eine aus dem Terrain Builder exportierte Linie enthält
bereits den 200000-Versatz – dann bleibt *Versatz X* auf 0.

## Erster Test im Terrain Builder (wichtig!)

Zwei Konventionen kann ich ohne deinen Terrain Builder nicht zu 100 % verifizieren; beide sind einstellbar:

* **Position = Bounding-Box-Mitte** (Standard). DayZ-Straßenteile haben kein `autocenter=0`, also benutzt die
  Engine die Mitte der Bounding Box als Objektposition. Sitzen die Teile nach dem Import versetzt
  (Kurventeile um ~1 m seitlich, Geraden um die halbe Länge nach vorn/hinten), auf *Modell-Ursprung* umstellen.
* **Yaw im Uhrzeigersinn** (wie `getDir` in Arma/DayZ, 0° = Nord). Sind die Kurven nach dem Import gespiegelt
  bzw. die Straße falsch gedreht, *Yaw umkehren* anhaken.

Am besten eine kurze Teststrecke mit einer Kurve exportieren, importieren und prüfen, ob die Fugen exakt
aufeinandertreffen. Danach merkt sich das Programm die Einstellungen.

## Wie der Algorithmus arbeitet

* Aus jeder P3D werden die Memorypunkte **LB/PB** (Anfang links/rechts) und **LE/PE** (Ende links/rechts) gelesen.
  Daraus ergeben sich Länge, Kurvenwinkel, Radius und Breite jedes Teils (z.B. `asf1_22 50` = 22,5°, R 50 m).
* Jede Kurve kann auch umgedreht eingebaut werden → ergibt die Gegenkurve (wie im TB-Road-Tool).
* Eine **Strahlsuche (Beam Search)** setzt Teil für Teil an das Ende der Kette und bewertet jede Variante:
  * Abstand² der Teil-Mittellinie zur Polyline (integriert entlang des Teils),
  * Richtungsfehler am Teilende,
  * kleine Strafen pro Teil und pro Grad Kurve (verhindert Schlängeln),
  * am Schluss der Abstand zum Linienende.
  Pro 0,5 m Stationierung werden nur die besten *Suchbreite* Varianten weiterverfolgt.
* Weil jedes Teil exakt am Ausgang des vorherigen beginnt, gibt es **keine Lücken** (Kontrollwert „Fugen“ im
  Protokoll, typisch < 1 mm). Scharfe Knicke in der Polyline werden automatisch mit Kurventeilen ausgerundet.

### Parameter

| Parameter | Wirkung |
|---|---|
| Suchbreite | Mehr = genauer, langsamer. 12 reicht meist, 20–30 für schwierige Linien. |
| Strafe pro Teil | Höher = weniger und längere Teile, etwas mehr Abweichung. |
| Strafe pro Grad | Höher = mehr Geraden, weniger kleine Ausgleichskurven. |
| Endtoleranz | Wie weit das letzte Teil vom Linienende entfernt aufhören darf. |
| Startwinkel ± | Erlaubt am Anfang eine leicht andere Richtung als das erste Liniensegment. |
| Linienrichtung umkehren | Baut von der anderen Seite (ändert, wo die Teilefuge am Ende landet). |

## Kommandozeile

```
DayZRoadBuilder.Cli --parts "C:\Users\jonas\Desktop\Road Parts" --list
DayZRoadBuilder.Cli --parts "...\Road Parts" --shp test_road.shp --family asf1 --out road.txt
       [--endcap asf1_6konec] [--flip-endcaps] [--reverse] [--beam 20] [--exclude "asf1_60 10"]
       [--piece-penalty 1] [--turn-penalty 0.05] [--end-tol 3.5] [--model-origin] [--invert-yaw]
       [--yaw-offset 0] [--offset-x 0] [--offset-y 0]
```

## Grenzen / Ideen für später

* Kreuzungen (`kr_t_*`, `kr_x_*`) werden nicht automatisch gesetzt – Straßen an Kreuzungen als getrennte Linien
  zeichnen und die Kreuzung von Hand setzen.
* Höhe/Neigung: Pitch/Roll = 0, Z relativ = 0; die Straßenteile passen sich im Spiel dem Terrain an.
* Binarisierte (ODOL) Modelle können nicht gelesen werden.
