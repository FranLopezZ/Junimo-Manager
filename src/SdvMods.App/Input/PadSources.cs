using System;
using System.Runtime.InteropServices;

namespace SdvMods.App.Input;

public interface IPadSource : IDisposable
{
    /// <summary>Human-readable state ("SDL loaded, 1 controller: …"), shown in Settings and written to the log to find out why a pad does not work.</summary>
    string Diagnostics { get; }
    /// <summary>False when no controller is connected (or the platform has no controller support).</summary>
    bool TryRead(out PadButtons state);
}

/// <summary>Windows: XInput (Xbox-compatible controllers).</summary>
public sealed class XInputSource : IPadSource
{
    [StructLayout(LayoutKind.Sequential)]
    struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public Gamepad Pad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint Get14(uint index, out State state);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern uint Get91(uint index, out State state);

    bool _use91;
    bool _broken;
    public string Diagnostics { get; private set; } = "XInput";

    public bool TryRead(out PadButtons state)
    {
        state = PadButtons.None;
        if (_broken) return false;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                State s;
                uint rc;
                try { rc = _use91 ? Get91(i, out s) : Get14(i, out s); }
                catch (DllNotFoundException) when (!_use91) { _use91 = true; rc = Get91(i, out s); }
                if (rc != 0) continue;
                Diagnostics = $"XInput: controller {i} connected";
                var b = s.Pad.Buttons;
                const int dead = 16000;
                if ((b & 0x1) != 0 || s.Pad.LY > dead) state |= PadButtons.Up;
                if ((b & 0x2) != 0 || s.Pad.LY < -dead) state |= PadButtons.Down;
                if ((b & 0x4) != 0 || s.Pad.LX < -dead) state |= PadButtons.Left;
                if ((b & 0x8) != 0 || s.Pad.LX > dead) state |= PadButtons.Right;
                if ((b & 0x10) != 0) state |= PadButtons.Start;
                if ((b & 0x20) != 0) state |= PadButtons.Select;
                if ((b & 0x100) != 0) state |= PadButtons.LB;
                if ((b & 0x200) != 0) state |= PadButtons.RB;
                if ((b & 0x1000) != 0) state |= PadButtons.A;
                if ((b & 0x2000) != 0) state |= PadButtons.B;
                if ((b & 0x4000) != 0) state |= PadButtons.X;
                if ((b & 0x8000) != 0) state |= PadButtons.Y;
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _broken = true; Diagnostics = "XInput not available: " + ex.Message; }
        if (!_broken) Diagnostics = "XInput: no controller connected";
        return false;
    }

    public void Dispose() { }
}

/// <summary>Linux / Steam Deck: SDL2 (already present on SteamOS and most desktops). Steam Input exposes the Deck's own controls as a gamepad.</summary>
public sealed class SdlSource : IPadSource
{
    const uint InitGameController = 0x2000;
    static readonly string[] Names = { "libSDL2-2.0.so.0", "libSDL2-2.0.so", "libSDL2.so" };

    static class Native
    {
        const string Lib = "SDL2";
        [DllImport(Lib)] public static extern int SDL_Init(uint flags);
        [DllImport(Lib)] public static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Lib)] public static extern int SDL_NumJoysticks();
        [DllImport(Lib)] public static extern int SDL_IsGameController(int index);
        [DllImport(Lib)] public static extern IntPtr SDL_GameControllerOpen(int index);
        [DllImport(Lib)] public static extern void SDL_GameControllerClose(IntPtr controller);
        [DllImport(Lib)] public static extern byte SDL_GameControllerGetButton(IntPtr controller, int button);
        [DllImport(Lib)] public static extern short SDL_GameControllerGetAxis(IntPtr controller, int axis);
        [DllImport(Lib)] public static extern byte SDL_GameControllerGetAttached(IntPtr controller);
        [DllImport(Lib)] public static extern void SDL_GameControllerUpdate();
        [DllImport(Lib)] public static extern IntPtr SDL_JoystickNameForIndex(int index);
        [DllImport(Lib)] public static extern IntPtr SDL_GameControllerName(IntPtr controller);
    }

    bool _ready, _broken;
    IntPtr _pad;
    DateTime _nextScan = DateTime.MinValue;
    public string Diagnostics { get; private set; } = "SDL2: not started";

    static SdlSource()
    {
        NativeLibrary.SetDllImportResolver(typeof(SdlSource).Assembly, (name, asm, path) =>
        {
            if (name != "SDL2") return IntPtr.Zero;
            foreach (var n in Names) if (NativeLibrary.TryLoad(n, asm, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    bool Init()
    {
        try
        {
            Native.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            _ready = Native.SDL_Init(InitGameController) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _broken = true;
            Diagnostics = "SDL2 library not found (" + ex.GetType().Name + "). Controller input needs libSDL2-2.0.so.0.";
            return false;
        }
        if (!_ready) { _broken = true; Diagnostics = "SDL2 could not start its game controller subsystem."; }
        return _ready;
    }

    public bool TryRead(out PadButtons state)
    {
        state = PadButtons.None;
        if (_broken) return false;
        if (!_ready && !Init()) return false;
        try
        {
            Native.SDL_GameControllerUpdate();
            if (_pad != IntPtr.Zero && Native.SDL_GameControllerGetAttached(_pad) == 0) { Native.SDL_GameControllerClose(_pad); _pad = IntPtr.Zero; }
            if (_pad == IntPtr.Zero)
            {
                if (DateTime.UtcNow < _nextScan) return false;
                _nextScan = DateTime.UtcNow.AddSeconds(2);
                var count = Native.SDL_NumJoysticks();
                var seen = new System.Text.StringBuilder();
                for (var i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUTF8(Native.SDL_JoystickNameForIndex(i)) ?? "?";
                    var isPad = Native.SDL_IsGameController(i) != 0;
                    seen.Append($" [{name}{(isPad ? "" : " (not a gamepad)")}]");
                    if (isPad && (_pad = Native.SDL_GameControllerOpen(i)) != IntPtr.Zero) break;
                }
                if (_pad == IntPtr.Zero)
                {
                    Diagnostics = count == 0
                        ? "SDL2 loaded but sees 0 game controllers (Steam Input may be turning the controller into keyboard/mouse: choose the \"Gamepad\" layout for this shortcut)."
                        : $"SDL2 sees {count} device(s) but none is usable as a gamepad:{seen}";
                    return false;
                }
                Diagnostics = "SDL2 controller: " + (Marshal.PtrToStringUTF8(Native.SDL_GameControllerName(_pad)) ?? "?");
            }
            bool B(int button) => Native.SDL_GameControllerGetButton(_pad, button) != 0;
            var lx = Native.SDL_GameControllerGetAxis(_pad, 0);
            var ly = Native.SDL_GameControllerGetAxis(_pad, 1);
            const int dead = 16000;
            if (B(11) || ly < -dead) state |= PadButtons.Up;
            if (B(12) || ly > dead) state |= PadButtons.Down;
            if (B(13) || lx < -dead) state |= PadButtons.Left;
            if (B(14) || lx > dead) state |= PadButtons.Right;
            if (B(0)) state |= PadButtons.A;
            if (B(1)) state |= PadButtons.B;
            if (B(2)) state |= PadButtons.X;
            if (B(3)) state |= PadButtons.Y;
            if (B(4)) state |= PadButtons.Select;
            if (B(6)) state |= PadButtons.Start;
            if (B(9)) state |= PadButtons.LB;
            if (B(10)) state |= PadButtons.RB;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _broken = true;
            Diagnostics = "SDL2 call failed: " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_pad != IntPtr.Zero) { try { Native.SDL_GameControllerClose(_pad); } catch { } _pad = IntPtr.Zero; }
    }
}

public static class PadSources
{
    public static IPadSource? Create()
        => OperatingSystem.IsWindows() ? new XInputSource() : OperatingSystem.IsLinux() ? new SdlSource() : null;
}
