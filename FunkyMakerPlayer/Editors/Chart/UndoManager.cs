using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Undo/Redo system for chart editor
/// </summary>
public class UndoManager
{
    private Stack<IEditorAction> _undoStack = new();
    private Stack<IEditorAction> _redoStack = new();
    private int _savedStateCount = 0; // Tracks undo stack count at last save
    
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public bool IsModified => _undoStack.Count != _savedStateCount;
    
    public void Do(IEditorAction action)
    {
        action.Execute();
        _undoStack.Push(action);
        _redoStack.Clear();
    }
    
    public void Undo()
    {
        if (!CanUndo) return;
        
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
    }
    
    public void Redo()
    {
        if (!CanRedo) return;
        
        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);
    }
    
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _savedStateCount = 0;
    }
    
    public void MarkAsSaved()
    {
        _savedStateCount = _undoStack.Count;
    }
}

public interface IEditorAction
{
    void Execute();
    void Undo();
}

public class PlaceNoteAction : IEditorAction
{
    private readonly NoteGrid _grid;
    private readonly FnmmNote _note;
    private readonly int _lane;
    
    public PlaceNoteAction(NoteGrid grid, FnmmNote note, int lane)
    {
        _grid = grid;
        _note = note;
        _lane = lane;
    }
    
    public void Execute()
    {
        // Note is already added by PlaceNote, this is just for redo
        if (!_grid.GetAllNotes().Contains(_note))
            _grid.PlaceNote(_lane, _note.Start);
    }
    
    public void Undo()
    {
        _grid.DeleteNote(_note);
    }
}

public class DeleteNoteAction : IEditorAction
{
    private readonly NoteGrid _grid;
    private readonly FnmmNote _note;
    private readonly int _lane;
    
    public DeleteNoteAction(NoteGrid grid, FnmmNote note, int lane)
    {
        _grid = grid;
        _note = note;
        _lane = lane;
    }
    
    public void Execute()
    {
        _grid.DeleteNote(_note);
    }
    
    public void Undo()
    {
        if (!_grid.GetAllNotes().Contains(_note))
            _grid.PlaceNote(_lane, _note.Start);
    }
}

public class DeleteMultipleNotesAction : IEditorAction
{
    private readonly NoteGrid _grid;
    private readonly List<(FnmmNote note, int lane)> _notesWithLanes;
    
    public DeleteMultipleNotesAction(NoteGrid grid, List<FnmmNote> notes)
    {
        _grid = grid;
        _notesWithLanes = new List<(FnmmNote, int)>();
        
        // Store notes with their lane info (we'll need to infer lanes for undo)
        foreach (var note in notes)
        {
            // For simplicity, store with lane 0 (we'd need more context to track actual lanes)
            _notesWithLanes.Add((note, 0));
        }
    }
    
    public void Execute()
    {
        foreach (var (note, _) in _notesWithLanes)
            _grid.DeleteNote(note);
    }
    
    public void Undo()
    {
        foreach (var (note, lane) in _notesWithLanes)
        {
            if (!_grid.GetAllNotes().Contains(note))
                _grid.PlaceNote(lane, note.Start);
        }
    }
}
