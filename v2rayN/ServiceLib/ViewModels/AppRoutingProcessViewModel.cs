using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

public partial class AppRoutingProcessViewModel : MyReactiveObject, IDisposable
{
    public ObservableCollection<AppRouteProcess> NetworkApps { get; } = [];
    private List<AppRouteProcess> _networkApps = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;
    [Reactive]
    public partial bool IsLoadingApps
    {
        get; set;
    }
    [Reactive]
    public partial bool CanUseProcess
    {
        get; set;
    }
    [Reactive] public partial string ProcessSearch { get; set; } = "";
    [Reactive] public partial string ProcessStatus { get; set; } = "";
    [Reactive]
    public partial AppRouteProcess? SelectedProcess
    {
        get; set;
    }

    public ReactiveCommand<RxVoid, RxVoid> RefreshProcessesCmd { get; }
    public AppRoutingProcessViewModel()
    {
        RefreshProcessesCmd = ReactiveCommand.CreateFromTask(RefreshProcesses,
            this.WhenAnyValue(vm => vm.IsLoadingApps).Select(loading => !loading));
        _subscriptions.Add(this.WhenAnyValue(vm => vm.ProcessSearch).Subscribe(_ => FilterProcesses()));
        _subscriptions.Add(this.WhenAnyValue(vm => vm.SelectedProcess, vm => vm.IsLoadingApps,
            (process, loading) => process != null && !loading).Subscribe(value => CanUseProcess = value));
    }
    private async Task RefreshProcesses()
    {
        IsLoadingApps = true;
        ProcessStatus = ResUI.AppRoutingLoadingApps;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var apps = await Task.Run(AppRouteProcessCatalog.Read);
            if (_disposed)
            {
                return;
            }

            _networkApps = apps;
            FilterProcesses();
            ProcessStatus = apps.Count == 0 ? ResUI.AppRoutingNoNetworkApps : ResUI.AppRoutingNetworkAppsHelp;
        }
        catch (Exception ex) { ProcessStatus = ex.Message; }
        finally { IsLoadingApps = false; }
    }

    private void FilterProcesses()
    {
        var pid = SelectedProcess?.Pid;
        NetworkApps.Clear();
        foreach (var process in _networkApps.Where(p => p.MatchesSearch(ProcessSearch.Trim())))
        {
            NetworkApps.Add(process);
        }

        SelectedProcess = NetworkApps.FirstOrDefault(p => p.Pid == pid);
    }


    public void Dispose()
    {
        _disposed = true;
        foreach (var subscription in _subscriptions) { subscription.Dispose(); }
        RefreshProcessesCmd.Dispose();
    }
}
