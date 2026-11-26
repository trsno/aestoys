using System;
using System.Collections.Generic;

namespace Aestoys.Features.AltTab;

internal interface ISwitcherView
{
    /// <summary>User clicked an item: switch to it.</summary>
    event Action<int>? ItemClicked;
    /// <summary>User clicked the background: close without switching.</summary>
    event Action? CancelRequested;
    /// <summary>Mouse wheel: +1 next, -1 previous.</summary>
    event Action<int>? Scrolled;

    bool IsShown { get; }
    void ShowOn(IntPtr monitor, IReadOnlyList<SwitchItem> items, int index);
    void Select(int index);
    void HideView();
    void CloseView();
}
