using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SampleWpfApp;

public record CollectionItem(string Title, string Description);

public partial class MainWindow : FluentWindow
{
    public ObservableCollection<CollectionItem> CollectionItems { get; } = new()
    {
        new("Quarterly Planning", "A shared roadmap and milestone review"),
        new("Research Notes", "Collected findings for the current initiative"),
        new("Design Review", "Open decisions and annotated mockups"),
        new("Team Retrospective", "Actions and observations from the last cycle")
    };

    public string RequiredValue { get; set; } = "Required value";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        CollectionListView.ItemsSource = CollectionItems;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Visible;
        CollectionView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Status: Navigated to Dashboard";
    }

    private void NavCollection_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CollectionView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Status: Navigated to Collection";
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CollectionView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        StatusLabel.Text = "Status: Navigated to Settings";
    }

    private void ToggleOverride_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Status: Highlight Toggled!";
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

    private void ResetForm_Click(object sender, RoutedEventArgs e)
    {
        NameInput.Text = "Ada Lovelace";
        NotesInput.Text = "Line one";
        SecretInput.Password = "initial-secret";
        ProfileList.SelectedIndex = 0;
        WorkspaceTabs.SelectedIndex = 0;
        RadioAlpha.IsChecked = true;
        VolumeSlider.Value = 30;
        TriStateCheck.IsChecked = null;
        StatusLabel.Text = "Status: Form Reset";
    }

    private void OpenPopup_Click(object sender, RoutedEventArgs e) => InspectorPopup.IsOpen = true;
    private void ClosePopup_Click(object sender, RoutedEventArgs e) => InspectorPopup.IsOpen = false;
}

public sealed class RequiredTextRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        string.IsNullOrWhiteSpace(value as string)
            ? new ValidationResult(false, "A value is required.")
            : ValidationResult.ValidResult;
}
