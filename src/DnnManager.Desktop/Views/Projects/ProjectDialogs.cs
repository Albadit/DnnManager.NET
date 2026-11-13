using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>A one-line text prompt - checked as it is typed, with what is wrong under the box (the Windows app's InputDialog).</summary>
internal static class InputDialog
{
    /// <summary>The entered text (trimmed), or null when cancelled or left blank; OK waits until <paramref name="validate"/> is happy.</summary>
    public static async Task<string?> ShowAsync(string question, string? initial = null, string okText = "OK", Func<string, string?>? validate = null)
    {
        var window = DialogKit.Window("DNN Manager", 460);
        var text = new TextBox { Text = initial ?? "" };
        Avalonia.Automation.AutomationProperties.SetName(text, question);
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok(okText, $"{okText} (Enter)");
        string Value() => (text.Text ?? "").Trim();
        void Check()
        {
            var wrong = Value().Length == 0 ? null : validate?.Invoke(Value());
            DialogKit.Say(problem, wrong);
            ok.IsEnabled = Value().Length > 0 && wrong is null;
        }
        text.TextChanged += (_, _) => Check();
        ok.Click += (_, _) => DialogKit.Accept(window);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }, text, problem, DialogKit.Buttons(window, ok) }
        };
        Check();
        window.Opened += (_, _) => { text.Focus(); text.SelectAll(); };
        return await DialogKit.ShowAsync(window) && Value().Length > 0 ? Value() : null;
    }
}

/// <summary>What Rename project was asked to do: the new name, and whether the host name and the database follow it.</summary>
public sealed record RenameProjectChoice(string NewName, bool RenameHost, bool RenameDatabase);

/// <summary>A project's new name - checked as it is typed - and whether its host name and database follow it.</summary>
internal static class RenameProjectDialog
{
    public static async Task<RenameProjectChoice?> ShowAsync(string currentName, Func<string, string?> problemOf, string? currentHost,
        Func<string, string> hostFor, string? database)
    {
        var window = DialogKit.Window("Rename project", 460);
        var name = new TextBox { Text = currentName };
        Avalonia.Automation.AutomationProperties.SetName(name, "New name");
        var problem = DialogKit.Problem();
        var hostText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var hostBox = new CheckBox { IsChecked = true, Margin = new Thickness(0, 12, 0, 0), Content = hostText, IsVisible = currentHost is not null };
        var databaseText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var databaseBox = new CheckBox { IsChecked = true, Content = databaseText };
        var databasePanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0), IsVisible = database is not null,
            Children = { databaseBox, new TextBlock { Classes = { "hint" }, Margin = new Thickness(22, 2, 0, 0), Text = "The site is stopped while the database is renamed." } }
        };
        var ok = DialogKit.Ok("Rename", "Rename (Enter)");
        string NewName() => (name.Text ?? "").Trim();
        void Check()
        {
            var typed = NewName();
            // The same name again is no rename - OK just waits, without calling it a mistake. Another casing of it is a rename.
            var unchanged = typed == currentName;
            var wrong = typed.Length == 0 || unchanged ? null
                : ProjectName.Validate(typed) is { Success: false } invalid ? invalid.Error : problemOf(typed);
            DialogKit.Say(problem, wrong);
            ok.IsEnabled = typed.Length > 0 && !unchanged && wrong is null;
            var shown = typed.Length > 0 ? typed : currentName;
            if (currentHost is not null) hostText.Text = $"Also change the host name: {currentHost} → {hostFor(shown)}";
            if (database is not null) databaseText.Text = $"Also rename the database: [{database}] → [{shown}]";
        }
        name.TextChanged += (_, _) => Check();
        ok.Click += (_, _) => DialogKit.Accept(window);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro($"Renaming '{currentName}' changes its folder (when it is in the projects folder), its IIS site and its app pool. " +
                                "Its backups stay under the old name."),
                DialogKit.Label("New name"), name, problem, hostBox, databasePanel, DialogKit.Buttons(window, ok)
            }
        };
        Check();
        window.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        if (!await DialogKit.ShowAsync(window)) return null;
        return new RenameProjectChoice(NewName(), currentHost is not null && hostBox.IsChecked == true, database is not null && databaseBox.IsChecked == true);
    }
}

/// <summary>Which host account gets which new password - typed twice, or generated and shown (the Windows app's HostPasswordDialog).</summary>
internal static class HostPasswordDialog
{
    public static async Task<(string User, string Password)?> ShowAsync(string site, IReadOnlyList<string> hosts)
    {
        var window = DialogKit.Window("Change host password", 460);
        var user = new ComboBox { ItemsSource = hosts, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = hosts.Count > 0 ? 0 : -1 };
        Avalonia.Automation.AutomationProperties.SetName(user, "Host account");
        var only = hosts.Count == 1;
        var userPanel = new StackPanel { IsVisible = !only, Children = { DialogKit.Label("Host account"), user } };
        var password = new PasswordInput("New password");
        var confirm = new PasswordInput("New password again");
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Change password", "Change password (Enter)");
        ok.IsEnabled = false;
        var generate = new Button { Content = "Generate", Classes = { "link" }, FontSize = 12 };
        ToolTip.SetTip(generate, "Generate password");
        generate.Click += (_, _) =>
        {
            var generated = DnnAccountRules.GeneratePassword();
            password.Password = generated;
            confirm.Password = generated;
            password.Reveal();
        };
        void Check()
        {
            var wrong = password.Password.Length == 0 ? null
                : DnnAccountRules.PasswordProblem(password.Password)
                  ?? (confirm.Password.Length > 0 && confirm.Password != password.Password ? "The two passwords aren't the same." : null);
            DialogKit.Say(problem, wrong);
            ok.IsEnabled = wrong is null && password.Password.Length > 0 && confirm.Password == password.Password;
        }
        password.Changed += Check;
        confirm.Changed += Check;
        ok.Click += (_, _) =>
        {
            if (user.SelectedItem is null)
            {
                DialogKit.Say(problem, hosts.Count > 0 ? "Choose the host account." : "The site's database has no host account to change.");
                return;
            }
            DialogKit.Accept(window);
        };
        var newLabel = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        DockPanel.SetDock(generate, Dock.Right);
        newLabel.Children.Add(generate);
        newLabel.Children.Add(new TextBlock { Text = "New password", Classes = { "fieldLabel" }, Margin = new Thickness(0) });
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro((only ? $"A new password for '{hosts[0]}', the host (superuser) account of '{site}'."
                                     : $"A new password for a host (superuser) account of '{site}'.") + " DNN Manager doesn't keep it - note it down."),
                userPanel, newLabel, password, DialogKit.Label("New password again"), confirm, problem, DialogKit.Buttons(window, ok)
            }
        };
        window.Opened += (_, _) => password.FocusBox();
        return await DialogKit.ShowAsync(window) && user.SelectedItem is string chosen ? (chosen, password.Password) : null;
    }
}

/// <summary>
/// The host names and ports a site answers on over http (the Windows app's BindingsDialog). Every row remembers the binding
/// it started as, so DNN's portal alias of a changed host name can be renamed rather than a new one added.
/// </summary>
internal static class BindingsDialog
{
    private sealed record Row(string? OldHost, int? OldPort, TextBox Host, TextBox Port, Control View)
    {
        public string HostName => (Host.Text ?? "").Trim().ToLowerInvariant();
        public int? PortNumber => int.TryParse((Port.Text ?? "").Trim(), out var port) && port is > 0 and <= 65535 ? port : null;
    }

    public static async Task<IReadOnlyList<HttpBindingEdit>?> ShowAsync(string site, IReadOnlyList<(string Host, int Port)> httpBindings, IReadOnlyList<string> otherBindings)
    {
        var window = DialogKit.Window("Edit host names", 460);
        var rows = new List<Row>();
        var rowsPanel = new StackPanel();
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Save", "Save (Enter)");

        List<HttpBindingEdit> Edits() => rows.Select(r => new HttpBindingEdit(r.OldHost, r.OldPort, r.HostName, r.PortNumber ?? 0)).ToList();

        bool Changed()
        {
            var edits = Edits();
            return edits.Count != httpBindings.Count
                   || edits.Zip(httpBindings).Any(p => p.First.OldHost is null
                                                       || !string.Equals(p.First.Host, p.Second.Host, StringComparison.OrdinalIgnoreCase)
                                                       || p.First.Port != p.Second.Port);
        }

        string? FindProblem()
        {
            if (rows.Count == 0) return "Keep at least one host name - the site needs an http binding.";
            var seen = new HashSet<(string, int)>();
            foreach (var row in rows)
            {
                var host = row.HostName;
                var shown = host.Length == 0 ? "any host name" : $"'{host}'";
                if (host.Length > 0 && !IsHostName(host))
                    return $"'{host}' isn't a host name: letters, digits, '-' and '.', each part 1 to 63 characters, not starting or ending with '-'.";
                if (row.PortNumber is not { } port) return $"The port of {shown} must be a number between 1 and 65535.";
                if (!seen.Add((host, port))) return $"{(host.Length == 0 ? "Any host name" : shown)} on port {port} is there twice.";
            }
            return null;
        }

        void Check()
        {
            var wrong = FindProblem();
            DialogKit.Say(problem, wrong);
            ok.IsEnabled = wrong is null && Changed();
        }

        Row AddRow(string? oldHost, int? oldPort, string host, int port)
        {
            var hostBox = new TextBox { Text = host, Width = 260 };
            Avalonia.Automation.AutomationProperties.SetName(hostBox, "Host name");
            var portBox = new TextBox { Text = port.ToString(), Width = 70, Margin = new Thickness(8, 0, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetName(portBox, "Port");
            var remove = new Button { Classes = { "icon" }, Margin = new Thickness(4, 0, 0, 0), Content = Glyph("GlyphDelete", "✕") };
            ToolTip.SetTip(remove, "Remove this host name");
            var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { hostBox, portBox, remove } };
            var row = new Row(oldHost, oldPort, hostBox, portBox, view);
            hostBox.TextChanged += (_, _) => Check();
            portBox.TextChanged += (_, _) => Check();
            remove.Click += (_, _) =>
            {
                rows.Remove(row);
                rowsPanel.Children.Remove(view);
                Check();
            };
            rows.Add(row);
            rowsPanel.Children.Add(view);
            return row;
        }

        foreach (var (host, port) in httpBindings) AddRow(host, port, host, port);
        var add = new Button { Content = "Add host name", Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            // A new host name most likely goes on the port the site already uses.
            var row = AddRow(null, null, "", rows.Select(r => r.PortNumber).FirstOrDefault(p => p is not null) ?? 80);
            Check();
            row.Host.Focus();
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            Children = { new TextBlock { Text = "Host name", Classes = { "fieldLabel" }, Width = 260 }, new TextBlock { Text = "Port", Classes = { "fieldLabel" }, Margin = new Thickness(8, 8, 0, 4) } }
        };
        var others = new StackPanel { IsVisible = otherBindings.Count > 0 };
        others.Children.Add(new TextBlock { Text = "Kept as they are", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) });
        foreach (var other in otherBindings) others.Children.Add(new TextBlock { Text = other, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        ok.Click += (_, _) => DialogKit.Accept(window);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro($"The host names and ports '{site}' answers on over http. DNN's portal aliases follow: a changed host name's alias " +
                                "is renamed, a new one's is added."),
                header, rowsPanel, add, DialogKit.Hint("An empty host name answers on any host name."), others, problem, DialogKit.Buttons(window, ok)
            }
        };
        Check();
        window.Opened += (_, _) => rows.FirstOrDefault()?.Host.Focus();
        return await DialogKit.ShowAsync(window) ? Edits() : null;
    }

    /// <summary>A DNS name: dot-separated labels of letters, digits and hyphens, at most 253 characters in all.</summary>
    private static bool IsHostName(string host) =>
        host.Length <= 253
        && host.Split('.').All(label => label.Length is > 0 and <= 63
                                        && label[0] != '-' && label[^1] != '-'
                                        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    internal static Control Glyph(string key, string fallback) => OperatingSystem.IsWindows()
        ? new TextBlock { Text = ThemeBrush.Glyph(key), Classes = { "glyph" }, FontSize = 13 }
        : new TextBlock { Text = fallback };
}

/// <summary>
/// A site's app pool: its .NET runtime, pipeline, 32-bit mode, identity, idle time-out and start mode (the Windows app's
/// AppPoolDialog). A specific user account is kept as it is - its password isn't known here.
/// </summary>
internal static class AppPoolDialog
{
    // IIS takes an idle time-out of up to 30 days.
    private const int MaxIdleMinutes = 43200;

    public static async Task<IisPoolSettings?> ShowAsync(string site, string poolName, IisPoolSettings current)
    {
        var window = DialogKit.Window("Edit app pool", 460);
        var ready = false;
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Save", "Save (Enter)");
        var runtime = Box(".NET CLR version", current.Runtime, ("v4.0", "v4.0"), ("No Managed Code", ""));
        var pipeline = Box("Pipeline mode", current.Pipeline, ("Integrated", "Integrated"), ("Classic", "Classic"));
        var enable32 = new CheckBox { Content = "Enable 32-bit applications", Margin = new Thickness(0, 12, 0, 0), IsChecked = current.Enable32Bit };
        string[] identities = ["ApplicationPoolIdentity", "NetworkService", "LocalService", "LocalSystem"];
        ComboBox identity;
        var identityHint = DialogKit.Hint();
        if (identities.Any(i => Same(i, current.Identity)))
        {
            identity = Box("Identity", current.Identity, identities.Select(i => (i, i)).ToArray());
            identityHint.Text = "Another identity needs access to the site's folder (granted when saved) and, with Windows authentication, to its database.";
        }
        else
        {
            var text = Same(current.Identity, "SpecificUser") ? "Specific user (kept)" : $"{current.Identity} (kept)";
            identity = Box("Identity", current.Identity, (text, current.Identity));
            identity.IsEnabled = false;
            identityHint.Text = "A specific user account is changed in IIS Manager.";
        }
        var startMode = Box("Start mode", current.StartMode, ("OnDemand", "OnDemand"), ("AlwaysRunning", "AlwaysRunning"));
        var idleText = ((long)Math.Round(current.IdleTimeout.TotalMinutes)).ToString();
        var idle = new TextBox { Text = idleText };
        Avalonia.Automation.AutomationProperties.SetName(idle, "Idle time-out (minutes)");

        TimeSpan? IdleTimeout()
        {
            // Left as it was, it is the current value exactly - which can have seconds the box rounds away.
            var typed = (idle.Text ?? "").Trim();
            if (typed == idleText) return current.IdleTimeout;
            return int.TryParse(typed, out var minutes) && minutes is >= 0 and <= MaxIdleMinutes ? TimeSpan.FromMinutes(minutes) : null;
        }

        IisPoolSettings Chosen() => new(Value(runtime), Value(pipeline), enable32.IsChecked == true, Value(identity),
            IdleTimeout() ?? current.IdleTimeout, Value(startMode));

        void Check()
        {
            if (!ready) return;
            var wrong = IdleTimeout() is null ? $"The idle time-out is a whole number of minutes from 0 (never) to {MaxIdleMinutes} (30 days)." : null;
            DialogKit.Say(problem, wrong);
            var chosen = Chosen();
            var changed = !Same(chosen.Runtime, current.Runtime) || !Same(chosen.Pipeline, current.Pipeline)
                          || chosen.Enable32Bit != current.Enable32Bit || !Same(chosen.Identity, current.Identity)
                          || chosen.IdleTimeout != current.IdleTimeout || !Same(chosen.StartMode, current.StartMode);
            ok.IsEnabled = wrong is null && changed;
        }

        foreach (var box in new[] { runtime, pipeline, identity, startMode }) box.SelectionChanged += (_, _) => Check();
        enable32.IsCheckedChanged += (_, _) => Check();
        idle.TextChanged += (_, _) => Check();
        ok.Click += (_, _) => DialogKit.Accept(window);

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro($"The settings of '{poolName}', the app pool of '{site}'. Saving recycles the pool."),
                Pair(Field(".NET CLR version", runtime), Field("Pipeline mode", pipeline)),
                enable32,
                new TextBlock { Text = "Identity", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) }, identity, identityHint,
                Pair(Field("Idle time-out (minutes)", idle), Field("Start mode", startMode)),
                DialogKit.Hint("An idle time-out of 0 never shuts the pool down for being idle."),
                problem, DialogKit.Buttons(window, ok)
            }
        };
        ready = true;
        Check();
        window.Opened += (_, _) => runtime.Focus();
        return await DialogKit.ShowAsync(window) ? Chosen() : null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The choices (text shown, value meant), <paramref name="current"/> selected - added when it isn't among them.</summary>
    private static ComboBox Box(string name, string current, params (string Text, string Value)[] choices)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        foreach (var (text, value) in choices) box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        var selected = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Same((string)i.Tag!, current));
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = current, Tag = current };
            box.Items.Add(selected);
        }
        box.SelectedItem = selected;
        return box;
    }

    private static string Value(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    internal static StackPanel Field(string label, Control control) => new() { Children = { DialogKit.Label(label), control } };

    internal static Grid Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}

/// <summary>
/// What Export for deployment makes the package for (the Windows app's DeploymentExportDialog): the live domains, HTTPS,
/// the live database's connection string, whether the database goes along, and the folder - outside the site's folder.
/// </summary>
internal static class DeploymentExportDialog
{
    public static async Task<ExportForDeploymentRequest?> ShowAsync(string site, string directory, string defaultFolder, bool hasDatabase)
    {
        var window = DialogKit.Window("Export for deployment", 520);
        var domains = new TextBox();
        Avalonia.Automation.AutomationProperties.SetName(domains, "Live domain(s)");
        var https = new CheckBox { Content = "The live site uses HTTPS", IsChecked = true, Margin = new Thickness(0, 14, 0, 0) };
        var connection = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 44, MaxHeight = 88, VerticalContentAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetName(connection, "Live database connection string (optional)");
        var includeDatabase = new CheckBox { Content = "Include the database (.bacpac)", Margin = new Thickness(0, 14, 0, 0), IsChecked = hasDatabase, IsEnabled = hasDatabase };
        var noDatabase = DialogKit.Hint("The site's web.config names no database that can be exported.");
        noDatabase.IsVisible = !hasDatabase;
        var folder = new TextBox { Text = defaultFolder };
        Avalonia.Automation.AutomationProperties.SetName(folder, "Folder");
        var browse = new Button { Content = "Browse…", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(browse, "Browse…");
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Export", "Export (Enter)");

        IReadOnlyList<string> Domains(out string? wrong)
        {
            wrong = null;
            var list = new List<string>();
            foreach (var entry in (domains.Text ?? "").Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsHostName(entry))
                {
                    wrong ??= $"'{entry}' isn't a host name - enter it like www.example.com or example.com:8080, without http:// or a path.";
                    continue;
                }
                var domain = entry.ToLowerInvariant();
                if (!list.Contains(domain)) list.Add(domain);
            }
            return list;
        }

        string? FolderProblem()
        {
            var path = (folder.Text ?? "").Trim();
            if (path.Length == 0) return "Choose the folder the package goes to.";
            if (!Path.IsPathFullyQualified(path)) return @"Enter the folder's full path, e.g. C:\Deploy.";
            try
            {
                Path.GetFullPath(path);
                if (directory.Length == 0) return null;
                return SafePath.IsSameOrInside(path, directory) ? "Choose a folder outside the site's folder." : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return @"Enter the folder's full path, e.g. C:\Deploy.";
            }
        }

        void Check()
        {
            Domains(out var domainProblem);
            var wrong = domainProblem ?? FolderProblem();
            DialogKit.Say(problem, wrong);
            ok.IsEnabled = wrong is null;
        }

        domains.TextChanged += (_, _) => Check();
        folder.TextChanged += (_, _) => Check();
        browse.Click += async (_, _) =>
        {
            var start = Directory.Exists((folder.Text ?? "").Trim()) ? await window.StorageProvider.TryGetFolderFromPathAsync((folder.Text ?? "").Trim()) : null;
            var picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export to folder", SuggestedStartLocation = start });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } local) folder.Text = local;
        };
        ok.Click += (_, _) =>
        {
            if (ok.IsEnabled) DialogKit.Accept(window);
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro($"A package of '{site}' for the live server: the site's files without logs, caches, source control and " +
                                "editor folders, with web.config ready for the server - and the database as a .bacpac, with the live " +
                                "domain as its portal alias. The local site and its database are not changed."),
                new TextBlock { Text = "Live domain(s)", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) }, domains,
                DialogKit.Hint("Comma- or space-separated host names, e.g. www.example.com, example.com - the first is the primary portal alias. Left empty, the portal aliases stay as they are."),
                https,
                DialogKit.Hint("Switches DNN's SSL setting on, and turns the HTTPS redirect rules DNN Manager switched off back on."),
                new TextBlock { Text = "Live database connection string (optional)", Classes = { "fieldLabel" }, Margin = new Thickness(0, 14, 0, 4) }, connection,
                DialogKit.Hint("Left empty, web.config gets a placeholder to fill in on the server."),
                includeDatabase, noDatabase,
                new TextBlock { Text = "Folder", Classes = { "fieldLabel" }, Margin = new Thickness(0, 14, 0, 4) }, folderRow,
                DialogKit.Hint($"Gets {site}.zip, {site}.bacpac and DEPLOY.txt."),
                problem, DialogKit.Buttons(window, ok)
            }
        };
        Check();
        window.Opened += (_, _) => domains.Focus();
        if (!await DialogKit.ShowAsync(window)) return null;
        var connectionString = (connection.Text ?? "").Trim();
        return new ExportForDeploymentRequest
        {
            ProjectName = site,
            ProjectDirectory = directory,
            OutputFolder = Path.GetFullPath((folder.Text ?? "").Trim()),
            Domains = Domains(out _),
            Https = https.IsChecked == true,
            ConnectionString = connectionString.Length > 0 ? connectionString : null,
            IncludeDatabase = hasDatabase && includeDatabase.IsChecked == true
        };
    }

    /// <summary>A DNS host name, optionally followed by :port.</summary>
    private static bool IsHostName(string text)
    {
        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        if (colon >= 0 && !(int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and <= 65535)) return false;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}

/// <summary>
/// Where a site's web.config points SiteSqlServer (the Windows app's DatabaseConnectionDialog): the connection type, server,
/// sign-in and database. Nothing in a database is moved or copied - only the connection changes, so it is tested here
/// (not required) before it is saved.
/// </summary>
internal static class DatabaseConnectionDialog
{
    public static async Task<DatabaseConnection?> ShowAsync(IServiceProvider services, string site, DatabaseConnection? current, string siteLogin)
    {
        var options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        var secrets = services.GetRequiredService<ISecretStore>();
        var databases = services.GetRequiredService<IDatabaseProvisioner>();
        var window = DialogKit.Window("Change database connection", 520);

        var type = current?.Kind ?? DatabaseKind.Container;
        var databaseName = current is { Kind: not DatabaseKind.LocalDbFile } ? current.Database : site;
        var localDbFile = current is { Kind: DatabaseKind.LocalDbFile, Database.Length: > 0 } ? current.Database : DatabaseConnection.LocalDbFileName;
        var localDbServer = current is { Kind: DatabaseKind.LocalDbFile, Server.Length: > 0 } ? current.Server : DatabaseConnection.LocalDbServer;
        var sqlServerFilled = false;
        var filling = true;
        var testing = false;
        DatabaseConnection? tested = null;
        DatabaseCheckReport? report = null;
        var containerServer = options.ServerFor(options.Docker.DefaultPort);

        var dbContainer = new RadioButton { GroupName = "DbType", Content = "Local SQL container (Docker)", Margin = new Thickness(0, 0, 20, 4) };
        var dbSqlServer = new RadioButton { GroupName = "DbType", Content = "SQL Server", Margin = new Thickness(0, 0, 20, 4) };
        var dbLocalDb = new RadioButton { GroupName = "DbType", Content = "SQL Server Express LocalDB (file)", Margin = new Thickness(0, 0, 0, 4) };
        var server = new TextBox();
        Avalonia.Automation.AutomationProperties.SetName(server, "Server");
        var name = new TextBox();
        Avalonia.Automation.AutomationProperties.SetName(name, "Database name");
        var nameLabel = DialogKit.Label("Database name");
        var windowsAuth = new RadioButton { GroupName = "DbAuth", Content = "Windows authentication", Margin = new Thickness(0, 0, 20, 0) };
        var sqlAuth = new RadioButton { GroupName = "DbAuth", Content = "SQL Server authentication" };
        var user = new TextBox();
        Avalonia.Automation.AutomationProperties.SetName(user, "Username");
        var password = new PasswordInput("Password");
        var serverPanel = AppPoolDialog.Field("Server", server);
        var namePanel = new StackPanel { Children = { nameLabel, name } };
        var authPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { windowsAuth, sqlAuth } };
        var userPanel = AppPoolDialog.Field("Username", user);
        var passwordPanel = AppPoolDialog.Field("Password", password);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Grid.SetColumn(namePanel, 2);
        Grid.SetRow(authPanel, 1);
        Grid.SetColumnSpan(authPanel, 3);
        Grid.SetRow(userPanel, 2);
        Grid.SetRow(passwordPanel, 2);
        Grid.SetColumn(passwordPanel, 2);
        grid.Children.AddRange([serverPanel, namePanel, authPanel, userPanel, passwordPanel]);
        var hint = DialogKit.Hint();
        var test = new Button { Content = "Test connection", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 80, Margin = new Thickness(0, 12, 0, 0) };
        ToolTip.SetTip(test, "Test connection");
        var checks = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Save", "Save (Enter)");

        void FillSqlServer()
        {
            sqlServerFilled = true;
            var was = filling;
            filling = true;
            if (current is { Kind: DatabaseKind.SqlServer } c)
            {
                server.Text = c.Server;
                windowsAuth.IsChecked = c.Authentication == SqlAuthentication.Windows;
                sqlAuth.IsChecked = c.Authentication == SqlAuthentication.Sql;
                user.Text = c.User;
                password.Password = c.Password;
            }
            else
            {
                var saved = options.DatabaseServer;
                var isSqlServer = !saved.IsContainer && !saved.IsLocalDbFile;
                server.Text = isSqlServer && saved.Server.Length > 0 ? saved.Server : @".\SQLEXPRESS";
                windowsAuth.IsChecked = !saved.UsesSqlAuthentication;
                sqlAuth.IsChecked = saved.UsesSqlAuthentication;
                user.Text = isSqlServer ? saved.UserName : "";
                password.Password = saved.UsesSqlAuthentication ? secrets.Read(SecretNames.DatabaseServerPassword) ?? "" : "";
            }
            filling = was;
        }

        bool SqlLogin() => type == DatabaseKind.SqlServer && sqlAuth.IsChecked == true;
        string NameText() => (name.Text ?? "").Trim();

        string? FieldProblem()
        {
            var n = NameText();
            if (type == DatabaseKind.LocalDbFile)
            {
                if (n.Length == 0) return "Enter the database file's name.";
                return n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !n.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase)
                    ? $"Enter the name of a .mdf file in the site's App_Data folder, e.g. {DatabaseConnection.LocalDbFileName}."
                    : null;
            }
            if (type == DatabaseKind.SqlServer && (server.Text ?? "").Trim().Length == 0) return "Enter the server.";
            if (n.Length == 0) return "Enter the database name.";
            if (n.Length > 128 || n.Contains(']')) return "The database name can have at most 128 characters, and no ']'.";
            if (SqlLogin() && (user.Text ?? "").Trim().Length == 0) return "Enter the username.";
            if (SqlLogin() && password.Password.Length == 0) return "Enter the password.";
            return null;
        }

        DatabaseConnection Chosen()
        {
            var database = NameText();
            return type switch
            {
                DatabaseKind.Container => new DatabaseConnection(DatabaseKind.Container, containerServer, database, SqlAuthentication.Sql,
                    options.Docker.SqlUser, options.Docker.SaPassword),
                DatabaseKind.LocalDbFile => new DatabaseConnection(DatabaseKind.LocalDbFile, localDbServer, database, SqlAuthentication.Windows),
                _ => SqlLogin()
                    ? new DatabaseConnection(DatabaseKind.SqlServer, (server.Text ?? "").Trim(), database, SqlAuthentication.Sql, (user.Text ?? "").Trim(), password.Password)
                    : new DatabaseConnection(DatabaseKind.SqlServer, (server.Text ?? "").Trim(), database, SqlAuthentication.Windows)
            };
        }

        static bool Same(DatabaseConnection a, DatabaseConnection b) =>
            a.Kind == b.Kind
            && string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase)
            && a.UsesWindowsAuthentication == b.UsesWindowsAuthentication
            && (a.UsesWindowsAuthentication || (a.User == b.User && a.Password == b.Password));

        void ShowChecks(DatabaseCheckReport? shown)
        {
            checks.Children.Clear();
            foreach (var check in shown?.Checks ?? [])
            {
                var (mark, brush) = check.Outcome switch
                {
                    CheckOutcome.Passed => ("✓", "SuccessText"), CheckOutcome.Warning => ("!", "LogWarn"), _ => ("✕", "ErrorText")
                };
                var markText = new TextBlock { Text = mark, Width = 18, FontWeight = FontWeight.SemiBold };
                markText.Bind(TextBlock.ForegroundProperty, markText.GetResourceObservable(brush));
                var line = new DockPanel { Margin = new Thickness(0, 1) };
                line.Children.Add(markText);
                line.Children.Add(new TextBlock { Text = $"{check.Name}: {check.Detail}", TextWrapping = TextWrapping.Wrap });
                checks.Children.Add(line);
            }
        }

        void Update()
        {
            serverPanel.IsVisible = authPanel.IsVisible = type == DatabaseKind.SqlServer;
            userPanel.IsVisible = passwordPanel.IsVisible = SqlLogin();
            nameLabel.Text = type == DatabaseKind.LocalDbFile ? "Database file in App_Data" : "Database name";
            Grid.SetColumn(namePanel, type == DatabaseKind.SqlServer ? 2 : 0);
            Grid.SetColumnSpan(namePanel, type == DatabaseKind.SqlServer ? 1 : 3);
            var login = siteLogin.Length > 0 ? $"its app pool's identity ({siteLogin})" : "its app pool's identity";
            hint.Text = type switch
            {
                DatabaseKind.Container => $"On the local SQL container at {containerServer}, signed in to as '{options.Docker.SqlUser}' - " +
                                          "as Settings → Database server has it.",
                DatabaseKind.LocalDbFile => $"The site's own App_Data\\{(NameText().Length > 0 ? NameText() : DatabaseConnection.LocalDbFileName)}, " +
                                            $"attached by LocalDB ({localDbServer}) - the site signs in as {login}.",
                _ => SqlLogin() ? "The site signs in with this SQL Server login - its password is written to web.config."
                    : $"The site signs in as {login}."
            };
            var wrong = FieldProblem();
            var connection = wrong is null ? Chosen() : null;
            test.IsEnabled = connection is not null && !testing;
            var testedNow = connection is not null && tested is not null && Same(connection, tested);
            if (!testing) ShowChecks(testedNow ? report : null);
            DialogKit.Say(problem, wrong ?? (testedNow && report is { Passed: false } ? "The last test failed - save anyway?" : null));
            ok.IsEnabled = connection is not null && (current is null || !Same(connection, current));
        }

        void FieldChanged()
        {
            if (!filling) Update();
        }

        void TypeChanged()
        {
            if (filling) return;
            var chosen = dbSqlServer.IsChecked == true ? DatabaseKind.SqlServer : dbLocalDb.IsChecked == true ? DatabaseKind.LocalDbFile : DatabaseKind.Container;
            if (chosen == type) return;
            filling = true;
            // The database box changes meaning between a database name and LocalDB's file.
            if (type == DatabaseKind.LocalDbFile)
            {
                localDbFile = NameText();
                name.Text = databaseName;
            }
            else if (chosen == DatabaseKind.LocalDbFile)
            {
                databaseName = NameText();
                name.Text = localDbFile;
            }
            if (chosen == DatabaseKind.SqlServer && !sqlServerFilled) FillSqlServer();
            type = chosen;
            filling = false;
            Update();
        }

        dbContainer.IsChecked = type == DatabaseKind.Container;
        dbSqlServer.IsChecked = type == DatabaseKind.SqlServer;
        dbLocalDb.IsChecked = type == DatabaseKind.LocalDbFile;
        name.Text = type == DatabaseKind.LocalDbFile ? localDbFile : databaseName;
        if (type == DatabaseKind.SqlServer) FillSqlServer();
        filling = false;
        foreach (var radio in new[] { dbContainer, dbSqlServer, dbLocalDb }) radio.IsCheckedChanged += (_, _) => TypeChanged();
        foreach (var radio in new[] { windowsAuth, sqlAuth }) radio.IsCheckedChanged += (_, _) => FieldChanged();
        foreach (var box in new[] { server, name, user }) box.TextChanged += (_, _) => FieldChanged();
        password.Changed += FieldChanged;
        test.Click += async (_, _) =>
        {
            if (FieldProblem() is not null || testing) return;
            var connection = Chosen();
            testing = true;
            test.IsEnabled = false;
            test.Content = "Testing…";
            checks.Children.Clear();
            checks.Children.Add(new TextBlock { Text = "Testing the connection…", Classes = { "hint" } });
            try
            {
                report = await databases.CheckAsync(connection,
                    new DatabaseCheckOptions(ForNewInstall: false, SiteLogin: connection.UsesWindowsAuthentication && siteLogin.Length > 0 ? siteLogin : null),
                    CancellationToken.None);
                tested = connection;
            }
            finally
            {
                testing = false;
                test.Content = "Test connection";
                Update();
            }
        };
        ok.Click += (_, _) =>
        {
            if (FieldProblem() is null) DialogKit.Accept(window);
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Intro($"Where '{site}' finds its database - the SiteSqlServer connection string in its web.config. The site is " +
                                "restarted after saving. Nothing in the databases is moved or copied: point it at a database that holds " +
                                "this site, or restore a backup there first."),
                new TextBlock { Text = "Connection type", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) },
                new WrapPanel { Children = { dbContainer, dbSqlServer, dbLocalDb } },
                grid, hint, test, checks, problem, DialogKit.Buttons(window, ok)
            }
        };
        Update();
        window.Opened += (_, _) => (type == DatabaseKind.SqlServer ? server : name).Focus();
        return await DialogKit.ShowAsync(window) && FieldProblem() is null ? Chosen() : null;
    }
}

/// <summary>The DNN a project is to be upgraded to.</summary>
public sealed record UpgradeChoice(string ReleaseApiUrl, string Tag, string Version);

/// <summary>
/// Which newer DNN a project is upgraded to - only releases above its DNN, the latest chosen - and the plan DNN's upgrade
/// path makes of it, each step with what the analyser checked (the Windows app's UpgradeDnnDialog). The upgrade starts
/// only when nothing blocks.
/// </summary>
internal static class UpgradeDnnDialog
{
    private sealed record Source(string Api, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Option(DnnRelease Release, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PlanLine(string Kind, string Mark, string Text, string? Tip, Thickness Margin);

    public static async Task<UpgradeChoice?> ShowAsync(IServiceProvider services, string site, string directory, string current)
    {
        var releases = services.GetRequiredService<IDnnReleaseService>();
        var currentNumber = DnnInstall.Number(current);
        var window = DialogKit.Window("Upgrade DNN", 700, resizable: true, height: 640);
        window.MinWidth = 560;
        window.MinHeight = 480;
        var sources = releases.KnownReleaseApis.Select(api => new Source(api, Label(api))).ToList();
        var repository = new ComboBox { ItemsSource = sources, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(repository, "Repository");
        var repositoryPanel = new StackPanel
        {
            IsVisible = sources.Count > 1,
            Children = { new TextBlock { Text = "Repository", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) }, repository }
        };
        var version = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(version, "Upgrade to");
        var versionHint = DialogKit.Hint();
        var plan = new StackPanel();
        var problem = DialogKit.Problem();
        var ok = DialogKit.Ok("Start upgrade", "Back up and upgrade, step by step (Enter)");
        ok.IsEnabled = false;
        DnnUpgradePlan? planned = null;
        int loading = 0, planning = 0;

        void Show(string? wrong, string? hintText)
        {
            DialogKit.Say(problem, wrong);
            if (hintText is not null) versionHint.Text = hintText;
            ok.IsEnabled = wrong is null && planned is { CanStart: true };
        }

        void ShowPlan(IEnumerable<PlanLine> lines)
        {
            plan.Children.Clear();
            foreach (var line in lines)
            {
                var row = new DockPanel { Margin = line.Margin };
                var mark = new TextBlock { Text = line.Mark, FontWeight = FontWeight.SemiBold, Width = line.Kind == "Heading" ? 0 : 74 };
                mark.Bind(TextBlock.ForegroundProperty, mark.GetResourceObservable(line.Kind switch
                {
                    "Compatible" => "SuccessText", "Warning" => "OutWarnText", "Blocking" => "ErrorText", _ => "TextMuted"
                }));
                var text = new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, FontWeight = line.Kind == "Heading" ? FontWeight.SemiBold : FontWeight.Normal };
                if (line.Kind == "Blocking") text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("ErrorText"));
                if (line.Tip is not null) ToolTip.SetTip(text, line.Tip);
                row.Children.Add(mark);
                row.Children.Add(text);
                plan.Children.Add(row);
            }
        }

        repository.SelectionChanged += async (_, _) =>
        {
            if (repository.SelectedItem is not Source source) return;
            var mine = ++loading;
            version.ItemsSource = null;
            Show(null, "Loading the versions…");
            Result<DnnReleaseList> result;
            try { result = await releases.ListReleasesAsync(source.Api, CancellationToken.None); }
            catch (Exception ex) { result = Result<DnnReleaseList>.Fail(ex.Message); }
            if (mine != loading) return;
            if (!result.Success)
            {
                Show($"Could not load the versions of {source.Label}: {result.Error}", null);
                return;
            }
            // Only what is newer than the site's DNN - an upgrade never goes back - and no pre-release.
            var newer = result.Value!.Releases
                .Where(r => !r.Prerelease && DnnInstall.Number(r.Version) is { } number && (currentNumber is null || number > currentNumber))
                .ToList();
            if (newer.Count == 0)
            {
                Show(null, $"The site runs the newest DNN release of {source.Label} - there is nothing newer to upgrade to.");
                return;
            }
            version.ItemsSource = newer.Select(r => new Option(r, r.Version + (r == newer[0] ? "  (latest)" : ""))).ToList();
            version.SelectedIndex = 0;
            if (result.Value.SavedAt is { } savedAt)
                Show(null, $"GitHub can't be reached - the releases as of {savedAt:g}. The upgrade downloads each step's package, so it needs internet.");
        };

        version.SelectionChanged += async (_, _) =>
        {
            planned = null;
            ok.IsEnabled = false;
            if (version.SelectedItem is not Option option || DnnInstall.Number(option.Release.Version) is not { } target) return;
            var mine = ++planning;
            ShowPlan([new PlanLine("Heading", "", "Analysing the site, its database, IIS and this PC…", null, new Thickness(0))]);
            Result<DnnUpgradePlan> result;
            using (var scope = services.CreateScope())
            {
                var useCase = scope.ServiceProvider.GetRequiredService<UpgradeDnnUseCase>();
                result = await Task.Run(() => useCase.PlanAsync(site, directory, target, CancellationToken.None));
            }
            if (mine != planning) return;
            if (!result.Success)
            {
                Show($"The site couldn't be analysed: {result.Error}", null);
                return;
            }
            planned = result.Value!;
            ShowPlan(Lines(planned));
            var blocking = planned.Blocking;
            Show(blocking.Count > 0 ? $"{blocking.Count} blocking problem(s) - the upgrade can't start until they're solved (see the plan)." : null,
                $"{planned.Steps.Count} step(s): {string.Join(" → ", new[] { DnnUpgradeStep.Name(planned.Current) }.Concat(planned.Steps.Select(s => DnnUpgradeStep.Name(s.Step.To))))}");
        };

        ok.Click += (_, _) => DialogKit.Accept(window);
        var top = new StackPanel
        {
            Children =
            {
                DialogKit.Intro($"Upgrades '{site}' from DNN {current} as DNN's suggested upgrade path says - through every version it lists on " +
                                "the way, never straight to the newest. Before each step the site and its database are backed up; after it the " +
                                "site is restarted and checked. A step that fails puts the site back to the version before it."),
                repositoryPanel,
                new TextBlock { Text = "Upgrade to", Classes = { "fieldLabel" }, Margin = new Thickness(0, 12, 0, 4) }, version, versionHint
            }
        };
        var bottom = new StackPanel { Children = { problem, DialogKit.Buttons(window, ok) } };
        var planCard = new Border
        {
            Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Padding = new Thickness(12, 10), Content = plan }
        };
        planCard.Bind(Border.BackgroundProperty, planCard.GetResourceObservable("CardBg"));
        planCard.Bind(Border.BorderBrushProperty, planCard.GetResourceObservable("CardBorder"));
        planCard.Bind(Border.CornerRadiusProperty, planCard.GetResourceObservable("ControlRadius"));
        var dock = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.AddRange([top, bottom, planCard]);
        window.Content = dock;
        window.Opened += (_, _) =>
        {
            repository.SelectedIndex = sources.Count > 0 ? 0 : -1;
            if (sources.Count == 0) Show("No DNN repository is set (Settings → DNN releases).", null);
        };
        if (!await DialogKit.ShowAsync(window) || repository.SelectedItem is not Source chosenSource || version.SelectedItem is not Option chosen) return null;
        return new UpgradeChoice(chosenSource.Api, chosen.Release.TagName, chosen.Release.Version);
    }

    private static List<PlanLine> Lines(DnnUpgradePlan plan)
    {
        var lines = new List<PlanLine>
        {
            new("Heading", "", $"DNN {DnnUpgradeStep.Name(plan.Current)} → {DnnUpgradeStep.Name(plan.Target)}: the required path", null, new Thickness(0, 0, 0, 4))
        };
        lines.AddRange(plan.Steps.Select((s, i) => new PlanLine("Note", $"Step {i + 1}", $"{s.Step}  ({Method(s.Step.Method)})", null, new Thickness(0, 1, 0, 1))));
        lines.Add(new("Heading", "", "The site", null, new Thickness(0, 12, 0, 4)));
        lines.AddRange(plan.Site.Select(Line));
        foreach (var (step, i) in plan.Steps.Select((s, i) => (s, i)))
        {
            lines.Add(new("Heading", "", $"Step {i + 1} - {step.Step}", null, new Thickness(0, 12, 0, 4)));
            lines.AddRange(step.Checks.Select(Line));
        }
        return lines;

        static PlanLine Line(UpgradeFinding f) => new(f.Severity.ToString(), f.Severity.ToString(),
            $"{f.Area}: {f.Title}" + (f.Detail is null ? "" : $" - {f.Detail}") + (f.Fix is null ? "" : $" → {f.Fix}"), f.Fix, new Thickness(0, 1, 0, 1));

        static string Method(DnnUpgradeMethod method) => method switch
        {
            DnnUpgradeMethod.LocalUpgrade => "DNN's local upgrade, from the install package",
            DnnUpgradeMethod.Manual => "by hand",
            _ => "upgrade package"
        };
    }

    /// <summary><c>https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases</c> → <c>dnnsoftware/Dnn.Platform</c>.</summary>
    private static string Label(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return api;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "repos" ? $"{parts[1]}/{parts[2]}" : api;
    }
}
