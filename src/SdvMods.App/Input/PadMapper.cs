using System;
using System.Collections.Generic;

namespace SdvMods.App.Input;

[Flags]
public enum PadButtons
{
    None = 0, Up = 1, Down = 2, Left = 4, Right = 8, A = 16, B = 32, X = 64, Y = 128,
    LB = 256, RB = 512, Start = 1024, Select = 2048,
}

public enum PadAction { Up, Down, Left, Right, Accept, Back, PrevTab, NextTab, X, Y, Start, Select }

/// <summary>Turns raw controller state into discrete actions: one per press, directions auto-repeat while held.</summary>
public sealed class PadMapper
{
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(420);
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(110);

    PadButtons _previous;
    readonly Dictionary<PadButtons, TimeSpan> _nextRepeat = new();

    static readonly (PadButtons Button, PadAction Action, bool Repeats)[] Map =
    {
        (PadButtons.Up, PadAction.Up, true), (PadButtons.Down, PadAction.Down, true),
        (PadButtons.Left, PadAction.Left, true), (PadButtons.Right, PadAction.Right, true),
        (PadButtons.A, PadAction.Accept, false), (PadButtons.B, PadAction.Back, false),
        (PadButtons.X, PadAction.X, false), (PadButtons.Y, PadAction.Y, false),
        (PadButtons.LB, PadAction.PrevTab, false), (PadButtons.RB, PadAction.NextTab, false),
        (PadButtons.Start, PadAction.Start, false), (PadButtons.Select, PadAction.Select, false),
    };

    public List<PadAction> Update(PadButtons state, TimeSpan now)
    {
        var actions = new List<PadAction>();
        foreach (var (button, action, repeats) in Map)
        {
            var down = state.HasFlag(button);
            var wasDown = _previous.HasFlag(button);
            if (down && !wasDown)
            {
                actions.Add(action);
                if (repeats) _nextRepeat[button] = now + RepeatDelay;
            }
            else if (down && repeats && _nextRepeat.TryGetValue(button, out var due) && now >= due)
            {
                actions.Add(action);
                _nextRepeat[button] = now + RepeatEvery;
            }
        }
        _previous = state;
        return actions;
    }
}
