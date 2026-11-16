using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StupidDict.App.Settings;

/// <summary>What the user picked the one time the close prompt appeared.</summary>
public enum CloseToTrayChoice
{
    /// <summary>Hide the window and keep the app in the tray from now on.</summary>
    MinimizeToTray = 0,
    /// <summary>Quit now and on every later close (the setting stays off).</summary>
    Quit = 1,
    /// <summary>The dialog was dismissed: keep running and ask again next close.</summary>
    Cancel = 2,
}

/// <summary>
/// The one-time "minimize to the tray when the window closes?" dialog shown on
/// the first close. Modal on the main window; the answer is persisted by the
/// caller, the dialog itself only reports the choice.
/// </summary>
public partial class CloseToTrayPrompt : Window
{
    /// <summary>Null until a button is pressed (dismissed dialog = cancel).</summary>
    public CloseToTrayChoice? Choice { get; private set; }

    public CloseToTrayPrompt()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A modal window does not take keyboard focus by itself (same reason
        // SettingsWindow focuses explicitly): without this the first Tab/Enter
        // would be routed to the disabled main window underneath.
        QuitButton.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Choice = CloseToTrayChoice.Cancel;
            Close();
        }
    }

    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        Choice = CloseToTrayChoice.MinimizeToTray;
        Close();
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        Choice = CloseToTrayChoice.Quit;
        Close();
    }
}
