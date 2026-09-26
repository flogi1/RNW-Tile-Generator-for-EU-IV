RNW Tile Generator - Installation & Setup
=========================================

RNW Tile Generator creates "Random New World" tile files for Europa
Universalis IV. It is a Windows desktop application.

There are two ways to get it running. Most people should use Option A.

  Option A - Ready-to-run release (recommended)
      Unzip, double-click RnwTileGenerator.exe. Nothing else to install.

  Option B - Build from source
      For people who received the source folder instead of the release ZIP,
      or whose Windows blocks the unsigned .exe from Option A.
      Needs the free .NET 10 SDK and builds the program once on your PC.


REQUIREMENTS (both options)
---------------------------

- Windows 10 or Windows 11, 64-bit. The program uses WPF, a Windows-only
  technology. It does not run on macOS or Linux.
- About 100 MB of free disk space for Option A (the program itself is one
  file of roughly 75 MB).
- No internet connection is needed to run it. The only automatic connection
  is the update check: once a day at startup the program asks GitHub for the
  latest version number (no data about you or your tiles is sent). Switch it
  off with Help > "Check for updates at startup". "Update now", "Report a
  bug" and "Donate" only go online when you click them.


OPTION A - READY-TO-RUN RELEASE
-------------------------------

1. Download "RnwTileGenerator-<version>-win-x64.zip" from the Releases page
   (https://github.com/flogi1/RNW-Tile-Generator-for-EU-IV/releases) and
   extract it to a folder you can write to, for example
   %LocalAppData%\Programs\RNW Tile Generator, or a folder inside Documents.

   Do NOT put it in "C:\Program Files" or another protected folder. The
   program saves its settings, presets and saved seeds as small files next
   to RnwTileGenerator.exe and needs permission to write there. Automatic
   updates also only work in a folder you can write to.

2. Keep the extracted files together. RnwTileGenerator.exe needs the
   "Icons" folder that sits next to it (it holds the game icons shown in the
   "Special Features" tab).

3. Double-click RnwTileGenerator.exe. A small loading window with a progress
   bar appears for a moment, then the program opens.

4. If Windows shows a blue "Windows protected your PC" (SmartScreen) window:
   the program is not digitally signed by a registered publisher, which is
   normal for a small independent tool. Click "More info", then
   "Run anyway".

5. If Windows refuses to start the .exe and offers no "Run anyway" option
   (this happens when "Smart App Control" is switched on), use Option B
   below. A program you build yourself on your own PC is not blocked.

Updating (version 1.0.6 and later): when a new version exists, a yellow bar
appears under the menu, or use Help > "Check for updates". Click "Update
now": the program downloads the new version from GitHub, checks its
signature and asks whether to save your project. Then it closes, replaces
its own files and restarts with your project reopened. Your settings,
presets and saved seeds stay as they are. If anything goes wrong, the old
version is restored automatically.

Updating from 1.0.5 (once, by hand): version 1.0.5 has no updater yet.
Extract the 1.0.6 (or newer) ZIP into a NEW folder. To keep your settings, presets and
saved seeds, copy the small .json files from the old folder (for example
language.json, generation_presets.json, generation_prefs.json,
saved_seeds.json) next to the new .exe. Saved projects (.rnwproj) are
separate files and are not affected. From then on updates are automatic.


OPTION B - BUILD FROM SOURCE
----------------------------

1. Install the ".NET 10 SDK" (once per computer).
   - Go to https://dotnet.microsoft.com/download
   - Choose ".NET 10" and download the "SDK" installer for Windows x64.
     It must say "SDK" - not "Runtime" or "ASP.NET Core Runtime".
   - Run the installer with the default options. No restart is needed.
   - To check: open Command Prompt and type   dotnet --version
     A version starting with "10." means it is installed.

2. Copy the whole source folder to a writable place (Desktop or Documents,
   not "Program Files"). Keep all files and subfolders together:

      RnwTileGenerator.App\
      RnwTileGenerator.Core\
      RnwTileGenerator.Updates\
      RnwTileGenerator.Checks\
      Readme\
      tools\
      RnwTileGenerator.sln
      run.bat
      build.bat
      build_and_run.bat

3. Double-click "run.bat".
   - The first time, it builds the program. This takes a few seconds and
     runs in a minimised console window, so you may only see a taskbar
     entry. The program window opens when the build is done, and the
     console window closes by itself.
   - If the build fails, Notepad opens with the error log (build_log.txt).
   - Every later start opens the program directly, without a build.

   "build_and_run.bat" always does a clean rebuild first. Use it after
   replacing source files with a newer version. "build.bat" only builds and
   keeps its window open, which is useful to read errors.


USING THE PROGRAM (short version)
---------------------------------

Work through the tabs from left to right: Coastline, Mountains & Height,
Rivers, Provinces, Special Features, Metadata, Export. Or use the "Random
Generation" tab to create a complete tile automatically and then refine it
by hand. Existing game tiles and saved projects (.rnwproj) can also be
opened from the start screen or the File menu.

In the Export tab, pick an output folder and click "Export tile files". The
program writes  <tile name>\<tile name>.txt  plus a  data\  subfolder with
the bitmaps, matching the layout of the game's own tile folders. Click
"Run validation checks" first. It warns about problems such as river loops
that would crash the game.


TROUBLESHOOTING
---------------

- "Windows protected your PC" - see Option A, step 4.

- The program does not start, or Windows removes the .exe. Some antivirus
  programs are suspicious of unsigned, independently built programs. Restore
  the file from quarantine and add an exclusion for the program folder. It
  is a false positive.

- Option B: "'dotnet' is not recognized" - the .NET SDK is not installed, or
  Windows needs to pick up the new PATH. Close and reopen the window, or
  restart the computer once.

- Option B: the build fails - make sure ALL files and folders were copied.
  Then run "build.bat" and read the error message.

- Icons in the "Special Features" tab appear as plain colored circles - the
  "Icons" folder is missing. Copy it back next to the program. (Option B:
  it is inside RnwTileGenerator.App.)

- Own icons: put your own .dds files into a folder "UserIcons" next to
  RnwTileGenerator.exe, named like the shipped file in "Icons" they
  replace. "Icons" itself is replaced by every update; "UserIcons" is never
  touched.

- The program crashed - a file named crash_log.txt is written next to the
  .exe. Click "Report bug" in the error window, or use Help > "Report a
  bug": you can post the report as a GitHub issue (needs a GitHub account)
  or send it by email. The last crash log entries are included, with your
  personal folder names removed.

- "Update not possible" - the program folder is protected (for example
  inside "C:\Program Files") or is a source build (Option B). Download the
  new version from the Releases page by hand, or move the program folder
  into your user profile so it can update itself.


WHERE THINGS ARE SAVED
----------------------

Settings, remembered brush sizes, random seeds, the chosen language and
generation presets are saved as small .json files next to the program
(Option A: next to RnwTileGenerator.exe; Option B: inside
RnwTileGenerator.App\bin\Debug\net10.0-windows\). They are created
automatically and can be deleted for a fresh start.

Update settings (check at startup, skipped version), downloaded updates and
the update log (update.log) are kept in %LocalAppData%\RnwTileGenerator\.

Your work is saved wherever you choose with File > Save project. Exported
tiles go to the folder you pick in the Export tab.
