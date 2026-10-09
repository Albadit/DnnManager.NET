using System.Security.Cryptography;

namespace DnnManager.Application.Configuration;

/// <summary>Passwords DNN Manager makes up itself - for the SQL Server container's sa.</summary>
public static class SqlPasswords
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", Lower = "abcdefghijkmnopqrstuvwxyz", Digits = "23456789", Symbols = "-_.!";

    /// <summary>
    /// 24 random characters with upper and lower case, digits and symbols - what SQL Server's password policy asks - and
    /// nothing a connection string, docker-compose.yml or a command line would need to quote (no ; = ' " $ or spaces).
    /// </summary>
    public static string New()
    {
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)], Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)], Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)]
        };
        while (chars.Count < 24) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        // The four kinds anywhere in it, not always first.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string([.. chars]);
    }
}
