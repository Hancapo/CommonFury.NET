using System.Buffers.Binary;
using CommonFury.Pso;
using Xunit;

namespace CommonFury.Tests;

// Wide RDR2 enum records, compact records, aliases and definition bounds.
// The fixture places its data block after the 16-byte PSIN prefix, which the shared reader requires.
public sealed class PsoSchemaTests
{
    internal const uint FileType = 0x3FF19213, RootType = 0xEFEBAB2F, EnumField = 0x149281CE, ScalarField = 0x44444444;
    internal const int SchemaOffset = 56, FirstEnumOffset = 72;

    internal static byte[] Fixture(int stride = 16, int scalar = 1)
    {
        (uint Hash, long Value)[] values =
        [
            (0x04DF4461, 0), (0xCBFB94E3, 1), (0xF549021B, 2),
            (0x11111111, -1), (0x22222222, stride == 16 ? 4294436772L : int.MinValue),
            (0x33333333, 1)
        ];
        int secondEnum = FirstEnumOffset + 4 + values.Length * stride;
        byte[] bytes = new byte[SchemaOffset + secondEnum + 12];
        "PSIN"u8.CopyTo(bytes); U32(bytes, 4, 24); bytes.AsSpan(8, 8).Fill(0x70);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), scalar); U32(bytes, 20, 0x12345678);
        "PMAP"u8.CopyTo(bytes.AsSpan(24)); U32(bytes, 28, 32); U32(bytes, 32, 1);
        U16(bytes, 36, 1); U32(bytes, 40, RootType); U32(bytes, 44, 16); U32(bytes, 52, 8);
        var schema = bytes.AsSpan(SchemaOffset);
        "PSCH"u8.CopyTo(schema); U32(schema, 4, (uint)schema.Length); U32(schema, 8, 3);
        U32(schema, 12, 0x55555555); U32(schema, 16, (uint)secondEnum);
        U32(schema, 20, RootType); U32(schema, 24, 36);
        U32(schema, 28, FileType); U32(schema, 32, FirstEnumOffset);
        U16(schema, 38, 2); U32(schema, 40, 8);
        U32(schema, 48, EnumField); schema[52] = (byte)PsoDataType.Enum; U32(schema, 56, FileType);
        U32(schema, 60, ScalarField); schema[64] = (byte)PsoDataType.UInt32; U16(schema, 66, 4);
        schema[FirstEnumOffset] = 1; U16(schema, FirstEnumOffset + 2, (ushort)values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            int at = FirstEnumOffset + 4 + i * stride;
            U32(schema, at, values[i].Hash);
            if (stride == 16) BinaryPrimitives.WriteInt64BigEndian(schema[(at + 8)..], values[i].Value);
            else BinaryPrimitives.WriteInt32BigEndian(schema[(at + 4)..], (int)values[i].Value);
        }
        schema[secondEnum] = 1; U16(schema, secondEnum + 2, 1);
        U32(schema, secondEnum + 4, 0xAAAAAAAA); U32(schema, secondEnum + 8, 1);
        return bytes;
    }

    private static void U32(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes[at..], value);
    private static void U16(Span<byte> bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes[at..], value);
    private static PsoEnum Enum(PsoFile file, uint hash) => Assert.IsType<PsoEnum>(file.Schema.ByHash[hash]);
    private static PsoField Field(PsoDocument document, uint hash) => document.Root.Fields.Single(f => f.Entry.NameHash == hash);

    [Fact]
    public void WideEnumEntriesRetainValuesAliasesAndSourceBytes()
    {
        byte[] bytes = Fixture();
        var file = PsoFile.Read(bytes);
        var enumeration = Enum(file, FileType);
        Assert.True(enumeration.IsWide);
        Assert.Equal(new long[] { 0, 1, 2, -1, 4294436772, 1 }, enumeration.Entries.Select(e => e.Value));
        Assert.Equal(new uint[] { 0x04DF4461, 0xCBFB94E3, 0xF549021B, 0x11111111, 0x22222222, 0x33333333 }, enumeration.Entries.Select(e => e.NameHash));
        Assert.False(Enum(file, 0x55555555).IsWide);
        var document = file.ReadRoot();
        Assert.Equal(1, Assert.IsType<PsoInteger>(Field(document, EnumField).Value).Value);
        Assert.Equal(0x12345678, Assert.IsType<PsoInteger>(Field(document, ScalarField).Value).Value);
        Assert.True(document.Report.IsValid);
        Assert.Equal(bytes, file.Write());
        // Canonical schema output keeps each enum's record width.
        Assert.Equal(file.Schema.Write(), PsoSchema.Read(file.Schema.Write()).Write());
        Assert.True(Assert.IsType<PsoEnum>(PsoSchema.Read(file.Schema.Write()).ByHash[FileType]).IsWide);
    }

    [Fact]
    public void CompactEnumsRemainReadableWithSignedValues()
    {
        var enumeration = Enum(PsoFile.Read(Fixture(8)), FileType);
        Assert.False(enumeration.IsWide);
        Assert.Equal(new long[] { 0, 1, 2, -1, int.MinValue, 1 }, enumeration.Entries.Select(e => e.Value));
        Assert.Throws<ArgumentException>(() => new PsoEnum(1, [new(2, 1L << 40)]));
        Assert.Throws<ArgumentException>(() => new PsoEnum(1, [new(2, 1, Reserved: 3)]));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void EnumMembersFollowReferenceKeyAndKeepAliases(int stride)
    {
        byte[] bytes = Fixture(stride);
        var file = PsoFile.Read(bytes);
        var field = Field(file.ReadRoot(), EnumField);
        Assert.Equal(new uint[] { 0xCBFB94E3, 0x33333333 }, file.Schema.FindEnumMembers(field.Entry, 1).Select(e => e.NameHash));
        U32(bytes, SchemaOffset + 56, 0x55555555);
        var other = PsoFile.Read(bytes);
        Assert.Equal(0xAAAAAAAAu, Assert.Single(other.Schema.FindEnumMembers(Field(other.ReadRoot(), EnumField).Entry, 1)).NameHash);
        Assert.Equal(bytes, other.Write());
    }

    [Theory]
    [InlineData(-1, 0x11111111u)]
    [InlineData(unchecked((int)4294436772u), 0x22222222u)]
    public void EnumMemberMatchingPreservesTheSignedFieldBitPattern(int scalar, uint label)
    {
        byte[] bytes = Fixture(scalar: scalar);
        foreach (var type in new[] { PsoDataType.Enum, PsoDataType.Flags })
        {
            bytes[SchemaOffset + 52] = (byte)type;
            var file = PsoFile.Read(bytes);
            var field = Field(file.ReadRoot(), EnumField);
            long value = Assert.IsType<PsoInteger>(field.Value).Value;
            Assert.Equal(scalar, value);
            Assert.Equal(label, Assert.Single(file.Schema.FindEnumMembers(field.Entry, value)).NameHash);
        }
    }

    [Fact]
    public void UnknownEnumValueRemainsNumericWithoutInventedMembers()
    {
        var file = PsoFile.Read(Fixture(scalar: 77));
        var field = Field(file.ReadRoot(), EnumField);
        Assert.Empty(file.Schema.FindEnumMembers(field.Entry, 77));
        Assert.Equal(77, Assert.IsType<PsoInteger>(field.Value).Value);
        Assert.Empty(file.Schema.FindEnumMembers(Field(file.ReadRoot(), ScalarField).Entry, 0x12345678));
    }

    [Fact]
    public void ReservedEnumWordIsPreservedWithoutBeingTreatedAsTheValue()
    {
        byte[] bytes = Fixture();
        U32(bytes, SchemaOffset + FirstEnumOffset + 8, 0xCAFEBABE);
        var file = PsoFile.Read(bytes);
        Assert.Equal(0xCAFEBABEu, Enum(file, FileType).Entries[0].Reserved);
        Assert.Equal(0L, Enum(file, FileType).Entries[0].Value);
        Assert.Equal(bytes, file.Write());
        Assert.Equal(0xCAFEBABEu, Assert.IsType<PsoEnum>(PsoSchema.Read(file.Schema.Write()).ByHash[FileType]).Entries[0].Reserved);
    }

    [Fact]
    public void WideConstantsAreNotTruncatedToMatchA32BitField()
    {
        byte[] bytes = Fixture(scalar: 77);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(SchemaOffset + FirstEnumOffset + 12), 0x10000004D);
        var file = PsoFile.Read(bytes);
        Assert.Equal(0x10000004DL, Enum(file, FileType).Entries[0].Value);
        Assert.Empty(file.Schema.FindEnumMembers(Field(file.ReadRoot(), EnumField).Entry, 77));
    }

    [Fact]
    public void DistinctSchemaIdentitiesCanShareADefinitionOffsetWhenAllowed()
    {
        byte[] bytes = Fixture()[..^12];
        U32(bytes, SchemaOffset + 4, (uint)(bytes.Length - SchemaOffset));
        U32(bytes, SchemaOffset + 16, FirstEnumOffset);
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(bytes));
        var file = PsoFile.Read(bytes, new() { AllowAliasedSchemaDefinitions = true });
        Assert.Equal(6, Enum(file, 0x55555555).Entries.Count);
        Assert.Equal(Enum(file, FileType).Entries, Enum(file, 0x55555555).Entries);
        Assert.Equal(bytes, file.Write());
    }

    [Theory]
    [InlineData(FirstEnumOffset + 2, 13)] // enum spills into the following definition in either record width
    [InlineData(38, 3)] // structure fields spill into the enum
    public void DefinitionCannotConsumeItsNeighbor(int offset, ushort count)
    {
        byte[] bytes = Fixture();
        U16(bytes, SchemaOffset + offset, count);
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(uint.MaxValue)]
    public void SchemaOffsetMustPointBeyondTheIndex(uint offset)
    {
        byte[] bytes = Fixture();
        U32(bytes, SchemaOffset + 32, offset);
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(bytes));
    }

    [Fact]
    public void TruncatedEnumIsRejectedEvenWithACompleteSectionHeader()
    {
        byte[] bytes = Fixture()[..^1];
        U32(bytes, SchemaOffset + 4, (uint)(bytes.Length - SchemaOffset));
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(bytes));
    }
}
