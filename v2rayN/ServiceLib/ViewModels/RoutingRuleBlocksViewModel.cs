using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

public partial class RoutingRuleBlocksViewModel : MyReactiveObject, ICloseable
{
    private readonly RulesItem _source;
    public event EventHandler? RequestClose;
    public Interaction<AppRoutingPackageViewModel, bool> PickPackages { get; } = new();
    public ObservableCollection<RoutingFilterViewModel> Filters { get; } = [];
    public ObservableCollection<RoutingSelectorChoice> AvailableSelectors { get; } = [];
    public Interaction<AppRoutingProcessViewModel, bool> PickProcess { get; } = new();
    public Interaction<RoutingProcessMode, string?> BrowseProcessPath { get; } = new();

    [Reactive] public partial IReadOnlyList<RoutingFilterViewModel> MatchFilters { get; set; } = [];
    [Reactive] public partial IReadOnlyList<RoutingFilterViewModel> ConstraintFilters { get; set; } = [];
    [Reactive] public partial bool HasMatches { get; set; }
    [Reactive] public partial string Expression { get; set; } = "";

    [Reactive] public partial string Remarks { get; set; }
    [Reactive] public partial string OutboundTag { get; set; }
    [Reactive] public partial string? RuleType { get; set; }
    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial string Error { get; set; } = "";

    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SelectProfileCmd { get; }

    public RoutingRuleBlocksViewModel(RulesItem source)
    {
        _source = source;
        Remarks = source.Remarks ?? "";
        OutboundTag = source.OutboundTag ?? Global.ProxyTag;
        RuleType = source.RuleType?.ToString();
        Enabled = source.IsEnabled;
        var filters = source.Blocks?.Filters ?? RoutingBlockRules.FromLegacy(source).Filters;
        foreach (var filter in filters)
        {
            AddFilter(filter.Selector, filter.Values, filter.Applications);
        }
        UpdateChoices();
        SaveCmd = ReactiveCommand.Create(() => { if (TrySave()) { RequestClose?.Invoke(this, EventArgs.Empty); } });
        SelectProfileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var picker = new ProfilesSelectViewModel();
            picker.SetConfigTypeFilter([EConfigType.Custom], exclude: true);
            if (await AppManager.Instance.WindowDialog.ShowDialogAsync(picker) == true
                && await picker.GetProfileItem() is { } profile) { OutboundTag = profile.Remarks; }
        });
    }

    public async Task Initialize()
    {
        if (!OperatingSystem.IsWindows() || !Filters.Any(f => f.IsPackages)) { return; }
        try
        {
            var packages = await Task.Run(() => OperatingSystem.IsWindows() ? RoutePackageCatalog.Read() : []);
            var names = packages.ToDictionary(p => p.Family, p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var block in Filters.Where(f => f.IsPackages)) { block.SetPackageNames(names); }
        }
        // Names are presentation only: unavailable/uninstalled packages remain editable by family ID.
        catch (Exception ex) { Logging.SaveLog("Load routing package names", ex); }
    }

    internal void AddFilter(RoutingSelector selector, IEnumerable<string>? values = null, IEnumerable<RoutingApplicationRow>? applications = null)
    {
        if (Filters.Any(f => f.Selector == selector)) { return; }
        var block = new RoutingFilterViewModel(selector, values ?? [], applications);
        block.RemoveCmd = ReactiveCommand.Create(() =>
        {
            Filters.Remove(block);
            UpdateChoices();
        });
        block.AddFullPathCmd = ReactiveCommand.CreateFromTask(() => AddProcessPath(block, RoutingProcessMode.FullPath));
        block.AddFolderCmd = ReactiveCommand.CreateFromTask(() => AddProcessPath(block, RoutingProcessMode.Folder));
        block.ChooseCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            if (block.IsProcess)
            {
                using var picker = new AppRoutingProcessViewModel();
                if (await PickProcess.HandleSafe(picker) && picker.SelectedProcess is { } process)
                { block.AddApplication(RoutingApplicationRow.FromValue(process.Name, selector)); }
            }
            else
            {
                using var picker = new AppRoutingPackageViewModel(block.Applications.Select(row => row.Value));
                if (await PickPackages.HandleSafe(picker)) { block.SetPackages(picker.SelectedPackageNames()); }
            }
        });
        Filters.Add(block);
        UpdateChoices();
    }

    private async Task AddProcessPath(RoutingFilterViewModel block, RoutingProcessMode mode)
    {
        var path = await BrowseProcessPath.HandleSafe(mode);
        if (string.IsNullOrWhiteSpace(path)) { return; }
        // A trailing separator makes folders unambiguous, even if named "something.exe".
        if (mode == RoutingProcessMode.Folder) { path = path.TrimEnd('/', '\\') + "/"; }
        block.AddApplication(RoutingApplicationRow.FromValue(path, RoutingSelector.Process));
    }

    private void UpdateChoices()
    {
        AvailableSelectors.Clear();
        foreach (var selector in Enum.GetValues<RoutingSelector>().Where(s => Filters.All(f => f.Selector != s)))
        {
            AvailableSelectors.Add(new(selector, ReactiveCommand.Create(() => AddFilter(selector))));
        }
        MatchFilters = Filters.Where(f => RoutingBlockRules.IsMatch(f.Selector)).ToArray();
        ConstraintFilters = Filters.Where(f => !RoutingBlockRules.IsMatch(f.Selector)).ToArray();
        HasMatches = MatchFilters.Count > 0;
        for (var index = 0; index < MatchFilters.Count; index++)
        {
            MatchFilters[index].ShowOr = index > 0;
        }
        for (var index = 0; index < ConstraintFilters.Count; index++)
        { ConstraintFilters[index].ShowAnd = HasMatches || index > 0; }
        UpdateExpression();
    }

    private RoutingRuleBlocks EditedBlocks() => new()
    {
        Version = _source.Blocks?.Version ?? 1, Enabled = Enabled,
        Filters = Filters.Select(f => new RoutingFilter
        { Selector = f.Selector, Values = f.IsApplications ? [] : f.Values(), Applications = f.IsApplications ? f.Applications.Select(row => row.ToModel()).ToList() : null }).ToList(),
    };

    private void UpdateExpression() => Expression = RoutingBlockRules.Describe(EditedBlocks(), f => RoutingFilterViewModel.TitleFor(f.Selector));

    internal bool TrySave()
    {
        try
        {
            var candidate = new RulesItem
            {
                RuleType = string.IsNullOrEmpty(RuleType) ? null : Enum.Parse<ERuleType>(RuleType),
                Blocks = EditedBlocks(),
            };
            RoutingBlockRules.Validate(candidate);
            if (string.IsNullOrWhiteSpace(OutboundTag)) { throw new ArgumentException(ResUI.RoutingBlocksOutboundRequired); }
            if (string.IsNullOrEmpty(_source.Id)) { _source.Id = Utils.GetGuid(false); }
            _source.Remarks = Remarks.Trim();
            _source.OutboundTag = OutboundTag.Trim();
            _source.RuleType = candidate.RuleType;
            RoutingBlockRules.Store(_source, candidate.Blocks);
            Error = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { Error = ex.Message; return false; }
    }

    internal static List<string> Split(string? text) => (text ?? "").Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
}

public sealed record RoutingSelectorChoice(RoutingSelector Selector, ReactiveCommand<RxVoid, RxVoid> Command)
{
    public string Title => RoutingFilterViewModel.TitleFor(Selector);
}

public partial class RoutingFilterViewModel : MyReactiveObject
{
    private bool _descending;
    public RoutingSelector Selector { get; }
    public string Title => TitleFor(Selector);
    public string Hint => Selector switch
    {
        RoutingSelector.Domain => ResUI.RoutingBlocksDomainHint,
        RoutingSelector.IP => ResUI.RoutingBlocksIpHint,
        RoutingSelector.Port => ResUI.RoutingBlocksPortHint,
        RoutingSelector.Process => ResUI.RoutingBlocksProcessHint,
        RoutingSelector.WindowsApp => ResUI.RoutingBlocksPackageHint,
        RoutingSelector.InboundTag => ResUI.RoutingBlocksInboundHint + " " + string.Join(", ", AvailableInbounds(AppManager.Instance.Config)),
        _ => ResUI.RoutingBlocksValuesHint,
    };
    public bool IsPackages => Selector == RoutingSelector.WindowsApp;
    public bool IsProcess => Selector == RoutingSelector.Process;
    public bool IsApplications => IsProcess || IsPackages;
    public bool IsText => !IsChoices && !IsApplications;
    public ObservableCollection<RoutingApplicationRowViewModel> Applications { get; } = [];
    public bool IsChoices => Selector is RoutingSelector.Protocol or RoutingSelector.Network;
    public bool CanPickPackages => OperatingSystem.IsWindows();
    public ObservableCollection<RoutingFilterChoice> Choices { get; } = [];
    [Reactive] public partial string Text { get; set; } = "";
    [Reactive] public partial bool ShowAnd { get; set; }
    [Reactive] public partial bool ShowOr { get; set; }
    [Reactive] public partial string PackageSortHeading { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> SortPackagesCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemoveCmd { get; internal set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ChooseCmd { get; internal set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AddFullPathCmd { get; internal set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AddFolderCmd { get; internal set; } = null!;

    public RoutingFilterViewModel(RoutingSelector selector, IEnumerable<string> values, IEnumerable<RoutingApplicationRow>? applications = null)
    {
        Selector = selector;
        PackageSortHeading = Title + " ↑";
        SortPackagesCmd = ReactiveCommand.Create(() =>
        {
            _descending = !_descending;
            PackageSortHeading = Title + (_descending ? " ↓" : " ↑");
            SortPackages();
        });
        var list = values.ToList();
        Text = string.Join(Environment.NewLine, list);
        if (IsApplications)
        { foreach (var row in applications ?? list.Select(value => RoutingApplicationRow.FromValue(value, selector))) { AddApplication(row); } }
        if (IsPackages) { SortPackages(); }
        var options = selector switch { RoutingSelector.Protocol => Global.RuleProtocols, RoutingSelector.Network => new List<string> { "tcp", "udp" }, _ => [] };
        foreach (var value in options.Concat(list).Distinct())
        { Choices.Add(new(value) { Selected = list.Contains(value) }); }
    }

    internal List<string> Values()
    {
        if (IsApplications) { return Applications.Where(row => row.Enabled).Select(row => row.ToModel().MatchValue(Selector)).Distinct().ToList(); }
        if (IsChoices) { return Choices.Where(c => c.Selected).Select(c => c.Value).ToList(); }
        if (Selector == RoutingSelector.Port) { return RoutingRuleBlocksViewModel.Split(Text); }
        // Commas are meaningful in domain regexes. Only the Port block uses them as separators.
        return Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct().ToList();
    }

    internal void AddApplication(RoutingApplicationRow model)
    {
        var row = new RoutingApplicationRowViewModel(model, IsProcess);
        row.DeleteCmd = ReactiveCommand.Create(() => Applications.Remove(row));
        row.UpCmd = ReactiveCommand.Create(() => Move(row, -1));
        row.DownCmd = ReactiveCommand.Create(() => Move(row, 1));
        Applications.Add(row);
    }

    private void Move(RoutingApplicationRowViewModel row, int delta)
    {
        var index = Applications.IndexOf(row);
        if (index >= 0 && index + delta >= 0 && index + delta < Applications.Count)
        { Applications.Move(index, index + delta); }
    }

    internal void SetPackages(IReadOnlyDictionary<string, string> names)
    {
        var selected = names.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Applications.Where(row => !selected.Contains(row.Value)).ToArray()) { Applications.Remove(row); }
        selected.ExceptWith(Applications.Select(row => row.Value));
        foreach (var family in selected) { AddApplication(new() { Value = family }); }
        SetPackageNames(names);
    }

    internal void SetPackageNames(IReadOnlyDictionary<string, string> names)
    {
        foreach (var row in Applications)
        { row.DisplayName = names.TryGetValue(row.Value, out var name) && !string.IsNullOrWhiteSpace(name) ? name : row.Value; }
        SortPackages();
    }

    private void SortPackages()
    {
        var sorted = Applications.OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_descending) { Array.Reverse(sorted); }
        for (var index = 0; index < sorted.Length; index++)
        { Applications.Move(Applications.IndexOf(sorted[index]), index); }
    }

    internal static IEnumerable<string> AvailableInbounds(Config? config)
    {
        yield return "socks";
        yield return "app-routing";
        var inbound = config?.Inbound?.FirstOrDefault();
        if (inbound?.SecondLocalPortEnabled == true) { yield return "socks2"; }
        if (inbound is { AllowLANConn: true, NewPort4LAN: true }) { yield return "socks3"; }
        if (config?.TunModeItem?.EnableTun == true) { yield return "tun"; }
    }

    public static string TitleFor(RoutingSelector selector) => selector switch
    {
        RoutingSelector.Domain => "Domain",
        RoutingSelector.IP => "IP",
        RoutingSelector.Port => ResUI.LvPort,
        RoutingSelector.Process => ResUI.RoutingBlocksProcess,
        RoutingSelector.WindowsApp => ResUI.RoutingBlocksWindowsApp,
        RoutingSelector.Protocol => ResUI.RoutingBlocksProtocol,
        RoutingSelector.InboundTag => ResUI.RoutingBlocksInboundTag,
        RoutingSelector.Network => ResUI.RoutingBlocksNetwork,
        _ => selector.ToString(),
    };
}

public partial class RoutingFilterChoice(string value) : MyReactiveObject
{
    public string Value { get; } = value;
    [Reactive] public partial bool Selected { get; set; }
}

public partial class RoutingApplicationRowViewModel : MyReactiveObject
{
    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial string Value { get; set; }
    [Reactive] public partial string DisplayName { get; set; }
    [Reactive] public partial bool IncludeChildren { get; set; }
    public bool IsProcess { get; }
    public bool IsPackage => !IsProcess;
    public ReactiveCommand<RxVoid, bool> DeleteCmd { get; internal set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpCmd { get; internal set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> DownCmd { get; internal set; } = null!;

    public RoutingApplicationRowViewModel(RoutingApplicationRow row, bool isProcess)
    {
        Enabled = row.Enabled;
        // Preserve older explicit modes by displaying the value they actually match.
        Value = isProcess ? row.MatchValue(RoutingSelector.Process) : row.Value;
        DisplayName = Value;
        IncludeChildren = row.IncludeChildren; IsProcess = isProcess;
    }

    public RoutingApplicationRow ToModel()
    {
        var row = RoutingApplicationRow.FromValue(Value, IsProcess ? RoutingSelector.Process : RoutingSelector.WindowsApp);
        row.Enabled = Enabled;
        row.IncludeChildren = IncludeChildren;
        return row;
    }
}
