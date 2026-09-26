using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

public enum UpdateNoticeKind { Available, Applied, Failed, Cancelled }

/// <summary>
/// What the update bar shows. Kept as kind + version (not as finished text) so the bar can rebuild its text after a
/// language switch.
/// </summary>
public sealed record UpdateNotice(UpdateNoticeKind Kind, AppVersion Version)
{
    /// <summary>"Update now", "Release page" and "Skip this version" only make sense for an available version.</summary>
    public bool ShowsUpdateButtons => Kind == UpdateNoticeKind.Available;

    /// <summary>After a failed update the bar offers to open update.log.</summary>
    public bool ShowsLogButton => Kind == UpdateNoticeKind.Failed;

    public string Text(AppVersion installed) => Kind switch
    {
        UpdateNoticeKind.Available => UpdateTexts.BarAvailable(Version, installed),
        UpdateNoticeKind.Applied => UpdateTexts.BarUpdated(Version),
        UpdateNoticeKind.Failed => UpdateTexts.BarFailed(Version),
        _ => UpdateTexts.BarCancelled(Version),
    };
}
