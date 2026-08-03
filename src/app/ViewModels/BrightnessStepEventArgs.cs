namespace NitTray.ViewModels;

// Outcome of one global brightness shortcut press, used to drive the on-screen
// overlay. Percent is null when nothing was adjusted.
public sealed class BrightnessStepEventArgs : EventArgs
{
    public static readonly BrightnessStepEventArgs NoDisplay =
        new(null, "No Apple display connected");

    public BrightnessStepEventArgs(int? percent, string caption)
    {
        Percent = percent;
        Caption = caption;
    }

    public int? Percent { get; }

    public string Caption { get; }
}
