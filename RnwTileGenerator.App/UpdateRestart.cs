using System;

namespace RnwTileGenerator.App;

/// <summary>What the user picked in the "update ready" dialog.</summary>
public enum UpdateRestartChoice { SaveAndRestart, RestartWithoutSaving, Cancel }

/// <summary>What happens next: nothing, or start the update mode without a project, with the just-saved project, or
/// with the project file in its last saved state (unsaved changes are dropped).</summary>
public enum UpdateRestartAction { Abort, LaunchWithoutProject, LaunchWithProject, LaunchWithLastSaved }

/// <summary>
/// Decision before restarting for an update. RNW has no recovery data, so the only way to keep the current
/// tile is to save it first; a cancelled or failed save stops the update and the program stays open.
/// </summary>
public static class UpdateRestart
{
    /// <param name="lastSavedPath">File the open project was last saved to or loaded from; null for a never saved tile.</param>
    /// <param name="save">Saves the project; returns its path, or null when the user cancelled or saving failed.</param>
    public static UpdateRestartAction Decide(UpdateRestartChoice choice, bool hasProject, string? lastSavedPath, Func<string?> save)
    {
        if (choice == UpdateRestartChoice.Cancel) return UpdateRestartAction.Abort;
        if (!hasProject) return UpdateRestartAction.LaunchWithoutProject;
        if (choice == UpdateRestartChoice.RestartWithoutSaving)
            return lastSavedPath is null ? UpdateRestartAction.LaunchWithoutProject : UpdateRestartAction.LaunchWithLastSaved;
        return save() is null ? UpdateRestartAction.Abort : UpdateRestartAction.LaunchWithProject;
    }
}
