using System.Text;
using System.Text.Json;

namespace WhatDidIDo.Tray.Core.Control;

/// <summary>
/// Request bodies: a flat JSON object of strings, booleans and integers,
/// written in the given key order without reflection, so Core stays usable
/// trimmed or AOT-compiled.
/// </summary>
public static class JsonBody
{
    public static string Write(IEnumerable<KeyValuePair<string, object>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in members)
            {
                switch (value)
                {
                    case string s:
                        writer.WriteString(key, s);
                        break;
                    case bool b:
                        writer.WriteBoolean(key, b);
                        break;
                    case int i:
                        writer.WriteNumber(key, i);
                        break;
                    default:
                        throw new ArgumentException($"unsupported JSON value for {key}", nameof(members));
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
