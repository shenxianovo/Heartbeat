namespace Heartbeat.Collector.Desktop.Windows;

internal static class WindowsInputTranslator
{
    // Set 1 scan codes identify physical positions; virtual keys / keyboard layout are not used.
    private static readonly Dictionary<int, InputKeyPosition> Positions = new()
    {
        [0x01] = InputKeyPosition.Escape, [0x0e] = InputKeyPosition.Backspace,
        [0x0f] = InputKeyPosition.Tab, [0x1c] = InputKeyPosition.Enter,
        [0x1d] = InputKeyPosition.ControlLeft, [0x2a] = InputKeyPosition.ShiftLeft,
        [0x36] = InputKeyPosition.ShiftRight, [0x38] = InputKeyPosition.AltLeft,
        [0x39] = InputKeyPosition.Space, [0x3a] = InputKeyPosition.CapsLock,
        [0x45] = InputKeyPosition.NumLock, [0x46] = InputKeyPosition.ScrollLock,
        [0x56] = InputKeyPosition.IntlBackslash, [0x57] = InputKeyPosition.F11, [0x58] = InputKeyPosition.F12,
        [0x11c] = InputKeyPosition.NumpadEnter, [0x11d] = InputKeyPosition.ControlRight,
        [0x145] = InputKeyPosition.NumLock, [0x135] = InputKeyPosition.NumpadDivide, [0x137] = InputKeyPosition.PrintScreen,
        [0x138] = InputKeyPosition.AltRight, [0x147] = InputKeyPosition.Home,
        [0x148] = InputKeyPosition.ArrowUp, [0x149] = InputKeyPosition.PageUp,
        [0x14b] = InputKeyPosition.ArrowLeft, [0x14d] = InputKeyPosition.ArrowRight,
        [0x14f] = InputKeyPosition.End, [0x150] = InputKeyPosition.ArrowDown,
        [0x151] = InputKeyPosition.PageDown, [0x152] = InputKeyPosition.Insert,
        [0x153] = InputKeyPosition.Delete, [0x15b] = InputKeyPosition.MetaLeft,
        [0x15c] = InputKeyPosition.MetaRight, [0x15d] = InputKeyPosition.ContextMenu,
    };

    static WindowsInputTranslator()
    {
        AddRow(0x02, [InputKeyPosition.Digit1, InputKeyPosition.Digit2, InputKeyPosition.Digit3,
            InputKeyPosition.Digit4, InputKeyPosition.Digit5, InputKeyPosition.Digit6, InputKeyPosition.Digit7,
            InputKeyPosition.Digit8, InputKeyPosition.Digit9, InputKeyPosition.Digit0, InputKeyPosition.Minus, InputKeyPosition.Equal]);
        AddRow(0x10, [InputKeyPosition.KeyQ, InputKeyPosition.KeyW, InputKeyPosition.KeyE, InputKeyPosition.KeyR,
            InputKeyPosition.KeyT, InputKeyPosition.KeyY, InputKeyPosition.KeyU, InputKeyPosition.KeyI, InputKeyPosition.KeyO,
            InputKeyPosition.KeyP, InputKeyPosition.BracketLeft, InputKeyPosition.BracketRight]);
        AddRow(0x1e, [InputKeyPosition.KeyA, InputKeyPosition.KeyS, InputKeyPosition.KeyD, InputKeyPosition.KeyF,
            InputKeyPosition.KeyG, InputKeyPosition.KeyH, InputKeyPosition.KeyJ, InputKeyPosition.KeyK, InputKeyPosition.KeyL,
            InputKeyPosition.Semicolon, InputKeyPosition.Quote, InputKeyPosition.Backquote]);
        AddRow(0x2b, [InputKeyPosition.Backslash, InputKeyPosition.KeyZ, InputKeyPosition.KeyX, InputKeyPosition.KeyC,
            InputKeyPosition.KeyV, InputKeyPosition.KeyB, InputKeyPosition.KeyN, InputKeyPosition.KeyM,
            InputKeyPosition.Comma, InputKeyPosition.Period, InputKeyPosition.Slash]);
        AddRow(0x3b, [InputKeyPosition.F1, InputKeyPosition.F2, InputKeyPosition.F3, InputKeyPosition.F4,
            InputKeyPosition.F5, InputKeyPosition.F6, InputKeyPosition.F7, InputKeyPosition.F8, InputKeyPosition.F9, InputKeyPosition.F10]);
        AddRow(0x47, [InputKeyPosition.Numpad7, InputKeyPosition.Numpad8, InputKeyPosition.Numpad9,
            InputKeyPosition.NumpadSubtract, InputKeyPosition.Numpad4, InputKeyPosition.Numpad5, InputKeyPosition.Numpad6,
            InputKeyPosition.NumpadAdd, InputKeyPosition.Numpad1, InputKeyPosition.Numpad2, InputKeyPosition.Numpad3,
            InputKeyPosition.Numpad0, InputKeyPosition.NumpadDecimal]);
        Positions[0x37] = InputKeyPosition.NumpadMultiply;
    }

    private static void AddRow(int start, InputKeyPosition[] positions)
    {
        for (var index = 0; index < positions.Length; index++) Positions[start + index] = positions[index];
    }

    public static DesktopInputObservation? Keyboard(ushort scanCode, ushort flags)
    {
        // E1 includes Pause's multi-byte prefix; do not invent a key-up for this sequence.
        if ((flags & 4) != 0 || scanCode is 0 or 0xff) return null;
        var scan = scanCode | ((flags & 2) != 0 ? 0x100 : 0);
        return Positions.TryGetValue(scan, out var position)
            ? new DesktopInputObservation((flags & 1) == 0 ? DesktopInputKind.KeyDown : DesktopInputKind.KeyUp, (int)position)
            : null;
    }

    public static IEnumerable<DesktopInputObservation> Mouse(ushort flags, ushort data)
    {
        // One-based left/right/middle/X1/X2 buttons, matching the desktop protocol.
        int[] buttons = [1, 2, 3, 4, 5];
        for (var index = 0; index < buttons.Length; index++)
            if ((flags & (1 << (index * 2))) != 0)
                yield return new DesktopInputObservation(DesktopInputKind.MouseButtonDown, buttons[index]);
        var ticks = unchecked((short)data) / 120.0;
        if ((flags & 0x0400) != 0 && ticks != 0)
            yield return new DesktopInputObservation(DesktopInputKind.Scroll, DeltaY: ticks, ScrollUnit: ScrollUnit.Line);
        if ((flags & 0x0800) != 0 && ticks != 0)
            yield return new DesktopInputObservation(DesktopInputKind.Scroll, DeltaX: ticks, ScrollUnit: ScrollUnit.Line);
    }
}
