﻿using System;
using System.Collections.Generic;
using System.Windows.Forms;

// TODO: This should be moved to the Application project, but AppBarWindow needs TLC first

namespace CairoDesktop.SupportingClasses
{
    public interface IWindowService : IDisposable
    {
        List<CairoAppBarWindow> Windows { get; }

        void Register();

        void HandleScreenAdded(Screen screen);

        void RefreshWindows(WindowManagerEventArgs args);

        void HandleScreenRemoved(string screenDeviceName);
    }
}
