using System.Collections.ObjectModel;
using System.Windows;
using Wpf.Ui.Controls;

namespace SampleWpfApp;

public record AdapterItem(string Title, string Description);

public partial class MainWindow : FluentWindow
{
    public ObservableCollection<AdapterItem> CatalogItems { get; } = new()
    {
        new("Space Engineers 2 Grid Flight", "6-DOF Newtonian physics adapter module"),
        new("Elite Dangerous Flight Deck", "HOSAS rotational dampening & throttle curves"),
        new("Star Citizen Cockpit Control", "VJoy virtual joystick integration for flight control"),
        new("DCS World Flight Stick", "Direct HID axis mapping for HOTAS setups")
    };

    public MainWindow()
    {
        InitializeComponent();
        CatalogListView.ItemsSource = CatalogItems;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Visible;
        CatalogView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Status: Navigated to Dashboard";
    }

    private void NavCatalog_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CatalogView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Status: Navigated to Catalog";
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CatalogView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        StatusLabel.Text = "Status: Navigated to Settings";
    }

    private void ToggleOverride_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Status: License Override Toggled!";
    }

    private void OpenModal_Click(object sender, RoutedEventArgs e)
    {
        TestModalOverlay.Visibility = Visibility.Visible;
    }

    private void CloseModal_Click(object sender, RoutedEventArgs e)
    {
        TestModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void ConfirmModal_Click(object sender, RoutedEventArgs e)
    {
        TestModalOverlay.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Status: Modal Action Confirmed!";
    }

    private void OpenDrawer_Click(object sender, RoutedEventArgs e)
    {
        TestDrawerOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e)
    {
        TestDrawerOverlay.Visibility = Visibility.Collapsed;
    }
}
