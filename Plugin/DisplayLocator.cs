using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenTabletDriver.Plugin.Platform.Display;

namespace PenCalibration
{
    /// <summary>
    /// Works out which monitor the pen is on, so the calibration app can open on the pen display.
    /// </summary>
    public static class DisplayLocator
    {
        private static IVirtualScreen? screen;

        /// <summary>
        /// Index of the display containing <paramref name="position"/> (OTD output coordinates),
        /// counting displays left to right, then top to bottom; and how many displays there are.
        /// </summary>
        public static (int Index, int Count)? Locate(Vector2 position)
        {
            var displays = GetDisplays();
            if (displays.Count == 0)
                return null;

            // Output coordinates start at the virtual screen's top-left corner, like OTD's own
            // "Set to display" menu does it.
            var virtualScreen = screen!;
            float xOffset = displays.Min(d => d.Position.X) - virtualScreen.Position.X;
            float yOffset = displays.Min(d => d.Position.Y) - virtualScreen.Position.Y;

            // The display containing the point, or the nearest one if the pen is outside them all.
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                float left = d.Position.X - xOffset, top = d.Position.Y - yOffset;
                float dx = Math.Max(0, Math.Max(left - position.X, position.X - (left + d.Width)));
                float dy = Math.Max(0, Math.Max(top - position.Y, position.Y - (top + d.Height)));
                float distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return (best, displays.Count);
        }

        private static List<IDisplay> GetDisplays()
        {
            screen ??= FindVirtualScreen();
            if (screen == null)
                return new List<IDisplay>();

            return screen.Displays
                .Where(d => d is not IVirtualScreen)
                .OrderBy(d => d.Position.X)
                .ThenBy(d => d.Position.Y)
                .ToList();
        }

        // Filters don't get OTD's services injected, so read the daemon's static
        // DesktopInterop.VirtualScreen through reflection (checked against 0.6.x).
        private static IVirtualScreen? FindVirtualScreen()
        {
            var interop = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("OpenTabletDriver.Desktop.Interop.DesktopInterop", false))
                .FirstOrDefault(t => t != null);
            return interop?.GetProperty("VirtualScreen")?.GetValue(null) as IVirtualScreen;
        }
    }
}
