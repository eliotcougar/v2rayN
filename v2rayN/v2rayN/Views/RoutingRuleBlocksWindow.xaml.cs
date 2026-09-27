namespace v2rayN.Views;

public partial class RoutingRuleBlocksWindow
{
    public RoutingRuleBlocksWindow()
    {
        InitializeComponent();
        cmbOutboundTag.ItemsSource = Global.OutboundTags;
        cmbRuleType.ItemsSource = Utils.GetEnumNames<ERuleType>().AppendEmpty();
        Loaded += async (_, _) => { txtRemarks.Focus(); await ViewModel.Initialize(); };
        this.WhenActivated(disposables =>
        {
            DataContext = ViewModel;
            ViewModel.BrowseProcessPath.RegisterHandler(interaction =>
            {
                if (interaction.Input == RoutingProcessMode.Folder)
                {
                    var picker = new Microsoft.Win32.OpenFolderDialog();
                    interaction.SetOutput(picker.ShowDialog(this) == true ? picker.FolderName : null);
                }
                else
                {
                    var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|*.exe" };
                    interaction.SetOutput(picker.ShowDialog(this) == true ? picker.FileName : null);
                }
            }).DisposeWith(disposables);
            ViewModel.PickProcess.RegisterHandler(interaction =>
            {
                var picker = new AppRoutingAppPickerWindow { Owner = this, ViewModel = interaction.Input, DataContext = interaction.Input };
                interaction.SetOutput(picker.ShowDialog() == true);
            }).DisposeWith(disposables);
            ViewModel.PickPackages.RegisterHandler(interaction =>
            {
                var picker = new AppRoutingPackageWindow { Owner = this, ViewModel = interaction.Input, DataContext = interaction.Input };
                interaction.SetOutput(picker.ShowDialog() == true);
            }).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void AddSelector(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = btnAdd };
        foreach (var choice in ViewModel.AvailableSelectors)
        { menu.Items.Add(new System.Windows.Controls.MenuItem { Header = choice.Title, Command = choice.Command }); }
        menu.IsOpen = true;
    }
}
