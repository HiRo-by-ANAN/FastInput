using System.Text;

namespace FastInput.Windows.Realtime;

public static class TextSafety
{
    public static bool IsAllowed(string text)
    {
        if (text.Length > 4096) return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c <= 0x1f || c is >= '\u007f' and <= '\u009f' || c is '\u2028' or '\u2029' or '\u061c' or '\u200e' or '\u200f'
                || c is >= '\u202a' and <= '\u202e' || c is >= '\u2066' and <= '\u2069') return false;
            if (char.IsHighSurrogate(c)) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return Encoding.UTF8.GetByteCount(text) <= 16384;
    }

    public static bool IsBoundary(string text, int position) => position >= 0 && position <= text.Length
        && !(position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]));
}

public static class SafetyPolicy
{
    private static readonly HashSet<string> DeniedPrograms = new(StringComparer.OrdinalIgnoreCase)
    { "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "wt", "OpenConsole", "bash", "wsl", "mintty", "consent", "LogonUI", "winlogon", "FastInput", "fastinput-server" };

    public static string? Check(TargetCandidate target, SafetySnapshot snapshot)
    {
        if (snapshot.ProcessId != target.ProcessId || snapshot.ProcessStartTimeUtcTicks != target.ProcessStartTimeUtcTicks)
            return "target_changed";
        if (snapshot.PhysicalInputPending) return "physical_input";
        if (!snapshot.FocusMatches) return "focus_changed";
        if (snapshot.IsLocked || snapshot.IsSecureDesktop) return "locked";
        if (snapshot.HostElevated || snapshot.IntegrityLevel > snapshot.HostIntegrityLevel
            || snapshot.SessionId != snapshot.HostSessionId || snapshot.IsHostOrDescendant
            || snapshot.IsPassword || snapshot.IsReadOnly || !snapshot.IsEnabled || snapshot.IsRunDialog
            || !snapshot.Supported || DeniedPrograms.Contains(Path.GetFileNameWithoutExtension(snapshot.ProcessName))) return "unsupported";
        return null;
    }

    public static string Explain(TargetCandidate target, SafetySnapshot s)
    {
        if (s.ProcessId != target.ProcessId || s.ProcessStartTimeUtcTicks != target.ProcessStartTimeUtcTicks) return Loc.T("safety.target.process.identity.changed");
        if (s.PhysicalInputPending) return Loc.T("safety.local.keyboard.mouse.or.foreground.activity.detected.the.local");
        if (!s.FocusMatches) return Loc.T("safety.the.foreground.window.or.focus.changed.or.local.input");
        if (s.IsLocked || s.IsSecureDesktop) return Loc.T("safety.the.screen.is.locked.or.the.normal.input.desktop");
        if (s.HostElevated) return Loc.T("safety.fastinput.is.running.as.administrator.access.was.refused");
        if (s.IntegrityLevel > s.HostIntegrityLevel) return Loc.T("safety.the.target.has.a.higher.integrity.level.than.fastinput");
        if (s.SessionId != s.HostSessionId) return Loc.T("safety.the.target.is.in.a.different.windows.session");
        if (s.IsHostOrDescendant) return Loc.T("safety.fastinput.and.its.child.processes.cannot.be.targets");
        if (DeniedPrograms.Contains(Path.GetFileNameWithoutExtension(s.ProcessName))) return Loc.T("safety.terminals.uac.sign.in.screens.and.blocked.processes.are");
        if (s.IsPassword) return Loc.T("safety.password.fields.are.not.supported");
        if (s.IsReadOnly) return Loc.T("safety.read.only.fields.are.not.supported");
        if (!s.IsEnabled) return Loc.T("safety.disabled.fields.are.not.supported");
        if (s.IsRunDialog) return Loc.T("safety.the.run.dialog.and.other.system.dialogs.are.not");
        return target.UnsupportedReason ?? Loc.T("safety.cannot.verify.that.this.field.supports.safe.precise.editing");
    }

    public static string ProgramAlias(string name)
    {
        // External strings never enter the audit schema. Known programs use fixed short aliases.
        return Path.GetFileNameWithoutExtension(name).ToLowerInvariant() switch
        { "chrome" => "chrome", "msedge" => "edge", "fixture" => "fixture", _ => "other" };
    }
}
