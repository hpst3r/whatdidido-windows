using System.Text.Json;

namespace WhatDidIDo.Tray.Core.Control;

/// <summary>
/// A microphone from <c>GET /control/v1/microphones</c>. <see cref="Id"/> is a
/// serial that changes whenever the device re-enumerates (dock, suspend);
/// <see cref="StableId"/> (newer servers) does not. <see cref="ReportsStableId"/>:
/// the server sent the key at all (older servers don't).
/// </summary>
public sealed record MicItem(string Id, string Label, string? StableId, bool ReportsStableId = true);

/// <summary>The inventory: an opaque digest the mic request echoes, and the input sources.</summary>
public sealed record MicInventory(string Inventory, IReadOnlyList<MicItem> Microphones)
{
    public static MicInventory Parse(JsonElement root)
    {
        var digest = root.TryGetProperty("inventory", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
        var items = new List<MicItem>();
        if (root.TryGetProperty("microphones", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object
                    || !m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } idText)
                    continue;
                var label = m.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
                var reports = m.TryGetProperty("stable_id", out var s);
                var stable = reports && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } st ? st : null;
                items.Add(new MicItem(idText, label, stable, reports));
            }
        }
        return new MicInventory(digest, items);
    }
}
