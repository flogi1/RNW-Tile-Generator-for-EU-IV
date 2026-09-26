# RNW Tile Generator 1.0.5

A standalone Windows tool that creates **Random New World tiles for Europa Universalis IV**: the tile text file plus the coastline, height, river and province bitmaps the game expects. Draw a tile by hand, generate one automatically, or start from an existing game tile.

No installation, no dependencies: unzip and run `RnwTileGenerator.exe`. See `Installation Readme.txt`.

## Highlights

- **Full tile workflow in one program** - coastline, mountains and height, rivers, provinces, special features, metadata and export, each in its own tab.
- **Automatic tile generation** - a Random Generation tab builds coastline, terrain, rivers, provinces, straits, modifiers and regions in one run. Every result is reproducible from its seed. Sliders, saved seeds and generation presets (six built in, unlimited custom ones) let you steer the result.
- **Rivers that look like rivers and never crash the game** - generated rivers form clean trees (no loops, no scribbles), with tributaries and optional delta arms. A "River smoothness" slider controls how calm or winding they are. An export check warns about river loops before they reach the game.
- **Organic province borders** - automatic provinces with adjustable size, size variance and border curviness, without stray pixels.
- **Open what already exists** - load existing game tiles to edit them, import a grayscale heightmap, or trace a map image into land and water (experimental).

## Features

**Drawing tools**
- Coastline, mountain/height and river painting with adjustable brush sizes and brush presets
- Rivers: waypoint mode and freehand mode (pixel by pixel), with source, merge, split and patch segment types and live preview of the real river type and colour
- Undo/redo throughout, zoom and pan with pixel-accurate display, optional grid
- Semi-transparent reference image overlay as a tracing aid (never saved)

**Generation**
- Coastline generation with metadata flags for north/south-edge and equator restriction
- Terrain height from noise or from an imported heightmap, with ridge-preserving smoothing
- River network from flow accumulation: main rivers, tributaries at real confluences, sizes derived from catchment area
- Province generation: land and sea provinces, size variance, curvy borders, optional 1000-province warning
- Automatic straits, modifiers and regions, with adjustable frequency

**Special features**
- Straits, modifiers and regions placed by hand or automatically, using the game's own icons
- Trade-centre, natural-harbour, estuary and paradise icons

**Project and export**
- Save and load projects (`.rnwproj`)
- Export writes the tile text file and the three bitmaps in the game's own folder layout
- Validation before export: duplicate province colours, land below water level, empty layers, province limit, river loops
- Tile size up to the game's grid limits; tiles can be renamed in the Export tab

**Interface**
- Seven languages: English, German, Spanish, French, Italian, Russian and Simplified Chinese
- Start-up loading screen, application icon, no console window
- Every slider has an editable number field
- Crash log written next to the program if something goes wrong

## Requirements

- Windows 10 or 11, 64-bit
- Nothing else. The release is self-contained (about 75 MB).

## Known limitations

- The program is not digitally signed. Windows SmartScreen shows a warning on first launch ("More info" -> "Run anyway"), and Smart App Control may block it. Building from source (see the readme) avoids that.
- Map-image tracing is experimental and uses a plain brightness threshold.
- On very flat terrain, generated rivers at the smoothest settings can still show some 45-degree corners.

## Support the project

If the tool is useful to you, the **Donate** entry in the menu bar links to the project's GitHub Sponsors page.
