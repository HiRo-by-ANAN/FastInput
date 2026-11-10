using System.Runtime.InteropServices;

namespace FastInput.Windows.Realtime;

// No key-code parameter is accepted anywhere in the Enter path.
public interface IVirtualEnterInputApi
{
    bool FinalCheck(TargetCandidate target);
    AdapterOutcome SendSingleEnter(TargetCandidate target, Func<bool> mayApply);
}

public sealed class VirtualEnterSender(IVirtualEnterInputApi api)
{
    public AdapterOutcome Send(TargetCandidate target, Func<bool> mayApply, CancellationToken token)
    {
        try
        {
            bool Allowed() => !token.IsCancellationRequested && mayApply();
            if (!Allowed() || !api.FinalCheck(target) || !Allowed()) return AdapterOutcome.Rejected;
            // The implementation repeats authorization, modifiers and foreground immediately before SendInput.
            return api.SendSingleEnter(target, Allowed);
        }
        catch (Exception) { return AdapterOutcome.Unknown; }
    }
}

internal sealed class WindowsVirtualEnterInput : IVirtualEnterInputApi
{
    public bool FinalCheck(TargetCandidate target)
    {
        if (!NativeInput.ModifiersReleased() || !DesktopNative.FocusMatches(target)
            || SafetyPolicy.Check(target, new WindowsSafetyProbe().Inspect(target)) is not null) return false;
        if (target.AdapterKind == "chromium") return ChromiumUiaAdapter.EnterTargetSafe(target);
        return target.AdapterKind == "win32";
    }

    public AdapterOutcome SendSingleEnter(TargetCandidate target, Func<bool> mayApply)
    {
        var events = NativeInput.Enter();
        if (!FinalCheck(target) || !mayApply() || !NativeInput.ModifiersReleased() || !DesktopNative.FocusMatches(target))
            return AdapterOutcome.Rejected;
        // Exactly one batch: VK_RETURN down + up, with our existing injected-input marker. Never retry.
        var count = NativeInput.SendInput(2, events, Marshal.SizeOf<NativeInput.Input>());
        return count == 2 ? AdapterOutcome.Completed : AdapterOutcome.Unknown;
    }
}
