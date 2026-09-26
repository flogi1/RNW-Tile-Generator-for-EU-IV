using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Special Features stage: straits, regions, province names, and
/// province modifiers/flavor - split out of what used to be one big
/// "Metadata" tab so the map-related, per-province content (this tab) is
/// distinct from the tile-wide flags/weight/extras (StageMetadata), and
/// shown first since it's the content people reach for right after
/// generating provinces.
///
/// Runde 6: entries are now placed by clicking directly on the map (one
/// mode at a time - Straits/Regions/Names/Modifiers, picked via the radio
/// group below) instead of only through pick-from-a-cryptic-dropdown
/// dialogs, and existing entries are drawn as colored markers over the
/// province map so they can actually be reviewed spatially (this was the
/// most-requested follow-up after Runde 5's dropdown-only version,
/// especially for reviewing where auto-generated regions ended up).
///
/// Runde 9: the modifiers uploaded so far (estuary, paradise, level 1
/// center of trade, strait) now draw as the actual game icon (see
/// DdsIcon.cs/IconCatalog.cs) instead of a plain colored marker, wherever
/// a matching .dds file is present next to the .exe; "important natural
/// harbor" (no icon supplied for it yet) and Regions/Names still use the
/// original colored ring/disk markers, same as any icon-backed marker
/// falls back to if its .dds file is ever missing/unreadable.
///
/// All four lists here can ALSO be filled automatically from the Random
/// Generation tab (straits/modifiers with a frequency slider, regions with
/// a target count) - see TileProject.RandomTileOptions.StraitFrequency /
/// ModifierFrequency / AutoRegionCount.</summary>
public sealed class StageSpecialFeatures : IStage
{
    private static readonly string[] CommonModifiers =
    {
        "river_estuary_modifier",
        "important_natural_harbor",
        "level_1_center_of_trade",
        "paradise_modifier",
    };

    /// <summary>Distinct marker colors cycled by index so neighboring
    /// regions (which otherwise have no other visual identity - a region
    /// is just a single seed color in the file format, see
    /// TileMetadata.Regions) are still tellable apart on the map.</summary>
    private static readonly Rgb[] MarkerPalette =
    {
        new(255, 255, 255), new(255, 90, 90), new(90, 255, 130), new(255, 225, 70),
        new(130, 185, 255), new(255, 130, 255), new(70, 225, 225), new(255, 160, 70),
    };

    private static readonly Dictionary<string, Rgb> ModifierMarkerColors = new()
    {
        ["river_estuary_modifier"] = new Rgb(90, 185, 255),
        ["important_natural_harbor"] = new Rgb(70, 225, 225),
        ["level_1_center_of_trade"] = new Rgb(255, 215, 70),
        ["paradise_modifier"] = new Rgb(255, 110, 220),
    };

    private static Rgb ModifierColor(string name) => ModifierMarkerColors.TryGetValue(name, out var c) ? c : new Rgb(230, 230, 230);

    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _instruction;
    private readonly TextBlock _hoverInfo;
    private readonly Button _cancelPickButton;

    private ListBox _straitList = null!, _regionList = null!, _nameList = null!, _modList = null!;
    private readonly List<(string name, Rgb color)> _modIndex = new();

    // Runde 7 (second round of feedback): standalone generate/regenerate
    // controls for Straits/Regions/Modifiers, so each can be (re-)run on an
    // already-generated tile without a full Random Generation pass - shares
    // the exact same GenerationPrefs keys as StageRandom so a value tweaked
    // in either tab is remembered for both ("Einstellungen müssen dort auch
    // entsprechend verstellbar sein, wie im Random Generator").
    private double _straitGenFrequency = GenerationPrefs.GetDouble("random.straitFrequency", 0);
    private double _straitGenMaxDistance = GenerationPrefs.GetDouble("random.straitMaxDistance", 200);
    private double _straitGenMinSpacing = GenerationPrefs.GetDouble("random.straitMinSpacing", 48);
    private double _straitGenMaxPerIsland = GenerationPrefs.GetDouble("random.straitMaxPerIsland", 3);
    private double _regionGenCount = GenerationPrefs.GetDouble("random.autoRegionCount", 0);
    private double _modifierGenFrequency = GenerationPrefs.GetDouble("random.modifierFrequency", 0);

    private string _mode = "straits"; // "straits" | "regions" | "names" | "modifiers"
    private int _straitStage; // 0 = pick first land, 1 = pick connecting sea, 2 = pick second land
    private Rgb? _straitFrom;
    private Rgb? _straitThrough;
    private int? _hoverPid;

    private ProvinceMapResult? _colorLookupSource;
    private Dictionary<Rgb, ProvinceInfo> _colorLookup = new();

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public StageSpecialFeatures(MainWindow app)
    {
        _app = app;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("specialFeatures.title")));
        side.Children.Add(Ui.Wrap(Loc.T("specialFeatures.intro"), 300));

        var modePanel = Ui.Stack();
        var straitsRadio = Ui.Radio(Loc.T("specialFeatures.straits"), "sf-mode", true, (_, _) => SetMode("straits"));
        straitsRadio.ToolTip = Loc.T("specialFeatures.straits.tip");
        modePanel.Children.Add(straitsRadio);
        modePanel.Children.Add(Ui.Radio(Loc.T("specialFeatures.regions"), "sf-mode", false, (_, _) => SetMode("regions")));
        modePanel.Children.Add(Ui.Radio(Loc.T("specialFeatures.names"), "sf-mode", false, (_, _) => SetMode("names")));
        modePanel.Children.Add(Ui.Radio(Loc.T("specialFeatures.modifiers"), "sf-mode", false, (_, _) => SetMode("modifiers")));
        side.Children.Add(Ui.Group(Loc.T("specialFeatures.mode"), modePanel));

        _instruction = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 300, Margin = new Thickness(0, 4, 0, 2), FontWeight = FontWeights.Bold };
        side.Children.Add(_instruction);
        _cancelPickButton = Ui.Button(Loc.T("specialFeatures.cancelPick"), (_, _) => ResetStraitPick());
        _cancelPickButton.Visibility = Visibility.Collapsed;
        side.Children.Add(_cancelPickButton);
        _hoverInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 300, Margin = new Thickness(0, 2, 0, 10), Foreground = Brushes.Gray, FontSize = 11 };
        side.Children.Add(_hoverInfo);

        BuildStraits(side);
        BuildRegions(side);
        BuildNames(side);
        BuildModifiers(side);

        _canvas = new PaintCanvasControl { ImageProvider = ProvideImage };
        _canvas.OnPaint = OnPaint;

        View = Ui.SideAndCanvas(Ui.Sidebar(320, side), _canvas);
        UpdateInstruction();
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        RefreshStraits();
        RefreshRegions();
        RefreshNames();
        RefreshModifiers();
        _canvas.RequestRedraw();
    }

    private void SetMode(string mode)
    {
        _mode = mode;
        ResetStraitPick();
        UpdateInstruction();
        _canvas.RequestRedraw();
    }

    private void UpdateInstruction()
    {
        _instruction.Text = _mode switch
        {
            "straits" => _straitStage switch
            {
                0 => Loc.T("specialFeatures.instr.straits0"),
                1 => Loc.T("specialFeatures.instr.straits1"),
                _ => Loc.T("specialFeatures.instr.straits2"),
            },
            "regions" => Loc.T("specialFeatures.instr.regions"),
            "names" => Loc.T("specialFeatures.instr.names"),
            _ => Loc.T("specialFeatures.instr.modifiers"),
        };
        _cancelPickButton.Visibility = _mode == "straits" && _straitStage > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetStraitPick()
    {
        _straitStage = 0;
        _straitFrom = null;
        _straitThrough = null;
        UpdateInstruction();
    }

    // -- helpers --------------------------------------------------------------

    private Dictionary<Rgb, ProvinceInfo> ColorLookup(TileProject p)
    {
        if (!ReferenceEquals(_colorLookupSource, p.ProvinceResult))
        {
            _colorLookupSource = p.ProvinceResult;
            _colorLookup = new Dictionary<Rgb, ProvinceInfo>();
            if (p.ProvinceResult != null)
                foreach (var info in p.ProvinceResult.Provinces.Values) _colorLookup[info.Color] = info;
        }
        return _colorLookup;
    }

    // -- click handling ---------------------------------------------------------

    private void OnPaint(PaintEventKind kind, double ix, double iy)
    {
        var p = _app.Project!;
        if (p.ProvinceResult == null) return;
        int x = (int)Math.Round(ix), y = (int)Math.Round(iy);
        bool inBounds = x >= 0 && x < p.WidthPx && y >= 0 && y < p.HeightPx;

        if (kind == PaintEventKind.Hover)
        {
            int? newHover = null;
            if (inBounds)
            {
                int hoverPid = p.ProvinceResult.Labels[x, y];
                if (hoverPid >= 0) newHover = hoverPid;
            }
            if (newHover != _hoverPid)
            {
                _hoverPid = newHover;
                UpdateHoverInfo();
                _canvas.RequestRedraw();
            }
            return;
        }

        if (kind != PaintEventKind.Down || !inBounds) return;
        int clickedPid = p.ProvinceResult.Labels[x, y];
        if (clickedPid < 0 || !p.ProvinceResult.Provinces.TryGetValue(clickedPid, out var info)) return;

        switch (_mode)
        {
            case "regions": ClickRegion(info); break;
            case "straits": ClickStrait(info); break;
            case "names": ClickName(info); break;
            case "modifiers": ClickModifier(info); break;
        }
    }

    private void UpdateHoverInfo()
    {
        var p = _app.Project!;
        if (!_hoverPid.HasValue || p.ProvinceResult == null || !p.ProvinceResult.Provinces.TryGetValue(_hoverPid.Value, out var info))
        {
            _hoverInfo.Text = "";
            return;
        }
        string text = string.Format(Loc.T("specialFeatures.hoverBase"), info.ProvinceId, info.Kind.ToString().ToLowerInvariant());
        if (p.Metadata.ProvinceNames.TryGetValue(info.Color, out var name))
            text += string.Format(Loc.T("specialFeatures.hoverName"), name);
        if (p.Metadata.Regions.Contains(info.Color))
            text += Loc.T("specialFeatures.hoverRegion");
        var mods = p.Metadata.Modifiers.Where(kv => kv.Value.Contains(info.Color)).Select(kv => kv.Key).ToList();
        if (mods.Count > 0)
            text += string.Format(Loc.T("specialFeatures.hoverModifiers"), string.Join(", ", mods));
        _hoverInfo.Text = text;
    }

    private void ClickRegion(ProvinceInfo info)
    {
        var regions = _app.Project!.Metadata.Regions;
        if (!regions.Remove(info.Color)) regions.Add(info.Color);
        RefreshRegions();
        _canvas.RequestRedraw();
    }

    private void ClickStrait(ProvinceInfo info)
    {
        if (_straitStage == 0)
        {
            if (info.Kind != ProvinceKind.Land) { FlashInstruction(Loc.T("specialFeatures.instr.wrongKindLand")); return; }
            _straitFrom = info.Color;
            _straitStage = 1;
        }
        else if (_straitStage == 1)
        {
            if (info.Kind != ProvinceKind.Sea && info.Kind != ProvinceKind.Lake) { FlashInstruction(Loc.T("specialFeatures.instr.wrongKindSea")); return; }
            _straitThrough = info.Color;
            _straitStage = 2;
        }
        else
        {
            if (info.Kind != ProvinceKind.Land) { FlashInstruction(Loc.T("specialFeatures.instr.wrongKindLand")); return; }
            if (info.Color.Equals(_straitFrom!.Value)) { FlashInstruction(Loc.T("specialFeatures.instr.sameProvince")); return; }
            _app.Project!.Metadata.Straits.Add(new Strait { From = _straitFrom!.Value, To = info.Color, Through = _straitThrough!.Value });
            RefreshStraits();
            ResetStraitPick();
        }
        UpdateInstruction();
        _canvas.RequestRedraw();
    }

    private void FlashInstruction(string message)
    {
        _instruction.Text = message;
        _instruction.Foreground = Brushes.OrangeRed;
        // Revert to the normal step instruction shortly after, so the
        // warning is noticed but doesn't get stuck once the user corrects
        // their click.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) => { timer.Stop(); _instruction.Foreground = Brushes.Black; UpdateInstruction(); };
        timer.Start();
    }

    private void ClickName(ProvinceInfo info)
    {
        var names = _app.Project!.Metadata.ProvinceNames;
        names.TryGetValue(info.Color, out var existing);
        var result = Dialogs.OneText(_app, Loc.T("specialFeatures.namePickTitle"), Loc.T("specialFeatures.namePickLabel"), existing ?? "");
        if (result == null) return; // cancelled
        result = result.Trim();
        if (result.Length == 0) names.Remove(info.Color);
        else names[info.Color] = result;
        RefreshNames();
        _canvas.RequestRedraw();
    }

    private void ClickModifier(ProvinceInfo info)
    {
        var result = Dialogs.ComboPick(_app, Loc.T("specialFeatures.modifierPickTitle"), Loc.T("specialFeatures.modifierPickLabel"), CommonModifiers);
        if (result == null || result.Length == 0) return;
        var m = _app.Project!.Metadata;
        if (!m.Modifiers.TryGetValue(result, out var list)) m.Modifiers[result] = list = new List<Rgb>();
        if (!list.Contains(info.Color)) list.Add(info.Color);
        RefreshModifiers();
        _canvas.RequestRedraw();
    }

    // -- rendering: province map + mode-specific markers -----------------------

    private byte[] ProvideImage(Int32Rect rect)
    {
        var p = _app.Project!;
        var buf = Render.CompositeProvinces(p, rect, false, _hoverPid);
        if (p.ProvinceResult == null) return buf;
        var lookup = ColorLookup(p);

        switch (_mode)
        {
            case "regions":
                for (int i = 0; i < p.Metadata.Regions.Count; i++)
                {
                    if (!lookup.TryGetValue(p.Metadata.Regions[i], out var info)) continue;
                    var c = MarkerPalette[i % MarkerPalette.Length];
                    DrawRing(buf, rect, info.Seed.x, info.Seed.y, 7, c, 3);
                    DrawDisk(buf, rect, info.Seed.x, info.Seed.y, 2, c);
                }
                break;

            case "straits":
                var straitIcon = IconCatalog.TryGet("strait");
                foreach (var s in p.Metadata.Straits)
                {
                    if (!lookup.TryGetValue(s.From, out var fromInfo) || !lookup.TryGetValue(s.To, out var toInfo) || !lookup.TryGetValue(s.Through, out var throughInfo)) continue;
                    var line = new Rgb(255, 240, 60);
                    DrawLine(buf, rect, fromInfo.Seed.x, fromInfo.Seed.y, throughInfo.Seed.x, throughInfo.Seed.y, line, 1);
                    DrawLine(buf, rect, throughInfo.Seed.x, throughInfo.Seed.y, toInfo.Seed.x, toInfo.Seed.y, line, 1);
                    DrawDisk(buf, rect, fromInfo.Seed.x, fromInfo.Seed.y, 4, new Rgb(255, 150, 60));
                    DrawDisk(buf, rect, toInfo.Seed.x, toInfo.Seed.y, 4, new Rgb(255, 150, 60));
                    if (straitIcon != null) DrawIcon(buf, rect, throughInfo.Seed.x, throughInfo.Seed.y, straitIcon, 22);
                    else DrawDisk(buf, rect, throughInfo.Seed.x, throughInfo.Seed.y, 4, new Rgb(70, 225, 225));
                }
                // In-progress pick (this mode only) drawn on top, in magenta,
                // so it's obviously distinct from already-saved straits.
                if (_straitFrom.HasValue && lookup.TryGetValue(_straitFrom.Value, out var fromPick))
                    DrawRing(buf, rect, fromPick.Seed.x, fromPick.Seed.y, 6, new Rgb(255, 60, 220), 2);
                if (_straitThrough.HasValue && lookup.TryGetValue(_straitThrough.Value, out var throughPick))
                    DrawRing(buf, rect, throughPick.Seed.x, throughPick.Seed.y, 6, new Rgb(255, 60, 220), 2);
                break;

            case "names":
                foreach (var color in p.Metadata.ProvinceNames.Keys)
                {
                    if (!lookup.TryGetValue(color, out var info)) continue;
                    DrawRing(buf, rect, info.Seed.x, info.Seed.y, 4, new Rgb(255, 255, 255), 2);
                }
                break;

            default: // modifiers
                foreach (var kv in p.Metadata.Modifiers)
                {
                    var c = ModifierColor(kv.Key);
                    foreach (var color in kv.Value)
                    {
                        if (!lookup.TryGetValue(color, out var info)) continue;
                        var icon = ModifierIcon(p, kv.Key, info);
                        if (icon != null) DrawIcon(buf, rect, info.Seed.x, info.Seed.y, icon, 20);
                        else DrawDisk(buf, rect, info.Seed.x, info.Seed.y, 4, c);
                    }
                }
                break;
        }
        return buf;
    }

    /// <summary>Real game icon for a modifier name (Runde 9 feedback item
    /// 7, plus the Runde 9 follow-up adding "important_natural_harbor"), or
    /// null to keep the original colored-disk marker - which now only
    /// happens because IconCatalog couldn't find/load the .dds file
    /// (missing icon file, e.g. a distributed copy of the app without the
    /// Icons folder, always degrades gracefully to the pre-Runde-9 look
    /// rather than failing to draw anything).
    /// "level_1_center_of_trade" has two icon variants in the game files
    /// (coastal/inland) - picked here via IsLikelyCoastal since
    /// ProvinceInfo itself doesn't record that.</summary>
    private static DecodedIcon? ModifierIcon(TileProject p, string modifierName, ProvinceInfo info) => modifierName switch
    {
        "river_estuary_modifier" => IconCatalog.TryGet("estuary_icon"),
        "paradise_modifier" => IconCatalog.TryGet("paradise"),
        "level_1_center_of_trade" => IconCatalog.TryGet(IsLikelyCoastal(p, info) ? "cot_coastal_1" : "cot_inland_1"),
        "important_natural_harbor" => IconCatalog.TryGet("important_natural_harbor"),
        _ => null,
    };

    /// <summary>Approximate "does this land province touch open water"
    /// check, used only to pick between the two center-of-trade icon
    /// variants - not a general-purpose coastal flag on ProvinceInfo
    /// itself (there isn't one), so this just samples a ring of points
    /// around the province's own seed and checks whether any of them
    /// belongs to a different, Sea/Lake-kind province. Cheap enough to run
    /// per marker per redraw since there are typically only a handful of
    /// centers of trade on any one tile.</summary>
    private static bool IsLikelyCoastal(TileProject p, ProvinceInfo info)
    {
        var result = p.ProvinceResult;
        if (result == null) return false;
        var labels = result.Labels;
        int w = labels.Width, h = labels.Height;
        const int radius = 24, step = 4;
        for (int dy = -radius; dy <= radius; dy += step)
        {
            int y = info.Seed.y + dy;
            if (y < 0 || y >= h) continue;
            for (int dx = -radius; dx <= radius; dx += step)
            {
                int x = info.Seed.x + dx;
                if (x < 0 || x >= w) continue;
                int pid = labels[x, y];
                if (pid < 0 || pid == info.ProvinceId) continue;
                if (result.Provinces.TryGetValue(pid, out var other) && (other.Kind == ProvinceKind.Sea || other.Kind == ProvinceKind.Lake))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Blits a DecodedIcon (see DdsIcon.cs), nearest-neighbor-
    /// scaled to targetSize x targetSize regardless of its native
    /// resolution, centered at full-image point (cx,cy), alpha-blended
    /// against whatever is already in buf using the icon's own per-pixel
    /// alpha channel - same crop-relative coordinate convention as
    /// DrawDisk/DrawRing/DrawLine above.</summary>
    private static void DrawIcon(byte[] buf, Int32Rect rect, int cx, int cy, DecodedIcon icon, int targetSize)
    {
        int w = rect.Width, h = rect.Height;
        int half = targetSize / 2;
        int lx0 = cx - rect.X - half, ly0 = cy - rect.Y - half;
        for (int py = 0; py < targetSize; py++)
        {
            int ly = ly0 + py;
            if (ly < 0 || ly >= h) continue;
            int sy = Math.Min(icon.Height - 1, py * icon.Height / targetSize);
            int rowBase = ly * w;
            int srcRow = sy * icon.Width;
            for (int px = 0; px < targetSize; px++)
            {
                int lx = lx0 + px;
                if (lx < 0 || lx >= w) continue;
                int sx = Math.Min(icon.Width - 1, px * icon.Width / targetSize);
                int so = (srcRow + sx) * 4;
                byte a = icon.Bgra[so + 3];
                if (a == 0) continue;
                double af = a / 255.0;
                int o = (rowBase + lx) * 3;
                buf[o] = (byte)Math.Clamp(buf[o] * (1 - af) + icon.Bgra[so + 2] * af, 0, 255);         // R
                buf[o + 1] = (byte)Math.Clamp(buf[o + 1] * (1 - af) + icon.Bgra[so + 1] * af, 0, 255); // G
                buf[o + 2] = (byte)Math.Clamp(buf[o + 2] * (1 - af) + icon.Bgra[so + 0] * af, 0, 255); // B
            }
        }
    }

    private static void DrawDisk(byte[] buf, Int32Rect rect, int cx, int cy, int radius, Rgb color)
    {
        int w = rect.Width, h = rect.Height;
        int lx0 = cx - rect.X, ly0 = cy - rect.Y;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int ly = ly0 + dy;
            if (ly < 0 || ly >= h) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                int lx = lx0 + dx;
                if (lx < 0 || lx >= w) continue;
                int o = (ly * w + lx) * 3;
                buf[o] = color.R; buf[o + 1] = color.G; buf[o + 2] = color.B;
            }
        }
    }

    private static void DrawRing(byte[] buf, Int32Rect rect, int cx, int cy, int radius, Rgb color, int thickness)
    {
        int w = rect.Width, h = rect.Height;
        int lx0 = cx - rect.X, ly0 = cy - rect.Y;
        int rOuter2 = radius * radius;
        int rInnerR = Math.Max(radius - thickness, 0);
        int rInner2 = rInnerR * rInnerR;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int ly = ly0 + dy;
            if (ly < 0 || ly >= h) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 > rOuter2 || d2 < rInner2) continue;
                int lx = lx0 + dx;
                if (lx < 0 || lx >= w) continue;
                int o = (ly * w + lx) * 3;
                buf[o] = color.R; buf[o + 1] = color.G; buf[o + 2] = color.B;
            }
        }
    }

    /// <summary>Thin line between two full-image points, stamped with small
    /// disks along a Bresenham walk - simplest way to get a visible stroke
    /// without any real drawing library.</summary>
    private static void DrawLine(byte[] buf, Int32Rect rect, int x0, int y0, int x1, int y1, Rgb color, int thickness)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            DrawDisk(buf, rect, x, y, thickness, color);
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    // -- list helpers (Runde 7: Shift-/Ctrl-click multi-select + "delete all") ---

    /// <summary>Plain-text list row wrapped in its own object identity, so a
    /// row can be found again via reference-equality <c>Items.IndexOf</c>
    /// even when two rows render identical text (e.g. two straits through
    /// the same province) and regardless of whether the ListBox has
    /// virtualized the row's container away.</summary>
    private sealed class Entry
    {
        public readonly string Text;
        public Entry(string text) => Text = text;
        public override string ToString() => Text;
    }

    /// <summary>Every selected row's position in <paramref name="lb"/>,
    /// highest index first so callers can RemoveAt each one from the
    /// parallel data list without earlier removals shifting later
    /// indices.</summary>
    private static List<int> SelectedIndicesDescending(ListBox lb)
    {
        var idx = new List<int>();
        foreach (var item in lb.SelectedItems)
        {
            int i = lb.Items.IndexOf(item);
            if (i >= 0) idx.Add(i);
        }
        idx.Sort((a, b) => b.CompareTo(a));
        return idx;
    }

    // -- straits list --------------------------------------------------------------

    private void BuildStraits(StackPanel body)
    {
        var f = Ui.Stack();
        _straitList = new ListBox { Height = 80, SelectionMode = SelectionMode.Extended };
        f.Children.Add(_straitList);
        var deleteAll = Ui.Button(Loc.T("common.removeAll"), (_, _) => RemoveAllStraits());
        deleteAll.Margin = new Thickness(6, 4, 0, 0);
        f.Children.Add(Ui.Horizontal(Ui.Button(Loc.T("common.removeSelected"), (_, _) => RemoveStrait()), deleteAll));

        f.Children.Add(Ui.Sep());
        f.Children.Add(Ui.Bold(Loc.T("specialFeatures.generate"), 11));
        Ui.LabeledSlider(f, Loc.T("random.straitFrequency"), 0, 1, _straitGenFrequency, (_, e) => { _straitGenFrequency = e.NewValue; GenerationPrefs.Set("random.straitFrequency", _straitGenFrequency); }, Loc.T("random.straitFrequency.tip"));
        Ui.LabeledSliderWithValue(f, Loc.T("random.straitMaxDistance"), 20, 1000, _straitGenMaxDistance, v => { _straitGenMaxDistance = v; GenerationPrefs.Set("random.straitMaxDistance", _straitGenMaxDistance); }, Loc.T("random.straitMaxDistance.tip"));
        Ui.LabeledSliderWithValue(f, Loc.T("random.straitMinSpacing"), 0, 200, _straitGenMinSpacing, v => { _straitGenMinSpacing = v; GenerationPrefs.Set("random.straitMinSpacing", _straitGenMinSpacing); }, Loc.T("random.straitMinSpacing.tip"));
        Ui.LabeledSliderWithValue(f, Loc.T("random.straitMaxPerIsland"), 1, 10, _straitGenMaxPerIsland, v => { _straitGenMaxPerIsland = v; GenerationPrefs.Set("random.straitMaxPerIsland", _straitGenMaxPerIsland); }, Loc.T("random.straitMaxPerIsland.tip"));
        f.Children.Add(Ui.Button(Loc.T("specialFeatures.generateStraits"), (_, _) => GenerateStraits()));

        body.Children.Add(Ui.Group(Loc.T("specialFeatures.straits"), f));
        RefreshStraits();
    }

    /// <summary>Shared guard for all three standalone generate buttons -
    /// they all require an already-generated province layout, exactly like
    /// AutoGenerateNaturalStraits/AutoGenerateModifiers/AutoGenerateRegions
    /// themselves silently no-op without one.</summary>
    private bool RequireProvinces()
    {
        if (_app.Project!.ProvinceResult != null) return true;
        Dialogs.Info(_app, Loc.T("specialFeatures.title"), Loc.T("specialFeatures.noProvinces"));
        return false;
    }

    private void GenerateStraits()
    {
        if (!RequireProvinces()) return;
        var p = _app.Project!;
        var options = new TileProject.RandomTileOptions
        {
            StraitFrequency = _straitGenFrequency,
            StraitMaxDistancePx = _straitGenMaxDistance,
            StraitMinSpacingPx = _straitGenMinSpacing,
            StraitMaxPerIsland = (int)Math.Round(_straitGenMaxPerIsland),
        };
        _app.Undo?.SnapshotBeforeChange();
        p.AutoGenerateNaturalStraits(options, new Random());
        RefreshStraits();
        _canvas.RequestRedraw();
    }

    private void RefreshStraits()
    {
        _straitList.Items.Clear();
        foreach (var s in _app.Project!.Metadata.Straits)
            _straitList.Items.Add(new Entry($"{s.From} -> {s.To} through {s.Through}"));
    }

    private void RemoveStrait()
    {
        var indices = SelectedIndicesDescending(_straitList);
        if (indices.Count == 0) return;
        var straits = _app.Project!.Metadata.Straits;
        foreach (int idx in indices) straits.RemoveAt(idx);
        RefreshStraits();
        _canvas.RequestRedraw();
    }

    private void RemoveAllStraits()
    {
        if (_straitList.Items.Count == 0) return;
        _app.Project!.Metadata.Straits.Clear();
        RefreshStraits();
        _canvas.RequestRedraw();
    }

    // -- regions list --------------------------------------------------------------

    private void BuildRegions(StackPanel body)
    {
        var f = Ui.Stack();
        _regionList = new ListBox { Height = 80, SelectionMode = SelectionMode.Extended };
        f.Children.Add(_regionList);
        var deleteAll = Ui.Button(Loc.T("common.removeAll"), (_, _) => RemoveAllRegions());
        deleteAll.Margin = new Thickness(6, 4, 0, 0);
        f.Children.Add(Ui.Horizontal(Ui.Button(Loc.T("common.removeSelected"), (_, _) => RemoveRegion()), deleteAll));

        f.Children.Add(Ui.Sep());
        f.Children.Add(Ui.Bold(Loc.T("specialFeatures.generate"), 11));
        Ui.LabeledSlider(f, Loc.T("random.autoRegionCount"), 0, 20, _regionGenCount, (_, e) => { _regionGenCount = e.NewValue; GenerationPrefs.Set("random.autoRegionCount", _regionGenCount); }, Loc.T("random.autoRegionCount.tip"));
        f.Children.Add(Ui.Button(Loc.T("specialFeatures.generateRegions"), (_, _) => GenerateRegions()));

        body.Children.Add(Ui.Group(Loc.T("specialFeatures.regions"), f));
        RefreshRegions();
    }

    private void GenerateRegions()
    {
        if (!RequireProvinces()) return;
        _app.Undo?.SnapshotBeforeChange();
        _app.Project!.AutoGenerateRegions((int)Math.Round(_regionGenCount), new Random());
        RefreshRegions();
        _canvas.RequestRedraw();
    }

    private void RefreshRegions()
    {
        _regionList.Items.Clear();
        foreach (var c in _app.Project!.Metadata.Regions)
            _regionList.Items.Add(new Entry(c.ToString()));
    }

    private void RemoveRegion()
    {
        var indices = SelectedIndicesDescending(_regionList);
        if (indices.Count == 0) return;
        var regions = _app.Project!.Metadata.Regions;
        foreach (int idx in indices) regions.RemoveAt(idx);
        RefreshRegions();
        _canvas.RequestRedraw();
    }

    private void RemoveAllRegions()
    {
        if (_regionList.Items.Count == 0) return;
        _app.Project!.Metadata.Regions.Clear();
        RefreshRegions();
        _canvas.RequestRedraw();
    }

    // -- province names ---------------------------------------------------------

    private void BuildNames(StackPanel body)
    {
        var f = Ui.Stack();
        _nameList = new ListBox { Height = 80, SelectionMode = SelectionMode.Extended };
        f.Children.Add(_nameList);
        var deleteAll = Ui.Button(Loc.T("common.removeAll"), (_, _) => RemoveAllNames());
        deleteAll.Margin = new Thickness(6, 4, 0, 0);
        f.Children.Add(Ui.Horizontal(Ui.Button(Loc.T("common.removeSelected"), (_, _) => RemoveName()), deleteAll));
        body.Children.Add(Ui.Group(Loc.T("specialFeatures.names"), f));
        RefreshNames();
    }

    private void RefreshNames()
    {
        _nameList.Items.Clear();
        foreach (var kv in _app.Project!.Metadata.ProvinceNames)
            _nameList.Items.Add(new Entry($"\"{kv.Value}\" -> {kv.Key}"));
    }

    private void RemoveName()
    {
        var indices = SelectedIndicesDescending(_nameList);
        if (indices.Count == 0) return;
        var names = _app.Project!.Metadata.ProvinceNames;
        var keys = names.Keys.ToList();
        foreach (int idx in indices) names.Remove(keys[idx]);
        RefreshNames();
        _canvas.RequestRedraw();
    }

    private void RemoveAllNames()
    {
        if (_nameList.Items.Count == 0) return;
        _app.Project!.Metadata.ProvinceNames.Clear();
        RefreshNames();
        _canvas.RequestRedraw();
    }

    // -- modifiers --------------------------------------------------------------

    private void BuildModifiers(StackPanel body)
    {
        var f = Ui.Stack();
        _modList = new ListBox { Height = 100, SelectionMode = SelectionMode.Extended };
        f.Children.Add(_modList);
        var deleteAll = Ui.Button(Loc.T("common.removeAll"), (_, _) => RemoveAllModifiers());
        deleteAll.Margin = new Thickness(6, 4, 0, 0);
        f.Children.Add(Ui.Horizontal(Ui.Button(Loc.T("common.removeSelected"), (_, _) => RemoveModifier()), deleteAll));

        f.Children.Add(Ui.Sep());
        f.Children.Add(Ui.Bold(Loc.T("specialFeatures.generate"), 11));
        Ui.LabeledSlider(f, Loc.T("random.modifierFrequency"), 0, 1, _modifierGenFrequency, (_, e) => { _modifierGenFrequency = e.NewValue; GenerationPrefs.Set("random.modifierFrequency", _modifierGenFrequency); }, Loc.T("random.modifierFrequency.tip"));
        f.Children.Add(Ui.Button(Loc.T("specialFeatures.generateModifiers"), (_, _) => GenerateModifiers()));

        body.Children.Add(Ui.Group(Loc.T("specialFeatures.modifiers"), f));
        RefreshModifiers();
    }

    private void GenerateModifiers()
    {
        if (!RequireProvinces()) return;
        _app.Undo?.SnapshotBeforeChange();
        _app.Project!.AutoGenerateModifiers(_modifierGenFrequency, new Random());
        RefreshModifiers();
        _canvas.RequestRedraw();
    }

    private void RefreshModifiers()
    {
        _modList.Items.Clear();
        _modIndex.Clear();
        foreach (var kv in _app.Project!.Metadata.Modifiers)
        {
            foreach (var color in kv.Value)
            {
                _modList.Items.Add(new Entry($"{kv.Key} -> {color}"));
                _modIndex.Add((kv.Key, color));
            }
        }
    }

    private void RemoveModifier()
    {
        var indices = SelectedIndicesDescending(_modList);
        if (indices.Count == 0) return;
        var m = _app.Project!.Metadata;
        foreach (int idx in indices)
        {
            var (name, color) = _modIndex[idx];
            if (m.Modifiers.TryGetValue(name, out var list))
            {
                list.Remove(color);
                if (list.Count == 0) m.Modifiers.Remove(name);
            }
        }
        RefreshModifiers();
        _canvas.RequestRedraw();
    }

    private void RemoveAllModifiers()
    {
        if (_modList.Items.Count == 0) return;
        _app.Project!.Metadata.Modifiers.Clear();
        RefreshModifiers();
        _canvas.RequestRedraw();
    }
}
