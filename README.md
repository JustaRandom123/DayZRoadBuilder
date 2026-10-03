# DayZ Road Builder

**Automatically build DayZ roads from a polyline.**
DayZ Road Builder takes a road line (ESRI shapefile) and chains the original DayZ road parts
(`asf1_25`, `asf1_10 100`, `city_6`, `mud_60 10`, …) along it – seamlessly connected, exactly like the
road tool in Terrain Builder does, but without placing a single part by hand. The result is an object list
that you import straight into **Terrain Builder**.

![Example: test_road.shp built with asf1 parts](docs/images/example_preview.png)

*The example above was reconstructed purely from the exported Terrain Builder file
([`example/test_road_asf1_objects.txt`](example/test_road_asf1_objects.txt)): red = input polyline,
blue = the 35 placed road parts. Max. deviation from the line 0.91 m (at the sharp 60° corner), average 0.20 m,
gaps between parts < 1 mm.*

---

## Contents

- [Features](#features)
- [Repository layout](#repository-layout)
- [Requirements](#requirements)
- [Building in Visual Studio](#building-in-visual-studio)
- [Quick start](#quick-start)
- [Step-by-step usage](#step-by-step-usage)
- [Importing the result into Terrain Builder](#importing-the-result-into-terrain-builder)
- [Export file format](#export-file-format)
- [How it works](#how-it-works)
- [Settings reference](#settings-reference)
- [Included road parts](#included-road-parts)
- [Command line version](#command-line-version)
- [Troubleshooting](#troubleshooting)
- [Limitations and ideas](#limitations-and-ideas)
- [Credits and license notes](#credits-and-license-notes)

---

## Features

- Reads the **original DayZ road part models** (unbinarized MLOD `.p3d`) and extracts the road memory points
  `LB`, `PB`, `LE`, `PE`. Length, curve angle, radius and width of every part are calculated automatically –
  no hard-coded part tables.
- Reads **polylines from shapefiles** (`PolyLine`, `PolyLineZ`, `PolyLineM`, `Polygon`). Several records or
  several lines per record become several roads.
- Automatically chooses the **best sequence of straights and curves** to follow the line (beam search),
  rounding off sharp corners with curve parts.
- Curves are also placed **reversed** to get the opposite turn direction (the same trick the Terrain Builder
  road tool uses).
- **No gaps:** every part starts exactly where the previous one ends.
- Optional **end pieces** (`*_6konec`) at the start and/or end of the road.
- You choose which parts may be used (for example, leave out the tight `60 10` curve on main roads).
- **Live preview** with zoom/pan. Hover over a part to see its name, position and yaw.
- **Export** to the Terrain Builder object import format (`.txt`).
- Settings are remembered between sessions.
- **Command line version** for batch processing.

## Repository layout

```
DayZRoadBuilder/
├─ DayZRoadBuilder.sln            ← open this in Visual Studio
├─ src/
│  ├─ DayZRoadBuilder.Core/       P3D reader, SHP reader, part library, road builder, TB exporter
│  ├─ DayZRoadBuilder.App/        Windows desktop app (WinForms) with preview – start project
│  └─ DayZRoadBuilder.Cli/        command line version
├─ RoadParts/                     DayZ road part models (.p3d), used to read the part geometry
│  ├─ Chernarus/                  asf1, asf2, asf3, city, grav, mud, crossroads
│  ├─ Livonia/                    asf1enoch, asf2enoch, mudenoch
│  ├─ Sakhal/                     asf1sakhal_*, asf2sakhal, asf3sakhal, gravsakhal, mudsakhal, quarrysakhal, snowroad
│  └─ Sidewalks/                  sidewalk models (binarized, not usable by the tool)
├─ example/
│  ├─ test_road.shp/.shx/.dbf/.prj   example polyline
│  └─ test_road_asf1_objects.txt     result exported with the asf1 parts
└─ docs/images/                   images for this README
```

## Requirements

- **Windows 10/11**
- **Visual Studio Community 2022 or newer** with the **".NET desktop development"** workload
  (this includes the .NET 8 SDK). No NuGet packages or other dependencies are needed.
- To run a compiled build you need the **.NET 8 Desktop Runtime or newer**. The app is set to
  `RollForward=Major`, so it also runs on .NET 9 / 10.
- **DayZ Tools / Terrain Builder** for importing the result.

## Building in Visual Studio

1. Clone the repository, or download it as a ZIP and extract it.
2. Double-click **`DayZRoadBuilder.sln`**.
3. `DayZRoadBuilder.App` is the start project. Press **F5** (run) or use **Build → Build Solution**.
4. The EXE is created at
   `src\DayZRoadBuilder.App\bin\Debug\net8.0-windows\DayZRoadBuilder.exe`
   (or `bin\Release\…` if you build in Release configuration).

From a terminal you can also run `dotnet build -c Release` in the repository folder.

## Quick start

1. Start the app. It finds the bundled `RoadParts` folder automatically when the EXE sits anywhere inside
   the repository folder.
2. **Polyline (.shp)** → select `example/test_road.shp`.
3. **Road type** → `asf1`.
4. Click **Build road** and check the preview.
5. Click **Export for Terrain Builder (.txt)** and import the file into Terrain Builder
   ([see below](#importing-the-result-into-terrain-builder)).

## Step-by-step usage

### 1. Files

| Field | What to select |
|---|---|
| **Road parts folder** | A folder containing the road `.p3d` files. Subfolders are searched too. Use the bundled `RoadParts` folder, or your own `P:\DZ\structures\roads\parts`. The models must be **unbinarized (MLOD)**, as they are on the P: drive. Binarized (ODOL) files are skipped and listed in the log. |
| **Polyline (.shp)** | Your road line as a shapefile. Coordinates are used as they are, so the line must be in **Terrain Builder coordinates**. A shape exported from Terrain Builder already contains the 200000 easting offset. |

**Where does the polyline come from?** Draw the road as a line in a Terrain Builder shape layer and export it
as `.shp`, or draw it in QGIS / Global Mapper using the same coordinate system as your Terrain Builder project.
The line represents the **centre line** of the road. The direction you draw it in is the direction the parts are
chained in. You can flip it with *Reverse line direction*.

### 2. Road type and parts

- **Road type** lists every road family found in the folder: `asf1`, `asf2`, `asf3`, `city`, `grav`, `mud`,
  `asf1enoch`, `mudsakhal`, `snowroad`, …
- The checklist shows all straights and curves of that family with their geometry, for example
  `asf1_22 50 (Curve 22.5° R50 (19.63 m))`. **Untick parts you don't want to use.** For example, untick `60 10`
  on a main road if you don't want hairpin curves. Crosswalk parts are unticked by default.
- **End piece:** optionally place a `*_6konec` part at the start and/or end. If the end pieces face the wrong
  way, tick *flip end pieces*.

### 3. Fitting

The default values work well for most roads. See the [settings reference](#settings-reference) for details.

### 4. Build and check

Click **Build road**. The log shows one line per road, for example:

```
Record 1: 35 parts | line 498.4 m, road 497.2 m | max. deviation 0.91 m, avg 0.20 m | end gap 0.21 m | joints ≤ 0.001 m | 0.3 s
  Used: asf1_1 1000 ×6, asf1_0 2000 ×5, asf1_12 ×4, asf1_22 50 ×3, …
```

- **max. deviation / avg** – how far the road centre line is from your polyline.
- **end gap** – distance between the end of the last part and the end of your line.
- **joints** – sanity check of the gaps between neighbouring parts (should always be ~0).

Preview controls: **mouse wheel** = zoom, **drag** with the left or middle mouse button = pan,
**double-click** = fit everything. Hover over a part to see its name, position and yaw in the status bar.
Reversed curves are marked with ↺.

### 5. Export

Click **Export for Terrain Builder (.txt)** and save the file. All roads of the shapefile go into one file.

## Importing the result into Terrain Builder

### One-time preparation: the road parts must exist in your Terrain Builder library

Terrain Builder places objects by **template name**. Every name in the exported file (for example `asf1_25` or
`asf1_10 100`) must exist as a template in one of your Terrain Builder libraries. The template name is the
`.p3d` file name without the extension.

If your project already has the DayZ road parts in its library (for example, because you use the Terrain Builder
road tool), there is nothing to do. Otherwise:

1. Open the **Library Manager** in Terrain Builder.
2. Create a new library, for example `dz_roads`.
3. Add the road `.p3d` files from your **P: drive** (`P:\DZ\structures\roads\parts\…`) to that library.

> The `RoadParts` folder in this repository is only read by the tool to get the part geometry.
> Terrain Builder and the game always use the models from your P: drive / the game data.

### Import the object list

1. Open your Terrain Builder project. It must use the usual DayZ map frame origin: **Easting 200000, Northing 0**.
2. In the **Layers Manager**, go to **Objects** and add a new layer (for example `roads_generated`), or select an
   existing one. Make sure it is the **active** layer, because the objects are imported into it.
3. Choose **File → Import → Objects…**
4. Select the exported `.txt` file.
5. If the dialog shows height options (behind **Advanced**), choose **relative**. The file contains a height
   of `0` relative to the terrain.
6. Click **OK**. The parts appear along your line.

### Check the first import (important)

Two conventions are configurable because they cannot be verified without your Terrain Builder setup. Build a
short test road with at least one curve, import it and zoom in on the joints:

| What you see | Fix |
|---|---|
| Joints line up perfectly | Everything is correct. Keep the settings. |
| Curves bend the wrong way / the road is mirrored or rotated | Tick **Invert yaw (counter-clockwise)**. If it is rotated by a fixed angle, use **Yaw offset**. |
| Parts are shifted (curves ~1 m sideways, straights by half their length) | Set **Position =** to **Model origin [0,0,0]**. |
| Everything is shifted by exactly 200 km | The line has no Terrain Builder offset. Set **Offset X** to `200000` (or `-200000`). |
| "Wrong file format or source template not found" | A part name is not in your Terrain Builder library. See the preparation above. |

The app remembers these settings, so you only have to do this once.

## Export file format

One object per line, in the Terrain Builder object list format:

```
"asf1_12";204723.224;7806.642;37.7330;0.0000;0.0000;1.0000;0.000;
 name      X          Y        yaw     pitch  roll   scale  Z (relative to terrain)
```

| Field | Meaning |
|---|---|
| name | Template name, which is the `.p3d` file name without the extension |
| X / Y | Object position in Terrain Builder coordinates (easting / northing, metres). By default this is the **bounding box centre** of the model, which the engine uses as the object position for these parts (autocenter). |
| yaw | Rotation in degrees, **clockwise from north** (like `getDir`). |
| pitch / roll | Always `0`. Road parts follow the terrain in game. |
| scale | Always `1`. |
| Z | `0` = on the terrain surface. |

Decimal separator is always `.`, independent of your Windows language.

## How it works

### 1. Reading the road parts

Each DayZ road part has a **memory LOD** with four named points:

```
      LE ─────────── PE        LE / PE = left / right point at the END
       │             │
       │   driving   │
       │  direction  │
       │      ↑      │
      LB ─────────── PB        LB / PB = left / right point at the BEGINNING
```

From these points the tool calculates:

- the centre of the start edge and the end edge,
- the driving direction at both ends (perpendicular to `LB→PB` and `LE→PE`),
- the **turn angle** (for example 22.5°), the **radius** (for example 50 m) and the centre line **length**,
- the **width** (asf1 = 12 m, asf2 = 9 m, asf3 = 7 m, city = 10 m, grav = 6 m, mud = 5 m, …),
- the bounding box centre over all LODs, which becomes the exported object position.

The part type is detected from the name: `…konec` = end piece, `…crosswalk` = crosswalk, `kr_…` = crossroad.
Everything else is a straight or a curve, depending on its geometry.

All DayZ curve parts turn **right**. A curve placed **reversed** (driven from its end to its start) turns
**left**. That way every curve is available in both directions.

### 2. Following the line (beam search)

The tool builds the road part by part from the start of the line:

1. A *state* is the open end of the chain built so far: a position and a driving direction.
2. From every state, **every allowed part** (curves in both directions) is appended in turn.
3. Each candidate gets a **cost**:
   - the **squared distance** between the part's centre line and the polyline, integrated along the part
     (sampled every ~1 m),
   - the squared **heading error** at the end of the part,
   - a small **penalty per part**, which prefers longer parts,
   - a small **penalty per degree of curve**, which avoids needless left-right wiggling,
   - at the end of the line, the squared distance to the line's end point.
4. Candidates are sorted by how far along the line they got (their **chainage**, in 0.5 m bins). In every bin,
   only the best *N* candidates survive (*Search width*). Near-duplicates are removed so the kept candidates
   stay diverse.
5. When a candidate reaches the end of the line within the *End tolerance*, it becomes a finished road. The
   cheapest finished road wins.

Because each part is placed exactly at the exit of the previous one, using the real memory points, the
result has **no gaps**. Sharp corners in the polyline cannot be driven exactly with real parts, so the search
automatically rounds them off with the tightest suitable curves.

The projection onto the line is always done in a **local window** around the current chainage. Lines that
cross themselves, hairpins and closed loops therefore work too.

### 3. Exporting

For every placed part, the tool writes the template name, the world position of the model's reference point
(bounding box centre) and the yaw.

## Settings reference

| Setting | Default | Effect |
|---|---|---|
| **Search width** | 12 | Candidates kept per 0.5 m of line. Higher = more accurate but slower. 8–30 is sensible. Above ~12 the result rarely changes. |
| **Penalty per part** | 1.0 | Higher = fewer and longer parts, slightly more deviation. |
| **Penalty per degree** | 0.05 | Higher = more straights and fewer small corrective curves. |
| **End tolerance [m]** | 3.5 | How far from the end of the line the last part may stop. Increase it if the log says the end was not reached. |
| **Start angle ± [°]** | 0 | Also tries start directions within ± this angle of the first line segment. Useful if the line starts with a short kink. |
| **Reverse line direction** | off | Builds from the other end of the line. |
| **End piece / at start / at end / flip** | none | Optional end pieces and their orientation. |
| **Position =** | bounding box centre | Which model point is exported as the object position. |
| **Invert yaw** | off | Flips the rotation direction (see [troubleshooting](#troubleshooting)). |
| **Yaw offset [°]** | 0 | Added to every yaw. |
| **Offset X / Y [m]** | 0 | Added to all exported coordinates. |

Settings are stored in `%AppData%\DayZRoadBuilder\settings.ini`.

## Included road parts

All families share the same part set:

| Part suffix | Type | Geometry |
|---|---|---|
| `_25`, `_12`, `_6` | straight | 25 m, 12.5 m, 6.25 m |
| `_0 2000` | curve | 0.5°, radius 2000 m, 17.45 m |
| `_1 1000` | curve | 1°, radius 1000 m, 17.45 m |
| `_7 100` | curve | 7.5°, radius 100 m, 13.09 m |
| `_10 100` / `_10 75` / `_10 50` / `_10 25` | curve | 10°, radius 100 / 75 / 50 / 25 m |
| `_15 75` | curve | 15°, radius 75 m, 19.63 m |
| `_22 50` | curve | 22.5°, radius 50 m, 19.63 m |
| `_30 25` | curve | 30°, radius 25 m, 13.09 m |
| `_60 10` | curve | 60°, radius 10 m, 10.47 m |
| `_6konec` | end piece | 6.25 m |
| `_6_crosswalk` | crosswalk | 6.25 m (asf1, asf2, city) |

| Folder | Families (width) |
|---|---|
| Chernarus | `asf1` (12 m), `asf2` (9 m), `asf3` (7 m), `city` (10 m), `grav` (6 m), `mud` (5 m), crossroads `kr_t_*`, `kr_x_*` |
| Livonia | `asf1enoch` (12 m), `asf2enoch` (9 m), `mudenoch` (5 m) |
| Sakhal | `asf1sakhal_dashedline` (12 m), `asf1sakhal_fullLine` (12 m), `asf2sakhal` (9 m), `asf3sakhal` (7 m), `gravsakhal` (6 m), `mudsakhal` (6 m), `quarrysakhal` (12 m), `snowroad` (6 m) |
| Sidewalks | binarized, so the tool cannot read them |

To list everything the tool finds in a folder:
`DayZRoadBuilder.Cli --parts RoadParts --list`

## Command line version

```
DayZRoadBuilder.Cli --parts <folder> --shp <file.shp> --family <type> --out <file.txt> [options]
```

| Option | Meaning |
|---|---|
| `--parts <folder>` | Folder with the road P3Ds (searched recursively) |
| `--shp <file.shp>` | Polyline shapefile |
| `--family <type>` | Road type, for example `asf1`, `city`, `mud`, `asf1enoch` |
| `--out <file.txt>` | Terrain Builder object list to write |
| `--list` | Only list the road types and parts found |
| `--exclude <a,b,…>` | Exclude parts by name, for example `--exclude "asf1_60 10,asf1_30 25"` |
| `--endcap <name>` | End piece at start and end, for example `--endcap asf1_6konec` |
| `--flip-endcaps` | Place the end pieces reversed |
| `--reverse` | Reverse the line direction |
| `--beam <n>` | Search width (default 12) |
| `--piece-penalty <x>` | Penalty per part (default 1.0) |
| `--turn-penalty <x>` | Penalty per degree of curve (default 0.05) |
| `--end-tol <m>` | End tolerance (default 3.5) |
| `--model-origin` | Export the model origin instead of the bounding box centre |
| `--invert-yaw` | Invert the yaw sign |
| `--yaw-offset <deg>` | Yaw offset |
| `--offset-x <m>`, `--offset-y <m>` | Coordinate offset |

Example:

```
DayZRoadBuilder.Cli --parts RoadParts --shp example\test_road.shp --family asf1 --out road.txt
Record 1: line 498.4 m -> 35 parts, road 497.2 m, max. deviation 0.91 m, RMS 0.20 m, end gap 0.21 m, max. joint gap 0.0006 m
35 objects written: road.txt
```

## Troubleshooting

| Problem | Solution |
|---|---|
| "binarized (ODOL) P3D file(s) skipped" | The tool can only read unbinarized models. Use the models from the P: drive (DayZ Tools → extracted game data) or the bundled `RoadParts` folder. |
| "The end of the line was not reached within the tolerance" | Enable more or shorter parts (`_6`, `_10 25`), increase *End tolerance*, or check that the line has no extremely sharp zig-zags. |
| The road cuts a sharp corner of the line | Sharp corners can't be driven with real parts. Enable tighter curves (`30 25`, `60 10`) or draw the corner as a smooth arc. |
| Many small curves on a straight section | Increase *Penalty per degree* (for example `0.2`). |
| Too many short parts | Increase *Penalty per part* (for example `3`). |
| Parts are mirrored, rotated or shifted after import | See [Check the first import](#check-the-first-import-important). |
| A closed loop has a small gap or overlap where it meets itself | Expected. Parts have fixed lengths, so the loop rarely closes exactly. Fix the last part by hand. |

## Limitations and ideas

- **Crossroads** (`kr_t_*`, `kr_x_*`) are not placed automatically. Draw the roads up to the crossing as
  separate lines and place the crossroad by hand.
- Pitch, roll and height are always 0. The road parts follow the terrain in game.
- Binarized (ODOL) models cannot be read.
- Ideas: automatic crossroad placement, mixed road types per shapefile attribute, sidewalks along roads.

## Credits and license notes

- The road part models in `RoadParts/` are **© Bohemia Interactive** and come from DayZ / DayZ Tools. They are
  included only so the tool works out of the box, and remain subject to Bohemia Interactive's DayZ licence and
  modding terms. They are not covered by this project's licence.
- Shapefile format: ESRI Shapefile Technical Description. P3D MLOD format: Bohemia Interactive (Object Builder).
