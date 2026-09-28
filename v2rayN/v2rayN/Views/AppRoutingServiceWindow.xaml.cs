namespace v2rayN.Views;

public partial class AppRoutingServiceWindow
{
    public AppRoutingServiceWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.Initialize();
        btnSave.Click += (_, _) => { if (ViewModel.CanConfirm) { DialogResult = true; } };
    }
}
