using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SdvMods.App.ViewModels;

namespace SdvMods.App.Input;

/// <summary>
/// Lets a game controller drive the window: D-pad / left stick move the focus, A presses, B goes back / closes the
/// mod sheet, LB/RB switch tabs, X checks for updates, Y syncs, Start launches the game. Only acts while the window is active.
/// </summary>
public sealed class GamepadNavigator
{
    readonly Window _window;
    readonly MainViewModel _vm;
    readonly IPadSource? _source;
    readonly PadMapper _mapper = new();
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public GamepadNavigator(Window window, MainViewModel vm)
    {
        _window = window;
        _vm = vm;
        _source = PadSources.Create();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    string _lastDiagnostics = "";

    public void Start()
    {
        GamepadLog.Startup(_vm.IsPadMode);
        if (_source is null) { _vm.PadStatus = "Controller input is not supported on this system."; return; }
        _timer.Start();
        _window.Closed += (_, _) => { _timer.Stop(); _source.Dispose(); };
    }

    void Tick()
    {
        // In Gaming Mode there is a single full-screen window and IsActive can be unreliable, so do not insist on it there.
        if (_source is null || (!_window.IsActive && !_vm.IsPadMode)) return;
        var connected = _source.TryRead(out var state);
        if (_source.Diagnostics != _lastDiagnostics)
        {
            _lastDiagnostics = _source.Diagnostics;
            _vm.PadStatus = _lastDiagnostics;
            GamepadLog.Write(_lastDiagnostics);
        }
        _vm.ControllerConnected = connected;
        if (!connected) return;
        foreach (var action in _mapper.Update(state, _clock.Elapsed)) Apply(action);
    }

    /// <summary>
    /// Keyboard path: if Steam Input (or a keyboard) delivers arrows / Esc / PageUp / PageDown instead of a gamepad,
    /// they drive the same navigation. Called from the window for keys no control handled.
    /// </summary>
    public bool HandleKey(Key key)
    {
        GamepadLog.KeyEvent(key.ToString());
        switch (key)
        {
            case Key.Up: Move(NavigationDirection.Up, Key.Up); return true;
            case Key.Down: Move(NavigationDirection.Down, Key.Down); return true;
            case Key.Left: Move(NavigationDirection.Left, Key.Left); return true;
            case Key.Right: Move(NavigationDirection.Right, Key.Right); return true;
            case Key.Escape: Back(); return true;
            case Key.PageUp: SwitchTab(-1); return true;
            case Key.PageDown: SwitchTab(+1); return true;
        }
        return false;
    }

    IInputElement? Focused => TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();

    void Apply(PadAction action)
    {
        switch (action)
        {
            case PadAction.Up: Move(NavigationDirection.Up, Key.Up); break;
            case PadAction.Down: Move(NavigationDirection.Down, Key.Down); break;
            case PadAction.Left: Move(NavigationDirection.Left, Key.Left); break;
            case PadAction.Right: Move(NavigationDirection.Right, Key.Right); break;
            case PadAction.Accept: Accept(); break;
            case PadAction.Back: Back(); break;
            case PadAction.PrevTab: SwitchTab(-1); break;
            case PadAction.NextTab: SwitchTab(+1); break;
            case PadAction.X: if (_vm.CheckUpdatesCommand.CanExecute(null)) _vm.CheckUpdatesCommand.Execute(null); break;
            case PadAction.Y: if (_vm.SyncNowCommand.CanExecute(null)) _vm.SyncNowCommand.Execute(null); break;
            case PadAction.Start: if (_vm.CanPlay) _vm.PlayCommand.Execute(null); break;
        }
    }

    static bool InsideOpenDropDown(IInputElement? f)
        => f is Visual v && (v.FindAncestorOfType<ComboBox>() is { IsDropDownOpen: true } || v is ComboBox { IsDropDownOpen: true });

    void Move(NavigationDirection dir, Key arrow)
    {
        var focused = Focused;
        if (focused is null) { FocusFirstInTab(); return; }

        // Inside an open drop-down or a list, the arrows move the selection like on a keyboard.
        var vertical = dir is NavigationDirection.Up or NavigationDirection.Down;
        if (InsideOpenDropDown(focused) || (vertical && focused is ListBoxItem)) { SendKey(focused, arrow); return; }

        var tl = TopLevel.GetTopLevel(_window);
        tl?.FocusManager?.TryMoveFocus(dir);

        // The mod sheet is modal: keep the focus inside it.
        if (_vm.ModalOverlayName is { } name && Focused is Visual now && _window.FindControl<Control>(name) is { } overlay && !overlay.IsVisualAncestorOf(now))
            FocusFirstIn(overlay);
    }

    void Accept()
    {
        var f = Focused;
        if (f is null) { FocusFirstInTab(); return; }
        switch (f)
        {
            case TextBox:
                if (OperatingSystem.IsLinux()) Services.Shell.OpenUrl("steam://open/keyboard"); // Steam's on-screen keyboard (Gaming Mode)
                break;
            case ToggleButton or ListBoxItem or TabItem:
                SendKey(f, Key.Space);
                break;
            default:
                SendKey(f, Key.Enter);
                break;
        }
    }

    void Back()
    {
        if (Focused is Visual v && InsideOpenDropDown(Focused)) { (v as ComboBox ?? v.FindAncestorOfType<ComboBox>())!.IsDropDownOpen = false; return; }
        if (_vm.ModalOpen) { _vm.CloseTopModal(); Dispatcher.UIThread.Post(FocusFirstInTab, DispatcherPriority.Background); }
    }

    void SwitchTab(int delta)
    {
        if (_vm.ModalOpen) return;
        var n = 5;
        _vm.SelectedTab = (_vm.SelectedTab + delta + n) % n;
        Dispatcher.UIThread.Post(FocusFirstInTab, DispatcherPriority.Background);
    }

    public void FocusFirstInTab()
    {
        if (_vm.ModalOverlayName is { } name && _window.FindControl<Control>(name) is { } overlay) { FocusFirstIn(overlay); return; }
        if (_window.FindControl<TabControl>("MainTabs") is { } tabs) FocusFirstIn(tabs);
    }

    static void FocusFirstIn(Control root)
    {
        var target = root.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c is not TabItem && c is not ScrollViewer);
        target?.Focus(NavigationMethod.Directional);
    }

    static void SendKey(IInputElement target, Key key)
    {
        if (target is not Interactive i) return;
        i.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = i });
        i.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = i });
    }
}
