namespace v2rayN.Views;

public partial class AppRoutingPackageWindow
{
    public AppRoutingPackageWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            NamePanel.Visibility = ViewModel.ShowName ? Visibility.Visible : Visibility.Collapsed;
            if (ViewModel.ShowName) { txtName.Focus(); }
            await ViewModel.Initialize();
        };
        btnSave.Click += (_, _) => { if (ViewModel.CanConfirm) { DialogResult = true; } };
    }
}
