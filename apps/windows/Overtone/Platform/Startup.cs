using Microsoft.Win32;

namespace Overtone.Platform;

/// <summary>
/// "Start with Windows": a per-user Run entry pointing at this exe, the same place
/// Task Manager's Startup apps page lists and toggles. The registry is the only
/// state; nothing is mirrored in settings.json, so a change made in Task Manager or
/// Windows Settings shows up in the tray menu as is.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's on/off switch: a 12-byte value whose first byte is odd when disabled.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Overtone";

    private static string Command => $"\"{Environment.ProcessPath}\" --startup";

    /// <summary>True when Windows will start this copy of Overtone at sign-in.</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(Name) is not string command) return false;
                if (!command.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase)) return false;
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
            }
            catch (Exception ex)
            {
                Settings.LogError(ex);
                return false;
            }
        }
        set
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) run.SetValue(Name, Command);
                else run.DeleteValue(Name, throwOnMissingValue: false);
                // Clear a "disabled" left by Task Manager, or turning it on here would do nothing.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(Name, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Settings.LogError(ex);
            }
        }
    }
}
