using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HoleLauncher.Admin;

public sealed class ToggleablePasswordTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        var wasRevealed = RevealPassword;
        base.OnLostFocus(e);
        if (wasRevealed)
            SetCurrentValue(RevealPasswordProperty, true);
    }
}
