using System.Windows;
using System.Windows.Controls;

namespace M03XManager.Views;

public partial class BatteryView : UserControl
{
    public BatteryView()
    {
        InitializeComponent();
    }

    private void ResetChart_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
}
