using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SampleWpfApp;

public record CollectionItem(string Title, string Description, string ActionId);

public sealed class SemanticNavigationItem : NavigationViewItem
{
    public static readonly DependencyProperty InvocationCountProperty = DependencyProperty.Register(
        nameof(InvocationCount), typeof(int), typeof(SemanticNavigationItem), new PropertyMetadata(0));

    public int InvocationCount
    {
        get => (int)GetValue(InvocationCountProperty);
        private set => SetValue(InvocationCountProperty, value);
    }

    protected override void OnClick()
    {
        InvocationCount++;
        base.OnClick();
    }
}

public partial class MainWindow : FluentWindow
{
    public ObservableCollection<CollectionItem> CollectionItems { get; } = new()
    {
        new("Quarterly Planning", "A shared roadmap and milestone review", "OpenQuarterlyPlanning"),
        new("Research Notes", "Collected findings for the current initiative", "OpenResearchNotes"),
        new("Design Review", "Open decisions and annotated mockups", "OpenDesignReview"),
        new("Team Retrospective", "Actions and observations from the last cycle", "OpenTeamRetrospective")
    };

    public string RequiredValue { get; set; } = "Required value";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        CollectionListView.ItemsSource = CollectionItems;
        SetActiveNavigation(NavDashboardBtn);
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Visible;
        CollectionView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        SetActiveNavigation(NavDashboardBtn);
        StatusLabel.Text = "Status: Navigated to Dashboard";
    }

    private void NavCollection_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CollectionView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        SetActiveNavigation(NavCollectionBtn);
        StatusLabel.Text = "Status: Navigated to Collection";
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        CollectionView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SetActiveNavigation(NavSettingsBtn);
        StatusLabel.Text = "Status: Navigated to Settings";
    }

    private void ToggleOverride_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Status: Highlight Toggled!";
    }

    private void OpenRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { DataContext: CollectionItem item })
            StatusLabel.Text = $"Status: Opened {item.Title}";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => StatusLabel.Text = "Status: Settings Saved";

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

    private void OpenDialogWindow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Modal Test Dialog",
            Width = 350,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = "Modal dialog opened with ShowDialog()", Margin = new Thickness(0, 0, 0, 16) },
                    new System.Windows.Controls.Button
                    {
                        Name = "CloseModalDialogBtn",
                        Content = "Close Dialog",
                        Height = 32
                    }
                }
            }
        };
        var closeBtn = (System.Windows.Controls.Button)((StackPanel)dialog.Content).Children[1];
        System.Windows.Automation.AutomationProperties.SetAutomationId(closeBtn, "CloseModalDialogBtn");
        closeBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        var result = dialog.ShowDialog();
        StatusLabel.Text = result == true ? "Status: Modal Dialog Confirmed" : "Status: Modal Dialog Dismissed";
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

    private void SetActiveNavigation(Wpf.Ui.Controls.Button active)
    {
        NavDashboardBtn.Appearance = ReferenceEquals(active, NavDashboardBtn) ? ControlAppearance.Primary : ControlAppearance.Secondary;
        NavCollectionBtn.Appearance = ReferenceEquals(active, NavCollectionBtn) ? ControlAppearance.Primary : ControlAppearance.Secondary;
        NavSettingsBtn.Appearance = ReferenceEquals(active, NavSettingsBtn) ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }
}

public sealed class RequiredTextRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        string.IsNullOrWhiteSpace(value as string)
            ? new ValidationResult(false, "A value is required.")
            : ValidationResult.ValidResult;
}
