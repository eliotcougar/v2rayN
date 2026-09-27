using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

/// <summary>The settings page owns enablement; the routing table owns all rules.</summary>
public partial class AppRoutingSettingsViewModel : MyReactiveObject
{
    private readonly Config _routingConfig;
    [Reactive] public partial bool Enabled { get; set; }
    public bool CanChangeRouting { get; }
    public Interaction<AppRoutingInterfaceViewModel, bool> PickInterfaces { get; } = new();
    public ReactiveCommand<RxVoid, RxVoid> PickInterfacesCmd { get; }

    internal AppRoutingSettingsViewModel(Config config, bool isAdministrator, RouteInterfaceMonitor? interfaces)
    {
        _routingConfig = config;
        Enabled = config.AppRouting.Enabled;
        CanChangeRouting = isAdministrator;
        PickInterfacesCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            if (interfaces == null) { return; }
            try
            {
                var snapshot = await interfaces.RefreshAsync();
                var editor = new AppRoutingInterfaceViewModel(snapshot.Options, snapshot.Adapters);
                if (await PickInterfaces.HandleSafe(editor)) { await interfaces.SaveAsync(editor.ToOptions()); }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("Application routing interfaces", ex);
                NoticeManager.Instance.SendMessageEx(ResUI.AppRoutingRouteError + ": " + ex.Message);
            }
        });
    }

    // The normal settings confirmation persists enablement. A canceled dialog or
    // failed save must not change the preference or start/stop interception.
    internal async Task<int> SaveAsync(Func<Config, Task<int>> save)
    {
        var previous = _routingConfig.AppRouting.Enabled;
        if (CanChangeRouting) { _routingConfig.AppRouting.Enabled = Enabled; }
        try
        {
            var result = await save(_routingConfig);
            if (result != 0) { _routingConfig.AppRouting.Enabled = previous; }
            return result;
        }
        catch { _routingConfig.AppRouting.Enabled = previous; throw; }
    }
}
