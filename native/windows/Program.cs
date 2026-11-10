using FastInput.Windows.Realtime;
using System.Runtime.InteropServices;
using System.Text;

namespace FastInput.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Loc.SetLanguage(RealtimeSettings.Load().Language);
        var command = args.Where(arg => arg is "--enable-autostart" or "--disable-autostart" or "--autostart-status").ToArray();
        var smokeTest = args.Contains("--smoke-test");
        if (command.Length > 0 || smokeTest) ConnectConsole();
        if (command.Length > 0)
        {
            if (command.Length != 1 || args.Length != 1)
            {
                Console.WriteLine(Loc.T("program.autostart.error.use.one.startup.command.at.a.time"));
                return 1;
            }
            try
            {
                if (command[0] != "--autostart-status") Autostart.SetEnabled(command[0] == "--enable-autostart");
                Console.WriteLine(Autostart.IsEnabled() ? "AUTOSTART ENABLED" : "AUTOSTART DISABLED");
                return 0;
            }
            catch (Exception error) { Console.WriteLine($"AUTOSTART ERROR {error.Message}"); return 1; }
        }
        try { return RunWindow(args, smokeTest); }
        catch (Exception error)
        {
            if (smokeTest) Console.WriteLine($"SMOKE FAIL {error.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            else MessageBox.Show(Loc.T("program.cannot.start.fastinput", error.Message), "FastInput", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunWindow(string[] args, bool smokeTest)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            // A smoke test must actually load a page, so it cannot report success by activating another window.
            if (smokeTest) { Console.WriteLine(Loc.T("program.smoke.fail.fastinput.is.already.running.close.it.before")); return 1; }
            if (instance.ActivateExisting()) return 0;
            MessageBox.Show(Loc.T("program.cannot.contact.the.running.fastinput.instance.try.again.later"), "FastInput", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        var serverArguments = args.Where(arg => arg is not ("--minimized" or "--smoke-test")).ToArray();
        using var form = new MainForm(serverArguments, args.Contains("--minimized"), smokeTest);
        instance.Listen(form);
        Application.Run(form);
        return form.ExitCode;
    }

    private static void ConnectConsole()
    {
        // A WinExe has no console by default. Preserve redirected handles for automation.
        var output = GetStdHandle(-11);
        if (output == 0 || output == -1) _ = AttachConsole(uint.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
}
