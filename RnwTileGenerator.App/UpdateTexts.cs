using System.Globalization;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// All texts of the self-update, looked up through Loc (keys "update.*", registered in
/// LocalizationStrings.Updates.cs). Ported from PMT's UpdateTexts.
/// </summary>
internal static class UpdateTexts
{
    // Update bar
    public static string BarAvailable(AppVersion available, AppVersion installed) => F("update.bar.available", available, installed);

    public static string BarUpdated(AppVersion version) => F("update.bar.updated", version);

    public static string BarFailed(AppVersion version) => F("update.bar.failed", version);

    public static string BarCancelled(AppVersion version) => F("update.bar.cancelled", version);

    public static string BarOpenLog => Loc.T("update.bar.openLog");

    public static string BarOpenLogTip => Loc.T("update.bar.openLog.tip");

    public static string BarInstall => Loc.T("update.bar.install");

    public static string BarInstallTip => Loc.T("update.bar.install.tip");

    public static string BarReleasePage => Loc.T("update.bar.releasePage");

    public static string BarReleasePageTip => Loc.T("update.bar.releasePage.tip");

    public static string BarSkip => Loc.T("update.bar.skip");

    public static string BarLaterTip => Loc.T("update.bar.later.tip");

    // Check
    public static string CheckTitle => Loc.T("update.check.title");

    public static string AvailableTitle => Loc.T("update.available.title");

    public static string AvailableQuestion(AppVersion available, AppVersion installed) => F("update.available.question", available, installed);

    public static string UpToDate(AppVersion installed) => F("update.check.upToDate", installed);

    public static string NoRelease => Loc.T("update.check.noRelease");

    public static string CheckFailed(string? error) => F("update.check.failed", error);

    // Download
    public static string DownloadTitle => Loc.T("update.download.title");

    public static string DownloadHint => Loc.T("update.download.hint");

    public static string DownloadStatus(AppVersion version) => F("update.download.status", version);

    public static string DownloadCount(string text, int done, int total) => F("update.download.count", text, done, total);

    public static string Cancel => Loc.T("update.cancel");

    // Not possible
    public static string NotPossibleTitle => Loc.T("update.notPossible.title");

    public static string OfferReleasePage(string reason) => F("update.notPossible.offer", reason);

    public static string NoInstallList => Loc.T("update.notPossible.noInstallList");

    public static string ProtectedFolder(string folder) => F("update.notPossible.protectedFolder", folder);

    public static string AlreadyRunning => Loc.T("update.alreadyRunning");

    public static string LaunchFailed(string error, string logPath) => F("update.launchFailed", error, logPath);

    public static string Problem(UpdateProblem problem, string? detail) => problem switch
    {
        UpdateProblem.NoSignedPackage => Loc.T("update.problem.noSignedPackage"),
        UpdateProblem.BadSignature => Loc.T("update.problem.badSignature"),
        UpdateProblem.InvalidManifest => Loc.T("update.problem.invalidManifest"),
        UpdateProblem.VersionMismatch => Loc.T("update.problem.versionMismatch"),
        UpdateProblem.NotNewer => Loc.T("update.problem.notNewer"),
        UpdateProblem.ProtocolTooNew => Loc.T("update.problem.protocolTooNew"),
        UpdateProblem.AssetMissing => Loc.T("update.problem.assetMissing"),
        UpdateProblem.SizeMismatch => Loc.T("update.problem.sizeMismatch"),
        UpdateProblem.HashMismatch => Loc.T("update.problem.hashMismatch"),
        UpdateProblem.ExtractFailed => Loc.T("update.problem.extractFailed"),
        UpdateProblem.Network => F("update.problem.network", detail),
        _ => "",
    };

    // Ready
    public static string ReadyTitle => Loc.T("update.ready.title");

    public static string ReadyQuestion(AppVersion version) => F("update.ready.question", version);

    // Update mode
    public static string ApplyTitle => Loc.T("update.apply.title");

    public static string ApplyVersions(AppVersion from, AppVersion to) => F("update.apply.versions", from, to);

    public static string Phase(UpdateRunnerPhase phase, int waitingFor) => phase switch
    {
        UpdateRunnerPhase.Waiting => F("update.apply.phase.waiting", waitingFor),
        UpdateRunnerPhase.Swapping => Loc.T("update.apply.phase.swapping"),
        UpdateRunnerPhase.Starting => Loc.T("update.apply.phase.starting"),
        UpdateRunnerPhase.Checking => Loc.T("update.apply.phase.checking"),
        _ => Loc.T("update.apply.phase.rollingBack"),
    };

    public static string KeepWaitingTitle => Loc.T("update.apply.keepWaiting.title");

    public static string KeepWaiting(int count) => F("update.apply.keepWaiting", count);

    public static string RollbackFailed(string backupFolder, string logPath) => F("update.apply.rollbackFailed", backupFolder, logPath);

    public static string InvalidArguments => Loc.T("update.apply.invalidArguments");

    // Start after an update
    public static string StartFailedTitle => Loc.T("update.start.failed.title");

    public static string StartFailed(AppVersion version, string logPath) => F("update.start.failed", version, logPath);

    public static string StartCancelledTitle => Loc.T("update.start.cancelled.title");

    public static string StartCancelled(AppVersion version) => F("update.start.cancelled", version);

    // Browser
    public static string NotOpenedTitle => Loc.T("update.browser.notOpened.title");

    public static string NotGitHub => Loc.T("update.browser.notGitHub");

    public static string ErrorTitle => Loc.T("update.browser.error.title");

    public static string BrowserFailed(string error) => F("update.browser.failed", error);

    private static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Loc.T(key), args);
}
