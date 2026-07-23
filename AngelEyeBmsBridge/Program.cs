namespace AngelEyeBmsBridge;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(ApplicationModeSelector.IsEngineering(args) ? new Form1() : new QueryConsoleForm());
    }    
}
