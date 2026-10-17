using System.Text;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Kebuz.Recorder.Input;

internal sealed record Hotkey(HOT_KEY_MODIFIERS Modifiers, VIRTUAL_KEY Key)
{
    public static Hotkey Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new ArgumentException($"Пустое сочетание клавиш: {text}");
        }

        var modifiers = default(HOT_KEY_MODIFIERS);
        VIRTUAL_KEY? key = null;

        foreach (var part in parts)
        {
            var modifier = ParseModifier(part);

            if (modifier is not null)
            {
                if (key is not null)
                {
                    throw new ArgumentException($"Модификатор {part} стоит после клавиши: {text}");
                }

                modifiers |= modifier.Value;
                continue;
            }

            if (key is not null)
            {
                throw new ArgumentException($"В сочетании больше одной клавиши: {text}");
            }

            key = ParseKey(part) ?? throw new ArgumentException($"Неизвестная клавиша {part}: {text}");
        }

        if (key is null)
        {
            throw new ArgumentException($"В сочетании нет клавиши, только модификаторы: {text}");
        }

        return new Hotkey(modifiers, key.Value);
    }

    public override string ToString()
    {
        var text = new StringBuilder();

        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_CONTROL))
        {
            text.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_ALT))
        {
            text.Append("Alt+");
        }

        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_SHIFT))
        {
            text.Append("Shift+");
        }

        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_WIN))
        {
            text.Append("Win+");
        }

        return text.Append(Key.ToString()["VK_".Length..]).ToString();
    }

    private static HOT_KEY_MODIFIERS? ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HOT_KEY_MODIFIERS.MOD_CONTROL,
        "alt" => HOT_KEY_MODIFIERS.MOD_ALT,
        "shift" => HOT_KEY_MODIFIERS.MOD_SHIFT,
        "win" or "windows" => HOT_KEY_MODIFIERS.MOD_WIN,
        _ => null
    };

    private static VIRTUAL_KEY? ParseKey(string part)
    {
        var name = part.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            ? part.ToUpperInvariant()
            : "VK_" + part.ToUpperInvariant();

        return Enum.TryParse<VIRTUAL_KEY>(name, out var key) && Enum.IsDefined(key) ? key : null;
    }
}

public static class HotkeyRules
{
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var hotkey = Hotkey.Parse(text);
            return Allowed(hotkey.Modifiers, hotkey.Key);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsModifier(int virtualKey)
    {
        var key = (VIRTUAL_KEY)virtualKey;
        return Enum.IsDefined(key) && Modifier(key);
    }

    public static bool TryFormat(
        int virtualKey,
        bool ctrl,
        bool alt,
        bool shift,
        bool win,
        out string text,
        out string complaint)
    {
        text = string.Empty;
        complaint = string.Empty;

        var key = (VIRTUAL_KEY)virtualKey;

        if (!Enum.IsDefined(key) || Modifier(key))
        {
            complaint = Localization.Strings.Get("Hotkey.NeedModifier");
            return false;
        }

        var modifiers = default(HOT_KEY_MODIFIERS);

        if (ctrl)
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        }

        if (alt)
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
        }

        if (shift)
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        }

        if (win)
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;
        }

        if (!Allowed(modifiers, key))
        {
            complaint = shift
                ? Localization.Strings.Get("Hotkey.ShiftAlone")
                : Localization.Strings.Get("Hotkey.NeedCtrl");
            return false;
        }

        text = new Hotkey(modifiers, key).ToString();
        return true;
    }

    private static bool Allowed(HOT_KEY_MODIFIERS modifiers, VIRTUAL_KEY key)
    {
        if (Modifier(key))
        {
            return false;
        }

        var strong = modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_CONTROL)
            || modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_ALT)
            || modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_WIN);

        return strong || Function(key);
    }

    private static bool Function(VIRTUAL_KEY key)
    {
        return key >= VIRTUAL_KEY.VK_F1 && key <= VIRTUAL_KEY.VK_F24;
    }

    private static bool Modifier(VIRTUAL_KEY key) => key switch
    {
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL => true,
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU => true,
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT => true,
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN => true,
        _ => false
    };
}
