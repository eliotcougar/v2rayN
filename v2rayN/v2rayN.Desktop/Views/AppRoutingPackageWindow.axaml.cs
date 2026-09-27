using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

public partial class AppRoutingPackageWindow : WindowBase<AppRoutingPackageViewModel>
{
    public AppRoutingPackageWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.Initialize();
        btnCancel.Click += (_, _) => Close(false);
        btnSave.Click += (_, _) => { if (ViewModel.CanConfirm) { Close(true); } };
    }
}
