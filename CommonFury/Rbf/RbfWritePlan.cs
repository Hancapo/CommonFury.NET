using System.Globalization;
using System.Text;
using CommonFury.Binary;

namespace CommonFury.Rbf;

internal sealed class RbfWritePlan
{
    private readonly RbfLimits limits;
    private readonly List<Diagnostic> issues = [];
    private readonly List<Operation> operations = [];
    private readonly Dictionary<string, uint> names = new(StringComparer.Ordinal);
    private readonly HashSet<RbfStructure> active = new(ReferenceEqualityComparer.Instance);
    // Segments of the visited record's path, with -1 for no index; the text is built only for a diagnostic.
    private readonly List<(string Name, int Index)> path = [];
    private int nodes;
    private int size = 4;
    private bool stopped;

    internal ValidationReport Report { get; }

    internal RbfWritePlan(RbfDocument document, RbfLimits limits)
    {
        this.limits = limits;
        if (size > limits.MaximumBytes)
        {
            issues.Add(new(RbfDiagnosticCodes.LimitBytes, "RBF header exceeds the output byte budget.", "Root"));
            this.stopped = true;
        }
        for (int i = 0; i < document.LeadingComments.Count && !this.stopped; i++)
            Visit(document.LeadingComments[i], "LeadingComments", i, 0, attribute: false);
        if (!this.stopped)
            Visit(document.Root, "Root", -1, 1, attribute: false);
        for (int i = 0; i < document.TrailingComments.Count && !this.stopped; i++)
            Visit(document.TrailingComments[i], "TrailingComments", i, 0, attribute: false);
        Report = new ValidationReport(issues);
    }

    private void Visit(RbfNode? node, string segment, int index, int depth, bool attribute)
    {
        path.Add((segment, index));
        VisitRecord(node, depth, attribute);
        path.RemoveAt(path.Count - 1);
    }

    private void VisitRecord(RbfNode? node, int depth, bool attribute)
    {
        if (nodes == this.limits.MaximumNodes)
        {
            Stop(RbfDiagnosticCodes.LimitNodes, "RBF traversal stopped at the record budget.");
            return;
        }
        nodes++;
        if (node is null)
        { AddDiagnostic(RbfDiagnosticCodes.NodeNull, "RBF record lists cannot contain null."); return; }
        if (attribute && node is not RbfValue and not RbfComment)
        {
            AddDiagnostic(RbfDiagnosticCodes.AttributeType, "An RBF attribute must be an integer, Boolean, float or string record; comments may separate attributes.");
            return;
        }
        if (node is RbfStructure structure)
        {
            if (depth > this.limits.MaximumDepth)
            {
                AddDiagnostic(RbfDiagnosticCodes.LimitDepth, "Traversal of this branch stopped at the container depth budget.");
                return;
            }
            if (!active.Add(structure))
            {
                AddDiagnostic(RbfDiagnosticCodes.StructureCycle, "RBF containers form a tree on disk; this reference creates a cycle.");
                return;
            }
            try
            { VisitStructure(structure, depth); }
            finally { active.Remove(structure); }
            return;
        }
        if (node is RbfRawNode raw)
        {
            if (Reserve(6L + raw.Data.Length))
                operations.Add(new(node));
            return;
        }
        var value = (RbfValue)node;
        if (attribute && value is RbfVector3Value)
            AddDiagnostic(RbfDiagnosticCodes.AttributeType, "Vector records are not supported as attributes.");
        if (!attribute && value is RbfStringValue)
            AddDiagnostic(RbfDiagnosticCodes.StringElement, "String leaf records are readable, but export requires a string attribute or an explicit data container.");
        if (value is RbfStringValue text)
            ValidateText(text.Value, RbfDiagnosticCodes.Text, ".Value");
        int payloadSize = value switch
        {
            RbfInt32Value or RbfFloatValue => 4,
            RbfVector3Value => 12,
            RbfStringValue textValue => textValue.Value.Length,
            _ => 0
        };
        AddNamed(value, (long)payloadSize + (value is RbfStringValue ? 2 : 0));
    }

    private void VisitStructure(RbfStructure structure, int depth)
    {
        if (structure.Attributes.Count > this.limits.MaximumNodes - nodes)
        {
            Stop(RbfDiagnosticCodes.LimitNodes, "The attribute records exceed the remaining traversal budget.", ".Attributes");
            return;
        }
        int attributeCount = 0;
        foreach (RbfNode? attribute in structure.Attributes)
            if (attribute is not RbfComment)
                attributeCount++;
        if (attributeCount > ushort.MaxValue)
            AddDiagnostic(RbfDiagnosticCodes.AttributeCount, "The number of RBF attributes must fit an unsigned 16-bit count.", ".Attributes");
        AddNamed(structure, 6, attributeCount);
        for (int i = 0; i < structure.Attributes.Count && !this.stopped; i++)
            Visit(structure.Attributes[i], "Attributes", i, depth, attribute: true);
        bool hasData = false, hasElements = false;
        for (int i = 0; i < structure.Children.Count && !this.stopped; i++)
        {
            RbfNode? child = structure.Children[i];
            hasData |= child is RbfBytes;
            hasElements |= child is RbfNamedNode;
            Visit(child, "Children", i, depth + 1, attribute: false);
        }
        if (hasData && hasElements)
            AddDiagnostic(RbfDiagnosticCodes.ContentMixed, "Data content and child elements must be in separate containers.", ".Children");
        if (!this.stopped && Reserve(2))
            operations.Add(new(null));
    }

    private void AddNamed(RbfNamedNode node, long payloadSize, int attributeCount = 0)
    {
        // Only valid names enter the table, so a known name needs no validation.
        bool newName = !names.TryGetValue(node.Name, out uint nameId);
        if (newName)
        {
            if (!ValidateText(node.Name, RbfDiagnosticCodes.Name, ".Name"))
                return;
            if (names.Count == this.limits.MaximumNames)
            {
                Stop(RbfDiagnosticCodes.LimitNames, "RBF traversal stopped at the name budget.");
                return;
            }
            nameId = (uint)names.Count;
            names.Add(node.Name, nameId);
            payloadSize += 2L + node.Name.Length;
        }
        payloadSize += nameId < RbfEncoding.ShortNameLimit ? 2 : 6;
        if (Reserve(payloadSize))
            operations.Add(new(node, RbfEncoding.TypeOf(node), nameId, newName, attributeCount));
    }

    private bool ValidateText(string? text, string code, string suffix)
    {
        if (RbfEncoding.IsValidText(text))
            return true;
        AddDiagnostic(code, "Text must be non-null ASCII that fits a 16-bit byte count.", suffix);
        return false;
    }

    private bool Reserve(long bytes)
    {
        if (bytes > this.limits.MaximumBytes - size)
        {
            Stop(RbfDiagnosticCodes.LimitBytes, "RBF traversal stopped because the encoded output exceeds its byte budget.");
            return false;
        }
        size += (int)bytes;
        return true;
    }

    private void AddDiagnostic(string code, string message, string? suffix = null) => issues.Add(new(code, message, Path(suffix)));

    private void Stop(string code, string message, string? suffix = null)
    {
        AddDiagnostic(code, message, suffix);
        this.stopped = true;
    }

    private string Path(string? suffix)
    {
        var text = new StringBuilder();
        foreach (var (name, index) in path)
        {
            if (text.Length != 0)
                text.Append('.');
            text.Append(name);
            if (index >= 0)
                text.Append(CultureInfo.InvariantCulture, $"[{index}]");
        }
        return text.Append(suffix).ToString();
    }

    internal byte[] Write()
    {
        Report.ThrowIfInvalid();
        byte[] output = new byte[size];
        var writer = new BinarySpanWriter(output);
        writer.WriteBytes("RBF0"u8);
        foreach (Operation operation in operations)
        {
            if (operation.Node is null)
            { writer.WriteUInt16(RbfEncoding.Close); continue; }
            if (operation.Node is RbfRawNode raw)
            {
                writer.WriteUInt16(raw is RbfComment ? RbfEncoding.Comment : RbfEncoding.Bytes);
                writer.WriteUInt32((uint)raw.Data.Length);
                writer.WriteBytes(raw.Data);
                continue;
            }
            var node = (RbfNamedNode)operation.Node;
            if (operation.NameId < RbfEncoding.ShortNameLimit)
                writer.WriteUInt16((ushort)(((uint)operation.Type << 12) | operation.NameId));
            else
            {
                // Short records hold only a 12-bit name ID; larger IDs need the extended record.
                writer.WriteUInt16(RbfEncoding.Extended);
                writer.WriteUInt32(((uint)operation.Type << 28) | operation.NameId);
            }
            if (operation.NewName)
                RbfEncoding.WriteText(ref writer, output, node.Name);
            switch (node)
            {
                case RbfStructure structure:
                    writer.WriteUInt16(structure.Reserved1);
                    writer.WriteUInt16(structure.Reserved2);
                    writer.WriteUInt16((ushort)operation.AttributeCount);
                    break;
                case RbfInt32Value value:
                    writer.WriteInt32(value.Value);
                    break;
                case RbfFloatValue value:
                    writer.WriteSingle(value.Value);
                    break;
                case RbfVector3Value value:
                    writer.WriteVector3(value.Value);
                    break;
                case RbfStringValue value:
                    RbfEncoding.WriteText(ref writer, output, value.Value);
                    break;
            }
        }
        return output;
    }

    private readonly record struct Operation(RbfNode? Node, RbfRecordType Type = default,
        uint NameId = 0, bool NewName = false, int AttributeCount = 0);
}
