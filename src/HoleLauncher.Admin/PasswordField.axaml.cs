using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HoleLauncher.Admin;

public sealed partial class PasswordField : UserControl
{
    public PasswordField() => InitializeComponent();

    public string? Text => PasswordInput.Text;
    public bool IsInputFocused => PasswordInput.IsFocused;

    public string? Watermark
    {
        get => PasswordInput.Watermark;
        set => PasswordInput.Watermark = value;
    }

    public void Clear() => PasswordInput.Clear();

    private void OnPasswordVisibilityChanged(object? sender, RoutedEventArgs e) =>
        PasswordInput.RevealPassword = PasswordVisibility.IsChecked == true;
}
