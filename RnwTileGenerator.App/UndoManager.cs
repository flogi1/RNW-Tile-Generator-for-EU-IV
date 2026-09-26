using System;
using System.Collections.Generic;
using System.IO;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// Whole-project undo/redo: every undoable action snapshots the ENTIRE
/// TileProject state (via TileProject.SaveTo/LoadFrom's existing zip
/// serialization, into an in-memory MemoryStream - no disk I/O) before it
/// runs, and undo/redo simply swap the live project for a previous/later
/// snapshot. This replaces the earlier per-stage, single-slot "remember
/// just the one array this brush stroke touched" approach: that pattern
/// could only ever undo a brush stroke, never a whole-state operation like
/// "Grenzen einfärben" (rebuild provinces from borders) or a Generate
/// button, which is exactly the bug this was built to fix. Snapshot depth
/// is unlimited for the lifetime of one tile-editing session (bounded only
/// by available memory, not an artificial cap) - starting a different
/// tile (New/Open) resets the history, since undoing "past" a freshly
/// loaded/created project would be meaningless.
///
/// Callers are responsible for calling SnapshotBeforeChange() once per
/// discrete user action - at the start of a brush stroke (mouse-down, not
/// every drag point) or immediately before a Generate/Rebuild-style
/// button's work - never on every intermediate drag/hover event, or every
/// pixel dragged would become its own undo step.
/// </summary>
public sealed class UndoManager
{
    private readonly MainWindow _app;
    private readonly List<byte[]> _undoStack = new();
    private readonly List<byte[]> _redoStack = new();

    /// <summary>Fired after a snapshot is taken, an undo, a redo, or a
    /// history reset - stages/toolbar bind to this to refresh
    /// enabled-state and redraw.</summary>
    public event Action? Changed;

    public UndoManager(MainWindow app) => _app = app;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Call before starting a new tile (New/Open/.../random
    /// generate-from-nothing) so undo can never reach past the point a
    /// different project started being edited.</summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        Changed?.Invoke();
    }

    /// <summary>Snapshot the current project state onto the undo stack and
    /// clear the redo stack (a fresh action invalidates whatever redo
    /// history existed) - call this immediately BEFORE mutating the
    /// project for one discrete user action.</summary>
    public void SnapshotBeforeChange()
    {
        if (_app.Project == null) return;
        _undoStack.Add(Serialize(_app.Project));
        _redoStack.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_app.Project == null || _undoStack.Count == 0) return;
        _redoStack.Add(Serialize(_app.Project));
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _app.ReplaceProjectInPlace(Deserialize(snap));
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_app.Project == null || _redoStack.Count == 0) return;
        _undoStack.Add(Serialize(_app.Project));
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _app.ReplaceProjectInPlace(Deserialize(snap));
        Changed?.Invoke();
    }

    private static byte[] Serialize(TileProject p)
    {
        using var ms = new MemoryStream();
        p.SaveTo(ms);
        return ms.ToArray();
    }

    private static TileProject Deserialize(byte[] snap)
    {
        using var ms = new MemoryStream(snap);
        return TileProject.LoadFrom(ms);
    }
}
