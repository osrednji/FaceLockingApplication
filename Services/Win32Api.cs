using System.Runtime.InteropServices;

namespace FaceLockApp.Services;

public static class Win32Api
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    public const uint EWX_LOGOFF = 0x00000000;
    public const uint EWX_FORCE = 0x00000004;

    public static bool TriggerAction(bool logoutMode = false)
    {
        if (logoutMode)
        {
            return ExitWindowsEx(EWX_LOGOFF, 0);
        }
        return LockWorkStation();
    }
}
