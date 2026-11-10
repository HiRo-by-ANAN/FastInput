namespace FastInput.Windows.Realtime;

public sealed class FakeAdapter : ITargetAdapter, IVirtualEnterAdapter
{
    public string Text { get; set; } = "";
    public int SelectionStart { get; set; }
    public int SelectionEnd { get; set; }
    public int DelayMilliseconds { get; set; }
    public bool IgnoreCancellation { get; set; }
    // Models F2: a dispatch already accepted by another process may finish after our deadline.
    public bool DispatchBeforeDelay { get; set; }
    public bool MismatchReadBack { get; set; }
    public bool HasFocus { get; set; } = true;
    public bool Supported { get; set; } = true;
    public AdapterOutcome Outcome { get; set; } = AdapterOutcome.Completed;
    public Action? BeforeApply { get; set; }
    public int ApplyCount { get; private set; }
    public List<MinimalEdit> Edits { get; } = [];
    public int EnterCount { get; private set; }
    public int ReadCount { get; private set; }
    public Action? BeforeEnter { get; set; }
    public async Task<AdapterOutcome> PressEnterAsync(TargetCandidate target, Func<bool> mayApply, CancellationToken cancellationToken)
    {
        var dispatched = DispatchBeforeDelay && !cancellationToken.IsCancellationRequested && HasFocus && Supported && mayApply();
        if (DispatchBeforeDelay && !dispatched) return AdapterOutcome.Rejected;
        if (dispatched) EnterCount++;
        if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, IgnoreCancellation ? CancellationToken.None : cancellationToken);
        if (dispatched) return Outcome;
        BeforeEnter?.Invoke();
        if (cancellationToken.IsCancellationRequested || !HasFocus || !Supported || !mayApply()) return AdapterOutcome.Rejected;
        if (Outcome == AdapterOutcome.Rejected) return Outcome;
        EnterCount++;
        return Outcome;
    }
    public AdapterProbe Probe(TargetCandidate target) => new(Supported, "fake");
    public Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken cancellationToken) => ReadBackAsync(target, cancellationToken);
    public Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult(new TargetReadBack(MismatchReadBack && ApplyCount > 0 ? Text + "!" : Text, SelectionStart, SelectionEnd));
    }
    public async Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit,
        TextSelection selection, Func<bool> mayApply, CancellationToken cancellationToken)
    {
        var dispatched = DispatchBeforeDelay && HasFocus && mayApply() && !cancellationToken.IsCancellationRequested;
        if (DispatchBeforeDelay && !dispatched) return AdapterOutcome.Rejected;
        if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, IgnoreCancellation ? CancellationToken.None : cancellationToken);
        BeforeApply?.Invoke();
        if (!dispatched && (!HasFocus || !mayApply() || (!IgnoreCancellation && cancellationToken.IsCancellationRequested)))
            return AdapterOutcome.Rejected;
        if (Outcome is AdapterOutcome.Rejected or AdapterOutcome.Unknown) return Outcome;
        Text = Text.Remove(edit.Start, edit.DeleteLength).Insert(edit.Start, edit.InsertText);
        SelectionStart = selection.Start;
        SelectionEnd = selection.End;
        Edits.Add(edit);
        ApplyCount++;
        return Outcome;
    }
}
