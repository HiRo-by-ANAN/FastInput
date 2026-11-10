using System.Diagnostics;
using System.Text.Json;
using FastInput.Windows.Realtime;
namespace FastInput.LiveSim;
internal sealed class ObservedAdapter : ITargetAdapter, IVirtualEnterAdapter
{
    private readonly TargetAdapterRouter _inner = new();
    public AdapterProbe Probe(TargetCandidate target) => _inner.Probe(target);
    public Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken token) => _inner.BindAsync(target, token);
    public Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken token) => _inner.ReadBackAsync(target, token);
    public async Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit, TextSelection selection, Func<bool> mayApply, CancellationToken token)
    {
        var outcome = await _inner.ApplyMinimalEditAsync(target, edit, selection, mayApply, token);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "edit", start = edit.Start, deleted = edit.DeleteLength, inserted = edit.InsertText.Length, outcome = outcome.ToString(), ticks = Stopwatch.GetTimestamp() }));
        return outcome;
    }
    public async Task<AdapterOutcome> PressEnterAsync(TargetCandidate target, Func<bool> mayApply, CancellationToken token)
    {
        var outcome = await ((IVirtualEnterAdapter)_inner).PressEnterAsync(target, mayApply, token);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "enter_attempt", outcome = outcome.ToString(), ticks = Stopwatch.GetTimestamp() }));
        return outcome;
    }
}
