using Avalonia.Input;

namespace HaCue2.Presentation;

/// <summary>One canonical text form shared by hotkeys, keyboard-trigger learn, and matching.</summary>
public static class KeyboardGestureText
{
    public static string Format(Key key, KeyModifiers modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return "";

        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Meta");

        parts.Add(key switch
        {
            Key.Escape => "Esc",
            Key.Space => "Space",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => key.ToString(),
        });
        return string.Join('+', parts);
    }
}
