using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace DnnManager.IntegrationTests;

/// <summary>
/// DNN Manager's own database file: its tables made once even when several threads open it first at the same moment,
/// a file of a newer DNN Manager left alone, a copy kept before the tables change, a damaged file put aside and brought
/// back from that copy - and a native SQLite with the fix for CVE-2025-6965.
/// </summary>
[TestClass]
public sealed class AppDatabaseTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerDbTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* SQLite may hold a file for a moment */ }
    }

    private AppDatabase Database => new(_dir);

    /// <summary>A connection to <paramref name="file"/> without AppDatabase - to set up or look at a file as it is.</summary>
    private static SqliteConnection Raw(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long UserVersion(string file)
    {
        using var connection = Raw(file);
        return AppDatabase.Scalar<long>(connection, "PRAGMA user_version");
    }

    private long CurrentVersion()
    {
        using (Database.Open()) { }
        return UserVersion(Database.Path);
    }

    [TestMethod]
    public void Threads_opening_a_new_file_at_once_make_its_tables_once()
    {
        for (var round = 0; round < 5; round++)
        {
            var folder = Path.Combine(_dir, $"round{round}");
            var database = new AppDatabase(folder);
            const int threads = 12;
            using var start = new Barrier(threads);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                try { using (database.Open()) { } }
                catch (Exception ex) { errors.Add(ex); }
            })).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());

            Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors.Select(e => e.Message)));
            using var connection = database.Open();
            AppDatabase.Execute(connection, "INSERT INTO settings (key, value) VALUES ('a', 'b')");
            Assert.AreEqual(UserVersion(Path.Combine(_dir, "round0", AppDatabase.FileName)), UserVersion(database.Path),
                "Every round ends at the same, current version.");
        }
    }

    [TestMethod]
    public void A_file_of_a_newer_DNN_Manager_is_left_alone()
    {
        var current = CurrentVersion();
        using (var connection = Raw(Database.Path)) AppDatabase.Execute(connection, $"PRAGMA user_version = {current + 3}");

        var error = Assert.ThrowsExactly<NewerDatabaseException>(() => Database.Open());
        StringAssert.Contains(error.Message, "newer DNN Manager");
        Assert.IsInstanceOfType<IOException>(error, "Handled wherever a database that can't be used is.");
        Assert.AreEqual(current + 3, UserVersion(Database.Path), "Not changed.");
    }

    [TestMethod]
    public void A_copy_is_kept_before_the_tables_change()
    {
        var current = CurrentVersion();
        using (var connection = Raw(Database.Path))
        {
            AppDatabase.Execute(connection, "INSERT INTO settings (key, value) VALUES ('projects.sitePort', '8080')");
            // As an older version left it: the last step (dropping a table that is gone) runs again, harmlessly.
            AppDatabase.Execute(connection, $"PRAGMA user_version = {current - 1}");
        }

        using (Database.Open()) { }

        Assert.AreEqual(current, UserVersion(Database.Path));
        Assert.IsTrue(File.Exists(Database.BackupPath), "The copy is there.");
        Assert.AreEqual(current - 1, UserVersion(Database.BackupPath), "It is the file as it was before.");
        using var backup = Raw(Database.BackupPath);
        Assert.AreEqual("8080", AppDatabase.Scalar<string>(backup, "SELECT value FROM settings WHERE key = 'projects.sitePort'"));
        Assert.IsFalse(File.Exists(Database.BackupPath + ".tmp"), "Nothing half-written left beside it.");
    }

    [TestMethod]
    public void A_new_file_makes_no_copy()
    {
        using (Database.Open()) { }
        Assert.IsFalse(File.Exists(Database.BackupPath));
    }

    [TestMethod]
    public void A_sound_file_is_left_where_it_is()
    {
        Assert.IsNull(Database.RecoverIfDamaged(), "No file yet.");
        using (Database.Open()) { }
        Assert.IsNull(Database.RecoverIfDamaged());
        Assert.IsTrue(File.Exists(Database.Path));
    }

    [TestMethod]
    public void A_file_that_isnt_a_database_is_put_aside_and_made_anew()
    {
        File.WriteAllText(Database.Path, "this is not an SQLite database, it was overwritten by something else entirely");

        var message = Database.RecoverIfDamaged();

        StringAssert.Contains(message, "was damaged");
        StringAssert.Contains(message, "defaults");
        var aside = Directory.GetFiles(_dir, "dnnmanager.damaged-*.db").Single();
        StringAssert.StartsWith(File.ReadAllText(aside), "this is not", "Kept as it was, to look into.");
        using var connection = Database.Open();
        Assert.AreEqual(0L, AppDatabase.Scalar<long>(connection, "SELECT COUNT(*) FROM settings"), "A new, empty file.");
    }

    [TestMethod]
    public void A_damaged_file_comes_back_from_the_copy()
    {
        var current = CurrentVersion();
        using (var connection = Raw(Database.Path))
        {
            AppDatabase.Execute(connection, "INSERT INTO settings (key, value) VALUES ('appearance.theme', 'dark')");
            AppDatabase.Execute(connection, $"PRAGMA user_version = {current - 1}");
        }
        using (Database.Open()) { } // makes the copy
        DamagePages(Database.Path);

        var message = Database.RecoverIfDamaged();

        StringAssert.Contains(message, "went back to the copy");
        using var restored = Database.Open();
        Assert.AreEqual("dark", AppDatabase.Scalar<string>(restored, "SELECT value FROM settings WHERE key = 'appearance.theme'"));
    }

    [TestMethod]
    public void A_damaged_file_after_a_failed_reset_can_be_reset()
    {
        File.WriteAllBytes(Database.Path, new byte[8192]);
        var store = new DnnManager.Infrastructure.Settings.SettingsStore(new DnnManager.Infrastructure.Settings.AppDataPaths(_dir));

        store.ResetToDefaults();

        Assert.IsTrue(store.SavedValues().Count > 0, "The start-up dialog's Reset to defaults works on a damaged file.");
    }

    [TestMethod]
    public void The_native_SQLite_has_the_fix_for_CVE_2025_6965()
    {
        using var connection = Database.Open();
        var version = new Version(AppDatabase.Scalar<string>(connection, "SELECT sqlite_version()")!);
        Assert.IsTrue(version >= new Version(3, 50, 2), $"SQLite {version}: GHSA-2m69-gcr7-jv3q is fixed in 3.50.2.");
    }

    /// <summary>Overwrites every page after the first with junk - a file a crash or a disk left broken.</summary>
    private static void DamagePages(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var pageSize = (bytes[16] << 8) | bytes[17];
        if (pageSize == 1) pageSize = 65536;
        for (var i = pageSize; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 % 251);
        File.WriteAllBytes(file, bytes);
    }
}
