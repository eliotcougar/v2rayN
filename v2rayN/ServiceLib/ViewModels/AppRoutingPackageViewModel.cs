using System.Security.Principal;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

public partial class AppRoutingPackageRow : ReactiveObject
{
    public string Family { get; }
    public string Name { get; }
    public string Details { get; }
    [Reactive] public partial bool Selected { get; set; }

    internal AppRoutingPackageRow(RoutePackage package, bool selected, bool installed)
    {
        Family = package.Family;
        Name = package.Name;
        Details = string.Join(" · ", new[] { package.Family, package.Publisher,
            installed ? "" : ResUI.AppRoutingPackageUnavailable }.Where(s => s.Length != 0));
        Selected = selected;
    }
}

/// <summary>A detached package selection: only the owning rule editor can commit it.</summary>
public partial class AppRoutingPackageViewModel : ReactiveObject, IDisposable
{
    [Reactive] public partial string Search { get; set; } = "";
    [Reactive] public partial string Status { get; set; } = "";
    [Reactive] public partial string SelectionSummary { get; set; } = "";
    [Reactive] public partial bool IsBusy { get; set; } = true;
    [Reactive] public partial bool CanConfirm { get; set; }
    [Reactive] public partial IReadOnlyList<AppRoutingPackageRow> Packages { get; set; } = [];
    public string Account { get; }
    public ReactiveCommand<RxVoid, RxVoid> ImportCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SelectVisibleCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearVisibleCmd { get; }
    private readonly Func<IReadOnlyList<RoutePackage>> _read;
    private readonly Func<IReadOnlyList<RoutePackage>, RouteLoopbackSelection> _import;
    private readonly HashSet<string> _selected;
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<RoutePackage> _catalog = [];
    private IReadOnlyList<AppRoutingPackageRow> _rows = [];
    private bool _loaded, _disposed;

    internal AppRoutingPackageViewModel(IEnumerable<string> selected,
        Func<IReadOnlyList<RoutePackage>>? read = null, Func<IReadOnlyList<RoutePackage>, RouteLoopbackSelection>? import = null)
    {
        _selected = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _read = read ?? (() => OperatingSystem.IsWindows() ? RoutePackageCatalog.Read() : throw new PlatformNotSupportedException());
        _import = import ?? (packages => OperatingSystem.IsWindows() ? RouteLoopbackImport.Read(packages) : throw new PlatformNotSupportedException());
        if (OperatingSystem.IsWindows())
        {
            using var user = WindowsIdentity.GetCurrent();
            Account = string.Format(ResUI.AppRoutingPackageAccount, user.Name);
        }
        else { Account = ""; }
        var canEdit = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        ImportCmd = ReactiveCommand.CreateFromTask(Import, canEdit.Select(editable => editable && _loaded));
        SelectVisibleCmd = ReactiveCommand.Create(() => SetVisible(true), canEdit);
        ClearVisibleCmd = ReactiveCommand.Create(() => SetVisible(false), canEdit);
        _subscriptions.Add(this.WhenAnyValue(vm => vm.Search).Subscribe(_ => Filter()));
        _subscriptions.Add(this.WhenAnyValue(vm => vm.IsBusy).Subscribe(_ => UpdateSelection()));
    }

    public async Task Initialize()
    {
        Status = ResUI.AppRoutingLoadingPackages;
        try
        {
            var catalog = await Task.Run(_read);
            if (_disposed) { return; }
            _catalog = catalog;
            var present = catalog.Select(p => p.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rows = catalog.Concat(_selected.Where(f => !present.Contains(f)).Select(f => new RoutePackage(f, f, "")))
                .Select(p => new AppRoutingPackageRow(p, _selected.Contains(p.Family), present.Contains(p.Family)))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            foreach (var row in _rows)
            {
                _subscriptions.Add(row.WhenAnyValue(r => r.Selected).Subscribe(_ => UpdateSelection()));
            }
            _loaded = true;
            Filter();
            Status = "";
        }
        catch (Exception ex) { if (!_disposed) { Status = ResUI.OperationFailed + ": " + ex.Message; } }
        finally { if (!_disposed) { IsBusy = false; } }
    }

    internal IReadOnlyDictionary<string, string> SelectedPackageNames() => _rows.Where(p => p.Selected)
        .ToDictionary(p => p.Family, p => p.Name, StringComparer.OrdinalIgnoreCase);

    private void Filter() => Packages = _rows.Where(p => p.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
        p.Details.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private void SetVisible(bool selected)
    {
        foreach (var row in Packages) { row.Selected = selected; }
    }

    private void UpdateSelection()
    {
        var count = _rows.Count(p => p.Selected);
        SelectionSummary = string.Format(ResUI.AppRoutingPackagesSelected, count);
        CanConfirm = _loaded && !IsBusy && count != 0;
    }

    private async Task Import()
    {
        IsBusy = true;
        try
        {
            var imported = await Task.Run(() => _import(_catalog));
            if (_disposed) { return; }
            var families = imported.Families.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matched = _rows.Where(p => families.Contains(p.Family)).ToArray();
            foreach (var row in matched) { row.Selected = true; }
            Status = string.Format(ResUI.AppRoutingPackageImportResult, matched.Length, imported.Unmatched);
        }
        catch (Exception ex) { if (!_disposed) { Status = ResUI.OperationFailed + ": " + ex.Message; } }
        finally { if (!_disposed) { IsBusy = false; } }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var subscription in _subscriptions) { subscription.Dispose(); }
        ImportCmd.Dispose();
        SelectVisibleCmd.Dispose();
        ClearVisibleCmd.Dispose();
    }
}
