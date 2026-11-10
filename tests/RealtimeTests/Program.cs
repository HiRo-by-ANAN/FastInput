namespace FastInput.RealtimeTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        // Style-only controls have no message loop. Keep async tests on the thread pool.
        System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        var suite = new TestSuite();
        FastInput.Windows.Realtime.Loc.SetLanguage("zh-Hant");
        I18nTests.Register(suite);
        UiStyleTests.Register(suite);
        FrameTests.Register(suite);
        ClientTests.Register(suite);
        BackendHostTests.Register(suite);
        LogicTests.Register(suite);
        RealAdapterTests.Register(suite);
        EnterTests.Register(suite);
        EndToEndTests.Register(suite);
        return await suite.RunAsync();
    }
}
