using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Aestoys.Services;

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts a new elevated copy of aestoys. Returns false if the UAC prompt was declined.</summary>
    public static bool RelaunchElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }
    }
}

/// <summary>
/// Normal mode uses the HKCU Run key. Administrator mode uses a scheduled task with
/// "highest privileges", because Windows will not auto-elevate programs from the Run key.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "aestoys";
    private const string TaskName = "aestoys";

    public static bool RunKeyExists
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    /// <summary>Applies the startup registration. Returns false if it could not be completed (e.g. UAC declined).</summary>
    public static bool Apply(bool enabled, bool admin, bool hadTask)
    {
        SetRunKey(enabled && !admin);
        if (enabled && admin) return CreateTask();
        if (hadTask) return DeleteTask();
        return true;
    }

    private static void SetRunKey(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(Name, false);
    }

    private static bool CreateTask()
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts aestoys with administrator rights at sign-in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(Environment.ProcessPath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        Directory.CreateDirectory(AppSettings.Dir);
        string path = Path.Combine(AppSettings.Dir, "startup-task.xml");
        File.WriteAllText(path, xml, Encoding.Unicode);
        return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F");
    }

    private static bool DeleteTask() => Schtasks($"/Delete /TN \"{TaskName}\" /F");

    private static bool Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
        if (Elevation.IsElevated) psi.UseShellExecute = false;
        else { psi.UseShellExecute = true; psi.Verb = "runas"; }
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }
}
