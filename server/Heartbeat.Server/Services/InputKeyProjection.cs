using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Server.Services;

/// <summary>
/// 把原始版本化键码投影到 Keyboard Heatmap 的 canonical 物理位置。
/// 这是只读投影；历史 InputEvent.Code/CodeSet 永不回写。
/// </summary>
public static class InputKeyProjection
{
    public static bool TryProject(string codeSet, short code, out InputKeyPosition position)
    {
        if (codeSet == InputCodeSets.HeartbeatKeyPositionV1 &&
            Enum.IsDefined(typeof(InputKeyPosition), code))
        {
            position = (InputKeyPosition)code;
            return true;
        }

        if (codeSet == InputCodeSets.WindowsVirtualKeyV1)
            return TryProjectWindowsVirtualKey(code, out position);

        position = default;
        return false;
    }

    private static bool TryProjectWindowsVirtualKey(short code, out InputKeyPosition position)
    {
        if (code is >= 0x41 and <= 0x5A)
        {
            position = (InputKeyPosition)((short)InputKeyPosition.KeyA + code - 0x41);
            return true;
        }

        if (code is >= 0x31 and <= 0x39)
        {
            position = (InputKeyPosition)((short)InputKeyPosition.Digit1 + code - 0x31);
            return true;
        }

        position = code switch
        {
            0x30 => InputKeyPosition.Digit0,
            0x08 => InputKeyPosition.Backspace,
            0x09 => InputKeyPosition.Tab,
            // Generic VK_RETURN / SHIFT / CONTROL / MENU and navigation VKs are ambiguous:
            // historical rows have no scanCode/extended bit, so do not guess a physical side/cluster.
            0xA0 => InputKeyPosition.ShiftLeft,
            0xA1 => InputKeyPosition.ShiftRight,
            0xA2 => InputKeyPosition.ControlLeft,
            0xA3 => InputKeyPosition.ControlRight,
            0xA4 => InputKeyPosition.AltLeft,
            0xA5 => InputKeyPosition.AltRight,
            0x14 => InputKeyPosition.CapsLock,
            0x13 => InputKeyPosition.Pause,
            0x1B => InputKeyPosition.Escape,
            0x20 => InputKeyPosition.Space,
            0x2C => InputKeyPosition.PrintScreen,
            0x5B => InputKeyPosition.MetaLeft,
            0x5C => InputKeyPosition.MetaRight,
            0x5D => InputKeyPosition.ContextMenu,
            0x60 => InputKeyPosition.Numpad0,
            0x61 => InputKeyPosition.Numpad1,
            0x62 => InputKeyPosition.Numpad2,
            0x63 => InputKeyPosition.Numpad3,
            0x64 => InputKeyPosition.Numpad4,
            0x65 => InputKeyPosition.Numpad5,
            0x66 => InputKeyPosition.Numpad6,
            0x67 => InputKeyPosition.Numpad7,
            0x68 => InputKeyPosition.Numpad8,
            0x69 => InputKeyPosition.Numpad9,
            0x6A => InputKeyPosition.NumpadMultiply,
            0x6B => InputKeyPosition.NumpadAdd,
            0x6D => InputKeyPosition.NumpadSubtract,
            0x6E => InputKeyPosition.NumpadDecimal,
            0x6F => InputKeyPosition.NumpadDivide,
            >= 0x70 and <= 0x7B => (InputKeyPosition)((short)InputKeyPosition.F1 + code - 0x70),
            0x90 => InputKeyPosition.NumLock,
            0x91 => InputKeyPosition.ScrollLock,
            0xBA => InputKeyPosition.Semicolon,
            0xBB => InputKeyPosition.Equal,
            0xBC => InputKeyPosition.Comma,
            0xBD => InputKeyPosition.Minus,
            0xBE => InputKeyPosition.Period,
            0xBF => InputKeyPosition.Slash,
            0xC0 => InputKeyPosition.Backquote,
            0xDB => InputKeyPosition.BracketLeft,
            0xDC => InputKeyPosition.Backslash,
            0xDD => InputKeyPosition.BracketRight,
            0xDE => InputKeyPosition.Quote,
            _ => default,
        };

        return position != default;
    }
}
