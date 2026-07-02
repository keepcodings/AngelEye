using AngelEyeBmsBridge;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string configPath = ResolveConfigPath(args);
if (args.Any(static arg => string.Equals(arg, "--check-config", StringComparison.OrdinalIgnoreCase)))
{
    WorkerSettings.Load(configPath);
    Console.WriteLine($"設定檔檢查成功: {configPath}");
    return 0;
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

try
{
    WorkerSettings settings = WorkerSettings.Load(configPath);
    await using AngelBridgeWorker worker = new(settings);
    await worker.RunAsync(shutdown.Token).ConfigureAwait(false);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [ANGEL][FATAL] {ex}");
    return 1;
}

static string ResolveConfigPath(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(args[i + 1]);
        }
    }

    string? envPath = Environment.GetEnvironmentVariable("ANGEL_BRIDGE_CONFIG");
    if (!string.IsNullOrWhiteSpace(envPath))
    {
        return Path.GetFullPath(envPath);
    }

    return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}
