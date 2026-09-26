using System;

namespace RnwTileGenerator.App;

/// <summary>What the user picked in the "update ready" dialog.</summary>
public enum UpdateRestartChoice { SaveAndRestart, RestartWithoutSaving, Cancel }

/// <summary>What happens next: nothing, or start the update mode with or without a project to reopen.</summary>
public enum UpdateRestartAction { Abort, LaunchWithoutProject, LaunchWithProject }

/// <summary>
/// Decision before restarting for an update. RNW has no recovery data, so the only way to keep the current
/// tile is to save it first; a cancelled or failed save stops the update and the program stays open.
/// </summary>
public static class UpdateRestart
{
    /// <param name="save">Saves the project; returns its path, or null when the user cancelled or saving failed.</param>
    public static UpdateRestartAction Decide(UpdateRestartChoice choice, bool hasProject, Func<string?> save)
    {
        if (choice == UpdateRestartChoice.Cancel) return UpdateRestartAction.Abort;
        if (!hasProject || choice == UpdateRestartChoice.RestartWithoutSaving) return UpdateRestartAction.LaunchWithoutProject;
        return save() is null ? UpdateRestartAction.Abort : UpdateRestartAction.LaunchWithProject;
    }
}
