namespace v2rayN.Views;

public partial class AppRoutingInterfaceWindow
{
    public AppRoutingInterfaceWindow()
    {
        InitializeComponent();
        btnSave.Click += (_, _) => DialogResult = true;
    }
}
