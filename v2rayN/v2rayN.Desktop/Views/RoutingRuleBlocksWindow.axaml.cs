using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

using Avalonia.Platform.Storage;

public partial class RoutingRuleBlocksWindow : WindowBase<RoutingRuleBlocksViewModel>
{
    public RoutingRuleBlocksWindow()
    {
        InitializeComponent();
        cmbOutboundTag.ItemsSource = Global.OutboundTags;
        cmbRuleType.ItemsSource = Utils.GetEnumNames<ERuleType>().AppendEmpty();
        Loaded += async (_, _) => { txtRemarks.Focus(); await ViewModel.Initialize(); };
        btnCancel.Click += (_, _) => Close(false);
        this.WhenActivated(disposables =>
        {
            DataContext = ViewModel;
            ViewModel.BrowseProcessPath.RegisterHandler(async interaction =>
            {
                if (interaction.Input == RoutingProcessMode.Folder)
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
                    interaction.SetOutput(folders.FirstOrDefault()?.TryGetLocalPath());
                }
                else
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType("Executable") { Patterns = ["*.exe"] }],
                    });
                    interaction.SetOutput(files.FirstOrDefault()?.TryGetLocalPath());
                }
            }).DisposeWith(disposables);
            ViewModel.PickProcess.RegisterHandler(async interaction =>
            {
                var picker = new AppRoutingAppPickerWindow { ViewModel = interaction.Input, DataContext = interaction.Input };
                interaction.SetOutput(await picker.ShowDialog<bool>(this));
            }).DisposeWith(disposables);
            ViewModel.PickPackages.RegisterHandler(async interaction =>
            {
                var picker = new AppRoutingPackageWindow { ViewModel = interaction.Input, DataContext = interaction.Input };
                interaction.SetOutput(await picker.ShowDialog<bool>(this));
            }).DisposeWith(disposables);
        });
    }

    private void AddSelector(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var choice in ViewModel.AvailableSelectors)
        { menu.Items.Add(new MenuItem { Header = choice.Title, Command = choice.Command }); }
        menu.Open(btnAdd);
    }
}
