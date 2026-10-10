using System.Runtime.InteropServices;
using System.Text;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.IntegrationTests;

/// <summary>
/// A program run as the user (UserProcess) - Docker Desktop installed for this account only: its arguments arrive as
/// they were given, its input and output go through as text or as bytes, it ends at its time, and it runs without
/// administrator rights. Started with the same token whether the tests run elevated or not.
/// </summary>
[TestClass]
public sealed class UserProcessTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [TestMethod]
    [DataRow("plain")]
    [DataRow("with space")]
    [DataRow("")]
    [DataRow("say \"hi\"")]
    [DataRow(@"C:\trailing\")]
    [DataRow(@"C:\with space\")]
    [DataRow(@"back\\""slash")]
    [DataRow("cat > \"$1\"")]
    public void An_argument_arrives_as_it_was_given(string argument)
    {
        var line = UserProcess.CommandLine(@"C:\Program Files\x\docker.exe", ["exec", argument, "last"]);
        var split = Split(line);
        CollectionAssert.AreEqual(new[] { @"C:\Program Files\x\docker.exe", "exec", argument, "last" }, split, line);
    }

    [TestMethod]
    public async Task Output_and_the_exit_code_come_back()
    {
        var result = await UserProcess.RunAsync(Cmd, ["/d", "/c", "echo hello& exit 3"], null, null, null, null, null, null, CancellationToken.None);
        Assert.AreEqual(3, result.ExitCode, result.StdErr);
        Assert.AreEqual("hello", result.StdOut.Trim());
    }

    [TestMethod]
    public async Task Input_goes_in_and_output_comes_out_as_bytes()
    {
        var sort = Path.Combine(Environment.SystemDirectory, "sort.exe");
        using var input = new MemoryStream(Encoding.ASCII.GetBytes("b\r\na\r\n"));
        using var output = new MemoryStream();

        var result = await UserProcess.RunAsync(sort, [], null, null, null, input, output, null, CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode, result.StdErr);
        Assert.AreEqual("a\r\nb\r\n", Encoding.ASCII.GetString(output.ToArray()));
    }

    [TestMethod]
    public async Task Its_environment_carries_the_values_given()
    {
        var result = await UserProcess.RunAsync(Cmd, ["/d", "/c", "echo %SQLCMDPASSWORD%"], new Dictionary<string, string?> { ["SQLCMDPASSWORD"] = "s3cret" },
            null, null, null, null, null, CancellationToken.None);
        Assert.AreEqual("s3cret", result.StdOut.Trim());
    }

    [TestMethod]
    public async Task It_ends_at_its_time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await UserProcess.RunAsync(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], null, null, null, null, null,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.AreEqual(-1, result.ExitCode);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"It waited {watch.Elapsed.TotalSeconds:0} s.");
    }

    [TestMethod]
    public async Task It_runs_without_administrator_rights()
    {
        var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        var result = await UserProcess.RunAsync(whoami, ["/groups", "/fo", "csv", "/nh"], null, null, null, null, null, null, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.StdErr);
        // Medium integrity; Administrators (S-1-5-32-544), when the user is one, only denies.
        StringAssert.Contains(result.StdOut, "S-1-16-8192");
        var administrators = result.StdOut.Split('\n').FirstOrDefault(l => l.Contains("S-1-5-32-544", StringComparison.Ordinal));
        if (administrators is not null) StringAssert.Contains(administrators, "deny only");
    }

    [TestMethod]
    public async Task Docker_installed_for_this_account_answers_when_run_as_the_user()
    {
        // Only where Docker Desktop is installed for this account (as on a developer's PC with its per-user install).
        var docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "DockerDesktop", "resources", "bin", "docker.exe");
        if (!File.Exists(docker)) return;
        var result = await UserProcess.RunAsync(docker, ["version", "--format", "{{.Client.Version}}"], null, null, null, null, null,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.StdErr);
        Assert.IsTrue(result.StdOut.Trim().Length > 0, result.StdErr);
    }

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
