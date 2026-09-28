using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using DnnManager.Application.Abstractions;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Signs SQL Server Management Studio 21+ in to a database by completing its Connect dialog through UI Automation.
/// </summary>
/// <remarks>
/// SSMS takes server, database and login on its command line but never a password - and with any of those
/// switches it connects straight away, fails without the password and shows an error first. Started with no
/// connection switches it just opens the Connect dialog, so that is filled in here instead: server, SQL Server
/// Authentication, login, password (remembered), database, trust-server-certificate and name, then Connect.
/// Only the SSMS process DNN Manager just started is touched. Fields are found by name, the password box by its
/// role and the Connect button / authentication choice by language-neutral ids.
/// </remarks>
internal static class SsmsConnectDialog
{
    /// <summary>
    /// Waits for <paramref name="ssms"/>'s Connect dialog and signs in to <paramref name="database"/>. False when the
    /// dialog didn't show up in time (SSMS set not to show it at startup) or didn't look as expected.
    /// </summary>
    /// <param name="rememberPassword">Tick SSMS's "Remember Password" (untick it when false).</param>
    public static Task<bool> SignInAsync(Process ssms, SiteSqlConnection database, bool trustServerCertificate,
        string displayName, bool rememberPassword, TimeSpan timeout) =>
        Task.Run(() =>
        {
            try
            {
                var dialog = WaitForDialog(ssms, timeout);
                if (dialog is null) return false;

                // Authentication first: it decides which of the other fields are enabled.
                if (!SelectAuthentication(dialog, "SqlPassword")) return false;
                if (!SetText(dialog, "Server Name", database.Server)) return false;
                if (!SetText(dialog, "User Name", database.User)) return false;

                var passwordBox = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.IsPasswordProperty, true),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                if (passwordBox is null || !passwordBox.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return false;
                ((ValuePattern)p).SetValue(database.Password);

                SetChecked(dialog, "Remember Password", rememberPassword);
                SetChecked(dialog, "Trust Server Certificate", trustServerCertificate);
                SetDatabase(dialog, database.Database.Length > 0 ? database.Database : "<default>");
                SetText(dialog, "Name", displayName);

                var connect = dialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "OkButton"));
                if (connect is null || !connect.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) return false;
                ((InvokePattern)i).Invoke();
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
                                           or COMException)
            {
                return false; // the dialog changed or closed meanwhile, or UI Automation failed
            }
        });

    /// <summary>
    /// Brings an SSMS that is already running to the front and opens its Connect dialog (Object Explorer's
    /// "Connect Object Explorer..."), so a new connection goes into that window instead of a new SSMS. An open
    /// Connect dialog is reused. False when the dialog doesn't appear - e.g. SSMS is busy with another dialog.
    /// </summary>
    public static Task<bool> OpenInRunningAsync(Process ssms) =>
        Task.Run(() =>
        {
            try
            {
                var main = AutomationElement.FromHandle(ssms.MainWindowHandle);
                if (main.TryGetCurrentPattern(WindowPattern.Pattern, out var w) &&
                    ((WindowPattern)w).Current.WindowVisualState == WindowVisualState.Minimized)
                    ((WindowPattern)w).SetWindowVisualState(WindowVisualState.Normal);
                SetForegroundWindow(ssms.MainWindowHandle);

                if (WaitForDialog(ssms, TimeSpan.Zero) is not null) return true;

                var connect = Find(main, "Connect Object Explorer...", ControlType.Button);
                if (connect is null || !connect.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) return false;
                ((InvokePattern)i).Invoke();
                return WaitForDialog(ssms, TimeSpan.FromSeconds(5)) is not null;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
                                           or COMException)
            {
                return false;
            }
        });

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>The Connect window of <paramref name="ssms"/> - on its own or inside the main window.</summary>
    private static AutomationElement? WaitForDialog(Process ssms, TimeSpan timeout)
    {
        var ownWindows = new PropertyCondition(AutomationElement.ProcessIdProperty, ssms.Id);
        var deadline = DateTime.UtcNow + timeout;
        // do-while: a zero timeout still looks once.
        do
        {
            try
            {
                foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, ownWindows))
                {
                    if (window.Current.Name == "Connect") return window;
                    var inner = Find(window, "Connect", ControlType.Window);
                    if (inner is not null) return inner;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                // A window came or went mid-scan, or UI Automation hiccupped while SSMS starts - look again.
            }
            if (DateTime.UtcNow < deadline) Thread.Sleep(500);
        } while (DateTime.UtcNow < deadline && !ssms.HasExited);
        return null;
    }

    /// <summary>Picks the authentication whose item is <c>[key, label]</c> - the key doesn't depend on the language.</summary>
    private static bool SelectAuthentication(AutomationElement dialog, string key)
    {
        var combo = Find(dialog, "Authentication", ControlType.ComboBox);
        if (combo is null) return false;
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var e)) ((ExpandCollapsePattern)e).Expand();
        try
        {
            foreach (AutomationElement item in combo.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                if (!item.Current.Name.StartsWith("[" + key + ",", StringComparison.Ordinal)) continue;
                if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s)) return false;
                ((SelectionItemPattern)s).Select();
                return true;
            }
            return false;
        }
        finally
        {
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var c) &&
                ((ExpandCollapsePattern)c).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                ((ExpandCollapsePattern)c).Collapse();
        }
    }

    private static bool SetText(AutomationElement dialog, string name, string value)
    {
        var box = Find(dialog, name, ControlType.Edit);
        if (box is null || !box.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) return false;
        ((ValuePattern)v).SetValue(value);
        return true;
    }

    /// <summary>The database box is an editable combo box; its text part takes the name.</summary>
    private static void SetDatabase(AutomationElement dialog, string database)
    {
        var combo = Find(dialog, "Database Name", ControlType.ComboBox);
        var text = combo?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_EditableTextBox"));
        if (text is not null && text.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
            ((ValuePattern)v).SetValue(database);
    }

    private static void SetChecked(AutomationElement dialog, string name, bool on)
    {
        var box = Find(dialog, name, ControlType.CheckBox);
        if (box is null || !box.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) return;
        var toggle = (TogglePattern)t;
        if ((toggle.Current.ToggleState == ToggleState.On) != on) toggle.Toggle();
    }

    private static AutomationElement? Find(AutomationElement parent, string name, ControlType type) =>
        parent.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
}
