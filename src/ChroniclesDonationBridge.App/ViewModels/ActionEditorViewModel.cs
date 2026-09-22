namespace ChroniclesDonationBridge.App.ViewModels;

public sealed class ActionEditorViewModel(ActionViewModel source, Func<ActionViewModel, Task> save) : ObservableObject
{
    private bool _isSaving;
    private string _error = string.Empty;
    public ActionViewModel Draft { get; } = source.CreateEditorDraft();
    public bool IsSaving { get => _isSaving; private set => SetProperty(ref _isSaving, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public async Task<bool> SaveAsync()
    {
        if (IsSaving) return false;
        Error = string.Empty;
        if (!Draft.TryCommitPendingEdits(out var error)) { Error = error; return false; }
        IsSaving = true;
        try { await save(Draft); return true; }
        catch (Exception exception) { Error = exception.Message; return false; }
        finally { IsSaving = false; }
    }
}
