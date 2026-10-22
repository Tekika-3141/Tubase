using System.Windows.Input;

namespace Tubase;

public enum EditorCommand
{
    None,
    Save,
    Undo,
    Redo,
    TogglePlayback,
    DeleteSelection
}

public static class EditorInput
{
    public static EditorCommand Resolve(Key key, ModifierKeys modifiers, bool textEntryFocused)
    {
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (key == Key.S) return EditorCommand.Save;
            if (key == Key.Z && (modifiers & ModifierKeys.Shift) == 0) return EditorCommand.Undo;
            if (key == Key.Y || key == Key.Z && (modifiers & ModifierKeys.Shift) != 0) return EditorCommand.Redo;
        }
        if (key == Key.Delete && !textEntryFocused) return EditorCommand.DeleteSelection;
        return key == Key.Space && !textEntryFocused ? EditorCommand.TogglePlayback : EditorCommand.None;
    }
}
