using System.Runtime.InteropServices;
using System.Text;

namespace CodexStatusbar.Quota;

internal sealed record HelpAnchor(IntRect Help, string Source);

internal static class HelpMenuAnchor
{
    public static HelpAnchor Measure(CodexWindowInfo host)
    {
        var menu = GetMenu(host.Handle);
        if (menu != IntPtr.Zero)
        {
            for (var index = 0; index < GetMenuItemCount(menu); index++)
            {
                var name = new StringBuilder(128);
                GetMenuString(menu, (uint)index, name, name.Capacity, 0x400);
                var label = name.ToString().Replace("&", "");
                if ((label.Contains("帮助") || label.Equals("Help", StringComparison.OrdinalIgnoreCase))
                    && GetMenuItemRect(host.Handle, menu, (uint)index, out var rect))
                    return new(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), "native-help-menu");
            }
        }
        // Current Codex custom title bar has no Help element in its accessibility tree. These are
        // client-relative DIP measurements with the confirmed right 80 DIP / down 5 DIP adjustment.
        var origin = new NativePoint();
        ClientToScreen(host.Handle, ref origin);
        var scale = host.Dpi / 96d;
        return new(new(origin.X + (int)Math.Round(352 * scale), origin.Y + (int)Math.Round(5 * scale),
            (int)Math.Round(38 * scale), (int)Math.Round(36 * scale)), "measured-custom-titlebar");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr menu, uint item, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
