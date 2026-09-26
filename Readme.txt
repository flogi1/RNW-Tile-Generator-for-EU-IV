RNW Tile Generator for Europa Universalis IV
============================================

RNW Tile Generator creates "Random New World" tiles for Europa Universalis IV:
ready-made map pieces that replace a part of the game map. You draw or
generate the coastline, mountains and height, rivers, provinces and special
features (straits, modifiers, regions), and the program exports the tile's
text file and bitmaps in the format the game expects. Existing RNW tiles can
be loaded and changed as well.


Download
--------

Get the latest version from the Releases page of this repository. Download
RnwTileGenerator-<version>-win-x64.zip, extract it to a folder you can write
to and start RnwTileGenerator.exe. No .NET installation is needed.

See "Installation Readme.txt" for the full setup guide, including what to do
when Windows SmartScreen or Smart App Control blocks the unsigned program.


Updates
-------

Help -> Check for updates. The program downloads the new version, checks its
signature and restarts. By default it also checks once a day at startup (you
can switch this off in the Help menu).


Build from source
-----------------

Needs the free .NET 10 SDK on Windows 10 or 11 (64-bit). Double-click
run.bat, or run:

    dotnet build RnwTileGenerator.sln
    dotnet run --project RnwTileGenerator.App


Report a bug
------------

Help -> Report a bug. You can post the report as a GitHub issue (needs a
GitHub account) or send it by email. You can also open an issue on this
repository directly.


License
-------

MIT, see LICENSE.
