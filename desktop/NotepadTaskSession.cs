namespace MSGuide.Desktop;

// The adapter owns native checks. Test doubles cannot be selected from the UI.
internal interface INotepadEditor
{
    WindowChoice Window { get; }
    void Validate();
    int Length();
    string Read();
    void Write(string text);
    (Native.RECT Bounds, double[] Box) Target();
}

internal sealed class NotepadTaskSession
{
    private readonly INotepadEditor editor;
    private readonly Func<DateTimeOffset> clock;
    private readonly DateTimeOffset expires;
    private string draft;
    private bool approved, stopped, attempted;
    internal bool Finished { get; private set; }
    internal InteractionMode Mode { get; }
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    internal WindowChoice Window => editor.Window;
    internal string Plan => $"Target: {Window.Title}\nInsert exactly this draft into the empty Notepad editor:\n\n{draft}\n\nNo Save, send, file open, clipboard, keyboard or mouse commands. Local text verification only; no screenshots or uploads. Notepad itself may retain unsaved tabs/session backups. Use synthetic text. Approval expires 60 seconds after preparation.";

    internal NotepadTaskSession(INotepadEditor editor, InteractionMode mode, string draft, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(draft) || draft.Length > 1000 || draft.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("Enter 1–1000 characters of synthetic draft text; unsupported control characters are rejected.");
        this.editor = editor;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        expires = this.clock().AddSeconds(60);
        this.draft = Normalize(draft);
        if (this.draft.Length > 1000) throw new InvalidOperationException("Draft exceeds 1000 characters after Windows newline normalization.");
        Mode = mode;
        Validate();
        if (editor.Length() != 0) throw new InvalidOperationException("Notepad is not empty. Nothing was read or overwritten. Select a new blank document.");
    }

    internal void Validate()
    {
        if (stopped || clock() >= expires) throw new InvalidOperationException("Task stopped or expired. Prepare and approve a new plan.");
        editor.Validate();
    }

    internal void Approve()
    {
        try
        {
            Validate();
            if (approved || editor.Length() != 0) throw new InvalidOperationException("Editor changed or approval was already used. Prepare a new task.");
            approved = true;
        }
        catch { Stop(); throw; }
    }

    internal (Native.RECT Bounds, double[] Box)? Observe()
    {
        try
        {
            Validate();
            if (!approved) throw new InvalidOperationException("Approve this task first.");
            if (Mode == InteractionMode.Guide)
            {
                // Only a deliberate Check reads text; the background timer checks identity/focus/bounds only.
                if (editor.Length() > 1000) throw new InvalidOperationException("Editor contains unexpected text. Task stopped.");
                Finished = Normalize(editor.Read()) == draft;
            }
            else if (!attempted && editor.Length() != 0)
                throw new InvalidOperationException("Editor changed. No text was overwritten.");
            return Finished ? null : editor.Target();
        }
        catch { Stop(); throw; }
    }

    internal void Execute()
    {
        try
        {
            Validate();
            if (Mode != InteractionMode.Control || !approved || attempted || Finished)
                throw new InvalidOperationException("No authority to insert text.");
            if (editor.Length() != 0) throw new InvalidOperationException("Editor changed. No text was overwritten.");
            attempted = true; // A timeout is an unknown outcome, never permission to retry.
            editor.Write(draft);
            Validate();
            if (editor.Length() > 1000 || Normalize(editor.Read()) != draft)
                throw new InvalidOperationException("Text outcome could not be verified. No retry; inspect Notepad manually.");
            Finished = true;
        }
        catch { Stop(); throw; }
    }

    internal void Stop() { stopped = true; approved = false; draft = ""; }
}