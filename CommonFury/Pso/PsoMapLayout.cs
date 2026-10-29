using CommonFury.Binary;

namespace CommonFury.Pso;

internal static class PsoMapLayout
{
    internal static (PsoEntry Key, PsoEntry Item) Descriptors(PsoStruct owner, PsoEntry entry)
    {
        int key = (int)(entry.ReferenceKey >> 16), item = (int)(entry.ReferenceKey & 0xFFFF);
        BinaryLayout.Require(entry.Subtype == 1 && key < owner.Entries.Count && item < owner.Entries.Count && key != item,
            "Binary map has invalid key/item descriptor indexes.");
        var k = owner.Entries[key];
        var v = owner.Entries[item];
        BinaryLayout.Require(k.IsAnonymous && v.IsAnonymous, "Binary map key/item descriptors must be anonymous.");
        return (k, v);
    }

    internal static PsoStruct Pair(PsoSchema schema, PsoStruct owner, PsoEntry entry, uint hash)
    {
        var (key, item) = Descriptors(owner, entry);
        if (schema.ByHash.GetValueOrDefault(hash) is not PsoStruct pair)
            throw new InvalidDataException("Binary map pair schema is missing.");
        PsoEntry first = default, second = default;
        int count = 0;
        for (int i = 0; i < pair.Entries.Count && count <= 2; i++)
        {
            PsoEntry value = pair.Entries[i];
            if (value.IsAnonymous)
                continue;
            if (count == 0)
                first = value;
            else
                second = value;
            count++;
        }
        BinaryLayout.Require(pair.Length > 0 && count == 2 && Equivalent(first, key) && Equivalent(second, item),
            "Binary map pair schema disagrees with its key/item descriptors.");
        return pair;
    }

    private static bool Equivalent(PsoEntry first, PsoEntry second) => first.DataType == second.DataType && first.Subtype == second.Subtype && first.ReferenceKey == second.ReferenceKey;
    internal static PsoEntry Array(PsoEntry entry) => new(entry.NameHash, PsoDataType.Array, 0, checked((ushort)(entry.DataOffset + 8)), 0);
    internal static PsoEntry Element(uint hash) => new(PsoEntry.ArrayInfoHash, PsoDataType.Structure, 0, 0, hash);
    internal static PsoArray Values(PsoMap map) => new(map.Entries.Select(node => (PsoValue)new PsoNodeValue(node)).ToArray());
}
