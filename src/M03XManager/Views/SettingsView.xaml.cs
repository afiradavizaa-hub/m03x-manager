using System.Windows;
using System.Windows.Controls;
using M03XManager.ViewModels;

namespace M03XManager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.PasswordText = box.Password;
    }
}
