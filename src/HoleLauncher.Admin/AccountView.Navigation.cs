using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private const double NarrowLayoutWidth = 820;
    private const double NavigationWidth = 190;
    private Guid? currentAccountId;
    private Button? activeSection;

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedIndex: >= 0 } picker)
        {
            var language = picker.SelectedIndex == 1 ? "en" : "ru";
            if (language != Program.GetLanguage())
                Program.SetLanguage(language);
        }
    }

    private void OnProfileNav(object? sender, RoutedEventArgs e) => SelectSection(ProfileNav);

    private void OnServerNav(object? sender, RoutedEventArgs e) => SelectSection(ServerNav);

    private void OnUsersNav(object? sender, RoutedEventArgs e) => SelectSection(UsersNav);

    private void OnTogglePassword(object? sender, RoutedEventArgs e) =>
        AccountOptions.IsVisible = !AccountOptions.IsVisible;

    private void SelectSection(Button button)
    {
        activeSection = button;
        UpdateSection();
    }

    private void UpdateSection()
    {
        if (activeSection?.IsVisible != true)
            activeSection = ProfileNav;
        ProfileContent.IsVisible = activeSection == ProfileNav;
        ServicesPanel.IsVisible = activeSection == ServerNav;
        UserManagementPanel.IsVisible = activeSection == UsersNav;
        RepositoryPanel.IsVisible = activeSection == RepositoryNav;
        OfferPanel.IsVisible = activeSection == OfferNav;
        OfferNav.Classes.Set("selected", activeSection == OfferNav);
        RepositoryNav.Classes.Set("selected", activeSection == RepositoryNav);
        StoragePanel.IsVisible = activeSection == StorageNav;
        StorageNav.Classes.Set("selected", activeSection == StorageNav);
        ProfileNav.Classes.Set("selected", activeSection == ProfileNav);
        ServerNav.Classes.Set("selected", activeSection == ServerNav);
        UsersNav.Classes.Set("selected", activeSection == UsersNav);
    }

    private void UpdateDashboardLayout()
    {
        var narrow = Bounds.Width < NarrowLayoutWidth;
        Dashboard.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(NavigationWidth);
        Dashboard.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        NavigationButtons.Orientation = narrow ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
        Grid.SetRowSpan(Navigation, narrow ? 1 : 2);
        Grid.SetColumn(DashboardContent, narrow ? 0 : 1);
        Grid.SetRow(DashboardContent, narrow ? 1 : 0);
        Grid.SetRowSpan(DashboardContent, narrow ? 1 : 2);
    }
}
