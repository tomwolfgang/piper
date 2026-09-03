namespace Piper.Core.Sessions;

/// <summary>Which drop target the pointer is over, as resolved by the shell's hit-testing.</summary>
public enum SessionDropZone
{
    /// <summary>Nowhere that takes a dragged session.</summary>
    None,

    /// <summary>The Composer tab, either its strip button or its page body.</summary>
    Composer,

    /// <summary>The AutoResponder tab, either its strip button or its page body.</summary>
    AutoResponder,
}

/// <summary>What a drop should do with the payload it carries.</summary>
public enum DragDropAction
{
    /// <summary>Leave the drag alone. The caller must not touch the drag effect, so that an
    /// inner control which already claimed the payload keeps its own decision.</summary>
    Ignore,

    ImportSazFiles,
    LoadIntoComposer,
    AddAutoResponderRule,
}

/// <summary>
/// Decides what a drop means from the payload and the zone under the pointer. UI-free so the
/// decision can be tested without a WinForms message loop, the same reason
/// <see cref="HostFilterTerm"/> lives here.
/// </summary>
/// <remarks>
/// The window makes every control a drop target so a SAZ file can be dropped anywhere, and in
/// WinForms the innermost target under the pointer wins. A handler that recognised only files
/// therefore swallowed dragged sessions on every control it was attached to, which is why a
/// session could not be dropped on the Composer body. One decision over both payload kinds
/// replaces two handler sets racing over the same tree.
/// </remarks>
public static class DragDropRouting
{
    public static DragDropAction Resolve(int sazFileCount, Session? dragged, SessionDropZone zone)
    {
        // Files win: a SAZ drop is unambiguous and works over the whole window.
        if (sazFileCount > 0) return DragDropAction.ImportSazFiles;

        // A session with no captured request has nothing to load or to match a rule against.
        if (dragged?.Request is null) return DragDropAction.Ignore;

        return zone switch
        {
            SessionDropZone.Composer => DragDropAction.LoadIntoComposer,
            SessionDropZone.AutoResponder => DragDropAction.AddAutoResponderRule,
            _ => DragDropAction.Ignore,
        };
    }
}
