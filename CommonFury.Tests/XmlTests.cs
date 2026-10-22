using System.Text;
using System.Xml.Linq;
using CommonFury.Xml;
using Xunit;

namespace CommonFury.Tests;

public sealed class XmlTests
{
    [Fact]
    public void DocumentCodecRejectsDtdsAndBoundsInput()
    {
        Assert.Throws<InvalidDataException>(() => XmlDocumentCodec.Read("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///private'>]><x>&a;</x>"u8));
        Assert.Throws<InvalidDataException>(() => XmlDocumentCodec.Read("<!DOCTYPE x [<!ENTITY a 'x'>]><x>&a;</x>"u8));
        Assert.Throws<InvalidDataException>(() => XmlDocumentCodec.Read("<x>"u8));
        Assert.Throws<InvalidDataException>(() => XmlDocumentCodec.Read("<x>abcdef</x>"u8, maximumCharacters: 8));
        XDocument document = XmlDocumentCodec.Read("<x> <y value=\"1\"/>\n</x>"u8);
        Assert.Equal(" ", ((XText)document.Root!.FirstNode!).Value);
    }

    [Fact]
    public void DocumentCodecWritesUtf8WithoutBomAndLfLines()
    {
        byte[] bytes = XmlDocumentCodec.Write(new XDocument(new XElement("root", new XElement("a", "é"))));
        Assert.NotEqual(0xEF, bytes[0]);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("<a>é</a>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ElementReaderReadsScalarsItemsAndReportsUnmodeledContent()
    {
        var element = XElement.Parse("""
            <Root kind="a" extra="b">
              <name> value </name>
              <count value="-3" />
              <enabled value="true" />
              <scale>1.5</scale>
              <list><Item>x</Item><item> y </item></list>
              <child><inner value="4" /><stray /></child>
              <unknown />
            </Root>
            """);
        var issues = new List<Diagnostic>();
        var reader = new XmlElementReader(element, issues, "Root");
        Assert.Same(element, reader.Element);
        Assert.Equal("a", reader.Attribute("kind"));
        Assert.Equal("", reader.Attribute("missing"));
        Assert.Equal("value", reader.Text("name"));
        Assert.Equal(-3, reader.Integer("count"));
        Assert.Equal(9, reader.Integer("absent", 9));
        Assert.True(reader.Boolean("enabled"));
        Assert.Null(reader.Boolean("absent"));
        Assert.Equal(1.5f, reader.Single("scale"));
        Assert.Equal(["x", "y"], reader.Strings("list"));
        Assert.Equal(4, reader.Object("child", child => (object)child.Integer("inner")));
        reader.Finish();
        Assert.All(issues, issue => Assert.Equal(XmlDiagnosticCodes.ContentUnmodeled, issue.Code));
        Assert.All(issues, issue => Assert.Equal(DiagnosticSeverity.Warning, issue.Severity));
        Assert.Equal(["Root/child/stray", "Root/unknown", "Root/@extra"], issues.Select(issue => issue.Path));
    }

    [Fact]
    public void ElementReaderRejectsDuplicatesAndBadScalars()
    {
        var issues = new List<Diagnostic>();
        Assert.Throws<InvalidDataException>(() => new XmlElementReader(XElement.Parse("<r><a/><a/></r>"), issues, "r").Child("a"));
        Assert.NotNull(new XmlElementReader(XElement.Parse("<r><a>1</a><a/></r>"), issues, "r").Child("a", allowTrailingEmptyContainers: true));
        Assert.Throws<InvalidDataException>(() => new XmlElementReader(XElement.Parse("<r><a value='x'/></r>"), issues, "r").Integer("a"));
        Assert.Throws<InvalidDataException>(() => new XmlElementReader(XElement.Parse("<r><a value='yes'/></r>"), issues, "r").Boolean("a"));
        Assert.Throws<InvalidDataException>(() => new XmlElementReader(XElement.Parse("<r><a value='1'>2</a></r>"), issues, "r").Integer("a"));
    }

    [Fact]
    public void ElementWritersRoundTripThroughTheReader()
    {
        var root = new XElement("Root",
            XmlElements.Text("name", "prop"),
            XmlElements.Value("enabled", false),
            XmlElements.Value("count", 5),
            XmlElements.Value("absent", null),
            XmlElements.Strings("list", ["a", "b"]),
            XmlElements.Items("pairs", [1, 2], value => new XElement("Item", XmlElements.Value("v", value))));
        Assert.Null(root.Element("absent"));
        var issues = new List<Diagnostic>();
        var reader = new XmlElementReader(root, issues, "Root");
        Assert.Equal("prop", reader.Text("name"));
        Assert.False(reader.Boolean("enabled"));
        Assert.Equal(5, reader.Integer("count"));
        Assert.Equal(["a", "b"], reader.Strings("list"));
        Assert.Equal([1, 2], reader.Items("pairs", item => item.Integer("v")));
        reader.Finish();
        Assert.Empty(issues);
    }

    [Fact]
    public void PlatformSelectionKeepsMatchingElementsAndDropsSelectors()
    {
        byte[] output = XmlPlatformSelection.Select(
            "<root><a platform=\"pc\"/><b platform=\"ps4|xboxone\"/><c platform=\"!ps4\"/><d platform=\"PS4|Win64\"/><e/></root>"u8,
            XmlPlatformSelection.Pc);
        XElement root = XElement.Parse(Encoding.UTF8.GetString(output));
        Assert.Equal(["a", "c", "d", "e"], root.Elements().Select(e => e.Name.LocalName));
        Assert.DoesNotContain(root.Descendants(), e => e.Attribute("platform") is not null);
        Assert.Throws<InvalidDataException>(() => XmlPlatformSelection.Select("<root platform=\"ps4\"/>"u8, XmlPlatformSelection.Pc));
        var console = new HashSet<string>(["ps4"], StringComparer.OrdinalIgnoreCase);
        Assert.True(XmlPlatformSelection.Applies("PS4", console));
        Assert.False(XmlPlatformSelection.Applies("pc", console));
        Assert.True(XmlPlatformSelection.Applies(" ", console));
    }
}
