using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

public partial class AppRoutingInterfaceWindow : WindowBase<AppRoutingInterfaceViewModel>
{
    public AppRoutingInterfaceWindow()
    {
        InitializeComponent();
        btnSave.Click += (_, _) => Close(true);
        btnCancel.Click += (_, _) => Close(false);
    }
}
