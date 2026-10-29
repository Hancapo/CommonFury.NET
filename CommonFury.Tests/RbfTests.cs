using System.Numerics;
using System.Text;
using CommonFury.Rbf;
using Xunit;

namespace CommonFury.Tests;

public sealed class RbfTests
{
    [Fact]
    public void ReadsGtxdScenarioWithRepeatedNamesAndOwnedData()
    {
        byte[] input = GtxdFixture();
        RbfDocument document = RbfDocument.Read(input);
        Assert.Equal("CMapParentTxds", document.Root.Name);
        RbfStructure relationships = Assert.IsType<RbfStructure>(Assert.Single(document.Root.Children));
        Assert.Equal("txdRelationships", relationships.Name);
        Assert.Equal(2, relationships.Children.Count);
        var first = Assert.IsType<RbfStructure>(relationships.Children[0]);
        var second = Assert.IsType<RbfStructure>(relationships.Children[1]);
        Assert.Equal("shared_parent\0", Text(Assert.IsType<RbfStructure>(first.Children[0])));
        Assert.Equal("child_a\0", Text(Assert.IsType<RbfStructure>(first.Children[1])));
        Assert.Equal("child_b\0", Text(Assert.IsType<RbfStructure>(second.Children[1])));
        Assert.Equal(input, document.Write());
        input.AsSpan().Clear();
        Assert.Equal("child_b\0", Text(Assert.IsType<RbfStructure>(second.Children[1])));
    }

    [Fact]
    public void ReadsIndependentTypedRecordsCommentsAndReservedWords()
    {
        byte[] input = Convert.FromHexString(
            "52424630" +
            "00000400726F6F743412CDAB0200" +
            "FCFF020000008021" +
            "01100500636F756E74FFFFFFFF" +
            "026005006C6162656C0300686921" +
            "03200400666C6167" + "0330" +
            "044005007363616C650000C03F" +
            "05500300706F730000803F0000004000004040" +
            "0600040074657874000000000000FDFF0400000061FF0062FFFF" +
            "FFFFFCFF010000007A");
        RbfDocument document = RbfDocument.Read(input);
        Assert.Equal((ushort)0x1234, document.Root.Reserved1);
        Assert.Equal((ushort)0xABCD, document.Root.Reserved2);
        Assert.Equal(0xABCD1234u, document.Root.Reserved);
        Assert.Equal(new byte[] { 0x80, 0x21 }, Assert.IsType<RbfComment>(document.Root.Attributes[0]).Data);
        Assert.Equal(-1, Assert.IsType<RbfInt32Value>(document.Root.Attributes[1]).Value);
        Assert.Equal("hi!", Assert.IsType<RbfStringValue>(document.Root.Attributes[2]).Value);
        Assert.True(Assert.IsType<RbfBooleanValue>(document.Root.Children[0]).Value);
        Assert.False(Assert.IsType<RbfBooleanValue>(document.Root.Children[1]).Value);
        Assert.Equal(1.5f, Assert.IsType<RbfFloatValue>(document.Root.Children[2]).Value);
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<RbfVector3Value>(document.Root.Children[3]).Value);
        Assert.Equal(new byte[] { 0x61, 0xFF, 0, 0x62 },
            Assert.IsType<RbfBytes>(Assert.Single(Assert.IsType<RbfStructure>(document.Root.Children[4]).Children)).Data);
        Assert.Equal(new byte[] { 0x7A }, Assert.Single(document.TrailingComments).Data);
        Assert.Equal(input, document.Write());
        Assert.Equal(
            [new(0, "root", RbfRecordType.Structure), new(1, "count", RbfRecordType.Int32), new(2, "label", RbfRecordType.String),
             new(3, "flag", RbfRecordType.True), new(4, "scale", RbfRecordType.Float), new(5, "pos", RbfRecordType.Vector3),
             new(6, "text", RbfRecordType.Structure)],
            document.Descriptors);
        Assert.Empty(new RbfDocument(new RbfStructure("authored")).Descriptors);
    }

    [Fact]
    public void EmitsExtendedIdsAtTheTwelveBitBoundaryAndAllowsTypeChangesForAName()
    {
        var root = new RbfStructure("root");
        for (int i = 1; i <= 4096; i++)
            root.Children.Add(new RbfBooleanValue($"n{i}", true));
        root.Children.Add(new RbfBooleanValue("n256", false));
        byte[] output = new RbfDocument(root).Write();
        Assert.True(output.AsSpan().EndsWith(Convert.FromHexString("FEFF0010002005006E343039360031FFFF")));
        RbfStructure decoded = RbfDocument.Read(output).Root;
        Assert.Equal(4097, decoded.Children.Count);
        Assert.Equal("n4096", Assert.IsType<RbfBooleanValue>(decoded.Children[4095]).Name);
        Assert.False(Assert.IsType<RbfBooleanValue>(decoded.Children[4096]).Value);
    }

    [Fact]
    public void ReadsUnsignedStringLengthsFromIndependentInput()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Convert.FromHexString("5242463000000100720000000001000160010073"));
            writer.Write((ushort)32768);
            writer.Write(Encoding.ASCII.GetBytes(new string('x', 32768)));
            writer.Write((ushort)0xFFFF);
        }
        RbfDocument document = RbfDocument.Read(stream.ToArray());
        Assert.Equal(32768, Assert.IsType<RbfStringValue>(Assert.Single(document.Root.Attributes)).Value.Length);
        Assert.Equal(stream.ToArray(), document.Write());
    }

    [Theory]
    [InlineData("52424630FFFF")]
    [InlineData("524246300100")]
    [InlineData("524246300070")]
    [InlineData("524246300000010072000000000100FFFF")]
    [InlineData("524246300000010072000000000000FDFF0300000061")]
    [InlineData("524246300000010072000000000000")]
    [InlineData("524246300000010072000000000000FFFF0000")]
    [InlineData("524246300000010080000000000000FFFF")]
    public void RejectsMalformedInputWithoutInventingMissingRecords(string hex)
    {
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ReportsCyclesAndUnsupportedStringElementsBeforeWriting()
    {
        var document = RbfDocument.Read(Convert.FromHexString(
            "5242463000000400726F6F74000000000000016005006C6162656C03006F6E65FFFF"));
        RbfStructure root = document.Root;
        Assert.Equal("one", Assert.IsType<RbfStringValue>(Assert.Single(root.Children)).Value);
        root.Children.Add(root);
        ValidationReport report = document.Validate();
        Assert.Contains(report.Diagnostics, issue => issue.Code == RbfDiagnosticCodes.StructureCycle);
        Assert.Contains(report.Diagnostics, issue => issue.Code == RbfDiagnosticCodes.StringElement);
        using var output = new MemoryStream();
        output.WriteByte(0xA5);
        Assert.Throws<ValidationException>(() => document.Write(output));
        Assert.Equal(new byte[] { 0xA5 }, output.ToArray());
    }

    [Fact]
    public void AppliesFiniteBudgetsToReadingAndWriting()
    {
        byte[] input = GtxdFixture();
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(input, new RbfLimits(maximumBytes: 16)));
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(input, new RbfLimits(maximumNodes: 3)));
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(input, new RbfLimits(maximumNames: 2)));
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(input, new RbfLimits(maximumDepth: 2)));
        RbfDocument document = RbfDocument.Read(input);
        Assert.Throws<ValidationException>(() => document.Write(new RbfLimits(maximumNodes: 3)));
        Assert.Throws<ValidationException>(() => document.Write(new RbfLimits(maximumBytes: 16)));
    }

    [Fact]
    public void ReadsAndRewritesSyntheticFixturesByteForByte()
    {
        // Synthetic RBF files.
        byte[] tree = Fixture("tree.ymt");
        RbfDocument document = RbfDocument.Read(tree);
        Assert.Equal("test", Assert.IsType<RbfStringValue>(Assert.Single(document.Root.Attributes)).Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<RbfBytes>(document.Root.Structures("payload").Single().Children[0]).Data);
        Assert.Equal(tree, document.Write());
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read(tree[..^1]));
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read([.. tree, 1]));
        Assert.Throws<InvalidDataException>(() => RbfDocument.Read("RBF0\xff\xff"u8));

        byte[] gtxd = Fixture("gtxd.ymt");
        RbfDocument relationships = RbfDocument.Read(gtxd);
        RbfStructure[] items = relationships.Root.Structures("txdRelationships").Single().Structures("item").ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("child_b", items[1].StringField("child"));
        Assert.Single(items.Select(item => item.StringField("parent")).Distinct());
        Assert.Equal(gtxd, relationships.Write());
    }

    [Fact]
    public void QueryHelpersFilterChildrenAndReadTextFields()
    {
        var root = new RbfStructure("Root") { Reserved = 7 };
        root.Attributes.Add(new RbfBooleanValue("enabled", true));
        root.Children.Add(new RbfInt32Value("number", 12));
        root.Children.Add(new RbfStringValue("text", " abc\0"));
        var named = new RbfStructure("name");
        named.Children.Add(new RbfBytes("prop\0"u8));
        root.Children.Add(named);
        root.Children.Add(new RbfStructure("other"));
        Assert.Equal((ushort)7, root.Reserved1);
        Assert.Equal(12, Assert.IsType<RbfInt32Value>(root.Values("number").Single()).Value);
        Assert.Equal(2, root.Values().Count());
        Assert.Empty(root.Values("enabled"));
        Assert.Equal(["name", "other"], root.Structures().Select(s => s.Name));
        Assert.Equal("abc", root.StringField("text"));
        Assert.Equal("12", root.StringField("number"));
        Assert.Equal("prop", root.StringField("name"));
        Assert.Equal("", root.StringField("other"));
        Assert.Equal("", root.StringField("missing"));
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", "rbf", name));

    private static string Text(RbfStructure structure) => Encoding.ASCII.GetString(Assert.IsType<RbfBytes>(Assert.Single(structure.Children)).Data);

    // Independent BinaryWriter packing is the oracle.
    private static byte[] GtxdFixture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RBF0"u8);
        Open(0, "CMapParentTxds");
        Open(1, "txdRelationships");
        Open(2, "item");
        Open(3, "parent");
        Bytes("shared_parent");
        Close();
        Open(4, "child");
        Bytes("child_a");
        Close();
        Close();
        Open(2);
        Open(3);
        Bytes("shared_parent");
        Close();
        Open(4);
        Bytes("child_b");
        Close();
        Close();
        Close();
        Close();
        return stream.ToArray();

        void Open(ushort id, string? name = null)
        {
            writer.Write(id);
            if (name is not null)
            { writer.Write((ushort)name.Length); writer.Write(Encoding.ASCII.GetBytes(name)); }
            writer.Write(new byte[6]);
        }
        void Bytes(string text)
        {
            byte[] data = Encoding.ASCII.GetBytes(text + '\0');
            writer.Write((ushort)0xFFFD);
            writer.Write(data.Length);
            writer.Write(data);
        }
        void Close() => writer.Write((ushort)0xFFFF);
    }

    [Fact]
    public void WritesBackEveryStringTheReaderAccepts()
    {
        // String records are length-prefixed, so embedded and trailing NULs survive; the reader accepts any ASCII byte.
        var root = new RbfStructure("root");
        root.Attributes.Add(new RbfStringValue("value", "abc\0"));
        root.Attributes.Add(new RbfStringValue("control", "\u0001\u007F"));
        byte[] written = new RbfDocument(root).Write();
        RbfDocument reread = RbfDocument.Read(written);
        Assert.Equal("abc\0", Assert.IsType<RbfStringValue>(reread.Root.Attributes[0]).Value);
        Assert.True(reread.Validate().IsValid);
        Assert.Equal(written, reread.Write());
        root.Attributes.Add(new RbfStringValue("wide", "é"));
        Assert.Contains(new RbfDocument(root).Validate().Diagnostics, d => d.Code == RbfDiagnosticCodes.Text);
    }

    [Fact]
    public void ValidationReportsEveryIssueAtItsRecordPath()
    {
        var root = new RbfStructure("root");
        root.Attributes.Add(new RbfInt32Value("a", 1));
        root.Attributes.Add(new RbfVector3Value("v", default));
        root.Attributes.Add(new RbfStructure("s"));
        root.Attributes.Add(null!);
        root.Attributes.Add(new RbfStringValue("t", "é"));
        root.Attributes.Add(new RbfInt32Value("ñ", 2));
        root.Children.Add(new RbfStringValue("a", "x"));
        var child = new RbfStructure("child");
        child.Attributes.Add(new RbfInt32Value("ñ", 3));
        child.Children.Add(new RbfBytes([1]));
        child.Children.Add(new RbfInt32Value("a", 4));
        root.Children.Add(child);
        root.Children.Add(root);
        var document = new RbfDocument(root);
        document.LeadingComments.Add(new RbfComment([2]));
        document.TrailingComments.Add(new RbfComment([3]));
        document.TrailingComments.Add(null!);

        string[] attributes =
        [
            "rbf.attribute.type Root.Attributes[1]", "rbf.attribute.type Root.Attributes[2]", "rbf.node.null Root.Attributes[3]",
            "rbf.text Root.Attributes[4].Value", "rbf.name Root.Attributes[5].Name", "rbf.string.element Root.Children[0]"
        ];
        Assert.Equal([.. attributes, "rbf.name Root.Children[1].Attributes[0].Name", "rbf.content.mixed Root.Children[1].Children",
            "rbf.structure.cycle Root.Children[2]", "rbf.node.null TrailingComments[1]"], Describe(document.Validate()));
        Assert.Equal([.. attributes, "rbf.limit.nodes Root.Children[1]"], Describe(document.Validate(new RbfLimits(maximumNodes: 9))));
        Assert.Equal([.. attributes, "rbf.limit.depth Root.Children[1]", "rbf.limit.depth Root.Children[2]", "rbf.node.null TrailingComments[1]"],
            Describe(document.Validate(new RbfLimits(maximumDepth: 1))));
        Assert.Equal(["rbf.attribute.type Root.Attributes[1]", "rbf.limit.names Root.Attributes[1]"],
            Describe(document.Validate(new RbfLimits(maximumNames: 2))));
        Assert.Equal(["rbf.attribute.type Root.Attributes[1]", "rbf.limit.bytes Root.Attributes[1]"],
            Describe(document.Validate(new RbfLimits(maximumBytes: 40))));
        Assert.Equal(["rbf.limit.bytes LeadingComments[0]"], Describe(document.Validate(new RbfLimits(maximumBytes: 5))));
        Assert.Equal(["rbf.limit.nodes Root.Attributes"], Describe(document.Validate(new RbfLimits(maximumNodes: 3))));

        static string[] Describe(ValidationReport report) => [.. report.Diagnostics.Select(d => $"{d.Code} {d.Path}")];
    }
}
