using System.Collections;
using System.Globalization;
using System.Reflection;

namespace DnnManager.Infrastructure.Data;

/// <summary>
/// An object as rows of a key and a value - how the settings and the workspace are kept in DNN Manager's database, one
/// row per value. A key is the path to the value in camelCase: <c>projects.sitePort</c>; an item of a list
/// <c>projects.dnnReleaseSources[0]</c> (the list itself, <c>projects.dnnReleaseSources</c>, holds how many there are); an
/// entry of a dictionary <c>keyboard.shortcuts{project.start}</c> (its key %-escaped). Values are text, written and read
/// the same on every PC (invariant culture). Only properties that can be set are kept.
/// </summary>
public static class ValueRows
{
    /// <summary>The rows of <paramref name="value"/> - a null value has none.</summary>
    public static Dictionary<string, string> From(object value)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        AddObject(value, "", rows);
        return rows;
    }

    /// <summary>
    /// Puts <paramref name="rows"/> into <paramref name="target"/> - a value without a row keeps the one it has (its
    /// default). Returns the keys whose value couldn't be read; those keep theirs too.
    /// </summary>
    public static IReadOnlyList<string> Into(object target, IReadOnlyDictionary<string, string> rows)
    {
        var problems = new List<string>();
        ReadObject(target, "", rows, problems);
        return problems;
    }

    // ─── Writing ──────────────────────────────────────────────────────────

    private static void AddObject(object value, string prefix, Dictionary<string, string> rows)
    {
        foreach (var property in Properties(value.GetType()))
            Add(property.GetValue(value), property.PropertyType, prefix + Name(property), rows);
    }

    private static void Add(object? value, Type type, string key, Dictionary<string, string> rows)
    {
        if (value is null) return;
        if (IsScalar(type))
        {
            rows[key] = Format(value);
        }
        else if (DictionaryValueType(type) is { } entryType)
        {
            var dictionary = (IDictionary)value;
            rows[key] = dictionary.Count.ToString(CultureInfo.InvariantCulture);
            foreach (DictionaryEntry entry in dictionary)
                Add(entry.Value, entryType, $"{key}{{{Uri.EscapeDataString((string)entry.Key)}}}", rows);
        }
        else if (ListItemType(type) is { } itemType)
        {
            var list = (IList)value;
            rows[key] = list.Count.ToString(CultureInfo.InvariantCulture);
            for (var i = 0; i < list.Count; i++) Add(list[i], itemType, $"{key}[{i}]", rows);
        }
        else
        {
            AddObject(value, key + ".", rows);
        }
    }

    // ─── Reading ──────────────────────────────────────────────────────────

    private static void ReadObject(object target, string prefix, IReadOnlyDictionary<string, string> rows, List<string> problems)
    {
        foreach (var property in Properties(target.GetType()))
        {
            var key = prefix + Name(property);
            var type = property.PropertyType;
            if (IsScalar(type))
            {
                if (!rows.TryGetValue(key, out var text)) continue;
                if (TryParse(text, type, out var parsed)) property.SetValue(target, parsed);
                else problems.Add(key);
            }
            else if (DictionaryValueType(type) is not null || ListItemType(type) is not null)
            {
                if (!rows.ContainsKey(key)) continue;
                if (Read(type, key, rows, problems, property.GetValue(target)) is { } collection) property.SetValue(target, collection);
            }
            else if (property.GetValue(target) is { } nested)
            {
                ReadObject(nested, key + ".", rows, problems);
            }
            else if (Activator.CreateInstance(type) is { } created)
            {
                ReadObject(created, key + ".", rows, problems);
                property.SetValue(target, created);
            }
        }
    }

    /// <summary>
    /// A value of <paramref name="type"/> made from the rows under <paramref name="key"/>; null when there are none. A
    /// list or dictionary goes into <paramref name="into"/> when given - emptied first, its comparer kept.
    /// </summary>
    private static object? Read(Type type, string key, IReadOnlyDictionary<string, string> rows, List<string> problems, object? into = null)
    {
        if (IsScalar(type))
        {
            if (!rows.TryGetValue(key, out var text)) return null;
            if (TryParse(text, type, out var parsed)) return parsed;
            problems.Add(key);
            return null;
        }
        if (DictionaryValueType(type) is { } entryType)
        {
            var dictionary = into as IDictionary ?? (IDictionary)Activator.CreateInstance(type)!;
            dictionary.Clear();
            var start = key + "{";
            foreach (var row in rows.Keys.Where(k => k.StartsWith(start, StringComparison.Ordinal)))
            {
                var end = row.IndexOf('}', start.Length);
                if (end < 0) continue;
                var entryKey = Uri.UnescapeDataString(row[start.Length..end]);
                if (dictionary.Contains(entryKey)) continue; // the rows of an entry's own values
                if (Read(entryType, row[..(end + 1)], rows, problems) is { } entry) dictionary[entryKey] = entry;
            }
            return dictionary;
        }
        if (ListItemType(type) is { } itemType)
        {
            if (!rows.TryGetValue(key, out var countText) || !int.TryParse(countText, CultureInfo.InvariantCulture, out var count) || count < 0)
            {
                problems.Add(key);
                return null;
            }
            var list = into as IList ?? (IList)Activator.CreateInstance(type)!;
            list.Clear();
            for (var i = 0; i < count; i++)
                if (Read(itemType, $"{key}[{i}]", rows, problems) is { } item) list.Add(item);
            return list;
        }
        var created = Activator.CreateInstance(type)!;
        ReadObject(created, key + ".", rows, problems);
        return created;
    }

    // ─── Types ────────────────────────────────────────────────────────────

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.SetMethod!.IsPublic);

    private static string Name(PropertyInfo property) => char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(TimeSpan);
    }

    private static Type? ListItemType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    private static Type? DictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string)
            ? type.GetGenericArguments()[1]
            : null;

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static bool TryParse(string text, Type type, out object? value)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        value = null;
        try
        {
            if (t == typeof(string)) value = text;
            else if (t == typeof(bool)) value = bool.Parse(text);
            else if (t == typeof(DateTime)) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            else if (t == typeof(TimeSpan)) value = TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture);
            else if (t.IsEnum) value = Enum.Parse(t, text, ignoreCase: true);
            else value = Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }
}
