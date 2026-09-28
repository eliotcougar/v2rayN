using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

public partial class AppRoutingServiceRow : ReactiveObject
{
    public string Name { get; }
    public string DisplayName { get; }
    public string Details { get; }
    [Reactive] public partial bool Selected { get; set; }

    internal AppRoutingServiceRow(RouteServiceInfo service, bool selected, bool installed)
    {
        Name = service.Name;
        DisplayName = service.DisplayName;
        Details = installed ? Name : Name + " · " + ResUI.AppRoutingServiceUnavailable;
        Selected = selected;
    }
}

/// <summary>Detached service selection. The owning rule editor commits it only on confirmation.</summary>
public partial class AppRoutingServiceViewModel : ReactiveObject, IDisposable
{
    [Reactive] public partial string Search { get; set; } = "";
    [Reactive] public partial string Status { get; set; } = "";
    [Reactive] public partial string SelectionSummary { get; set; } = "";
    [Reactive] public partial bool IsBusy { get; set; } = true;
    [Reactive] public partial bool CanConfirm { get; set; }
    [Reactive] public partial IReadOnlyList<AppRoutingServiceRow> Services { get; set; } = [];
    public ReactiveCommand<RxVoid, RxVoid> SelectVisibleCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearVisibleCmd { get; }
    private readonly Func<IReadOnlyList<RouteServiceInfo>> _read;
    private readonly HashSet<string> _selected;
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<AppRoutingServiceRow> _rows = [];
    private bool _loaded, _disposed;

    internal AppRoutingServiceViewModel(IEnumerable<string> selected, Func<IReadOnlyList<RouteServiceInfo>>? read = null)
    {
        _selected = selected.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _read = read ?? (() => OperatingSystem.IsWindows() ? RouteServiceCatalog.Read() : throw new PlatformNotSupportedException());
        var canEdit = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        SelectVisibleCmd = ReactiveCommand.Create(() => SetVisible(true), canEdit);
        ClearVisibleCmd = ReactiveCommand.Create(() => SetVisible(false), canEdit);
        _subscriptions.Add(this.WhenAnyValue(vm => vm.Search).Subscribe(_ => Filter()));
        _subscriptions.Add(this.WhenAnyValue(vm => vm.IsBusy).Subscribe(_ => UpdateSelection()));
    }

    public async Task Initialize()
    {
        Status = ResUI.AppRoutingLoadingServices;
        try
        {
            var catalog = await Task.Run(_read);
            if (_disposed) { return; }
            var present = catalog.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rows = catalog.Concat(_selected.Where(name => !present.Contains(name)).Select(name => new RouteServiceInfo(name, name, 0)))
                .Select(service => new AppRoutingServiceRow(service, _selected.Contains(service.Name), present.Contains(service.Name)))
                .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var row in _rows)
            { _subscriptions.Add(row.WhenAnyValue(r => r.Selected).Subscribe(_ => UpdateSelection())); }
            _loaded = true;
            Filter();
            Status = "";
        }
        catch (Exception ex) { if (!_disposed) { Status = ResUI.OperationFailed + ": " + ex.Message; } }
        finally { if (!_disposed) { IsBusy = false; } }
    }

    internal IReadOnlyDictionary<string, string> SelectedServiceNames() => _rows.Where(row => row.Selected)
        .ToDictionary(row => row.Name, row => row.DisplayName, StringComparer.OrdinalIgnoreCase);

    private void Filter() => Services = _rows.Where(row => row.DisplayName.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
        || row.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private void SetVisible(bool selected)
    {
        foreach (var row in Services) { row.Selected = selected; }
    }

    private void UpdateSelection()
    {
        var count = _rows.Count(row => row.Selected);
        SelectionSummary = string.Format(ResUI.AppRoutingServicesSelected, count);
        CanConfirm = _loaded && !IsBusy && count != 0;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var subscription in _subscriptions) { subscription.Dispose(); }
        SelectVisibleCmd.Dispose();
        ClearVisibleCmd.Dispose();
    }
}
