using System.Text;
using System.Windows.Input;

namespace NitTray.Services;

[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

// One global shortcut: at least one modifier plus a non-modifier key. Stored in
// settings.json as invariant names ("Win+Ctrl+Up") and shown to the user with
// spaced, friendly names ("Win + Ctrl + Up").
public sealed record HotKeyBinding(HotKeyModifiers Modifiers, Key Key)
{
    // Windows itself leaves Win+Ctrl+Up/Down unassigned (Win+Ctrl+Left/Right are the
    // virtual-desktop switches), which makes them the least collision-prone arrow
    // combination available. See src/app/README.md for the combos we ruled out.
    public static readonly HotKeyBinding DefaultBrightnessUp =
        new(HotKeyModifiers.Win | HotKeyModifiers.Control, Key.Up);

    public static readonly HotKeyBinding DefaultBrightnessDown =
        new(HotKeyModifiers.Win | HotKeyModifiers.Control, Key.Down);

    // Shift on its own is not enough: Shift+<key> is a typing and text-selection chord
    // in every application, and a global registration would swallow it system-wide.
    private const HotKeyModifiers RequiredModifiers =
        HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Win;

    public bool IsValid =>
        (Modifiers & RequiredModifiers) != 0 && !IsModifierKey(Key) && VirtualKey != 0;

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    // MOD_* flags for RegisterHotKey; the enum values already match.
    public uint NativeModifiers => (uint)Modifiers;

    public string DisplayText => Format(" + ", KeyDisplayName(Key));

    public string ToStorageString() => Format("+", Key.ToString());

    // Key.System is what WPF reports while Alt is held (the real key arrives in
    // KeyEventArgs.SystemKey), so it can never be the bound key either.
    public static bool IsModifierKey(Key key) => key is Key.None
        or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System or Key.ImeProcessed;

    public static HotKeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var modifiers = HotKeyModifiers.None;
        Key? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "win" or "windows" or "meta":
                    modifiers |= HotKeyModifiers.Win;
                    continue;
                case "ctrl" or "control":
                    modifiers |= HotKeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotKeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotKeyModifiers.Shift;
                    continue;
            }

            // Enum.TryParse also accepts raw numbers, so reject undefined values.
            if (key is not null
                || !Enum.TryParse(token, ignoreCase: true, out Key parsed)
                || !Enum.IsDefined(parsed))
            {
                return null;
            }
            key = parsed;
        }

        if (key is not Key value)
        {
            return null;
        }

        var binding = new HotKeyBinding(modifiers, value);
        return binding.IsValid ? binding : null;
    }

    private string Format(string separator, string keyName)
    {
        var builder = new StringBuilder();

        // Windows writes the Win key first, then Ctrl, Alt, Shift.
        if (Modifiers.HasFlag(HotKeyModifiers.Win))
        {
            builder.Append("Win").Append(separator);
        }
        if (Modifiers.HasFlag(HotKeyModifiers.Control))
        {
            builder.Append("Ctrl").Append(separator);
        }
        if (Modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            builder.Append("Alt").Append(separator);
        }
        if (Modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            builder.Append("Shift").Append(separator);
        }

        return builder.Append(keyName).ToString();
    }

    private static string KeyDisplayName(Key key) => key switch
    {
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.Add => "Numpad +",
        Key.Subtract => "Numpad -",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.Space => "Space",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Numpad {(char)('0' + (key - Key.NumPad0))}",
        _ => key.ToString(),
    };
}
