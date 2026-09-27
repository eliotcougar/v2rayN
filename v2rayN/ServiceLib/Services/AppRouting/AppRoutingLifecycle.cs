namespace ServiceLib.Services.AppRouting;

internal static class AppRoutingLifecycle
{
    // Apply every reload, including the first start and recovery after a failure.
    // Runtime state must never overwrite the persisted preference.
    public static Task SynchronizeAsync(Config config, IAppRoutingRuntime runtime) =>
        config.AppRouting.Enabled ? runtime.StartAsync(config) : runtime.StopAsync();
}
