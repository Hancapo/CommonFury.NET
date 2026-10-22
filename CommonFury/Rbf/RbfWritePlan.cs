using CommonFury.Binary;

namespace CommonFury.Rbf;

internal sealed class RbfWritePlan
{
    private readonly RbfLimits limits;
    private readonly List<Diagnostic> issues = [];
    private readonly List<Operation> operations = [];
    private readonly Dictionary<string, uint> names = new(StringComparer.Ordinal);
    private readonly HashSet<RbfStructure> active = new(ReferenceEqualityComparer.Instance);
    private int nodes;
    private int size = 4;
    private bool stopped;

    internal ValidationReport Report { get; }

    internal RbfWritePlan(RbfDocument document, RbfLimits limits)
    {
        this.limits = limits;
        if (size > limits.MaximumBytes)
            Stop(RbfDiagnosticCodes.LimitBytes, "RBF header exceeds the output byte budget.", "Root");
        for (int i = 0; i < document.LeadingComments.Count && !this.stopped; i++)
            Visit(document.LeadingComments[i], $"LeadingComments[{i}]", 0, attribute: false);
        if (!this.stopped)
            Visit(document.Root, "Root", 1, attribute: false);
        for (int i = 0; i < document.TrailingComments.Count && !this.stopped; i++)
            Visit(document.TrailingComments[i], $"TrailingComments[{i}]", 0, attribute: false);
        Report = new ValidationReport(issues);
    }

    private void Visit(RbfNode? node, string path, int depth, bool attribute)
    {
        if (nodes == this.limits.MaximumNodes)
        {
            Stop(RbfDiagnosticCodes.LimitNodes, "RBF traversal stopped at the record budget.", path);
            return;
        }
        nodes++;
        if (node is null)
        { AddDiagnostic(RbfDiagnosticCodes.NodeNull, "RBF record lists cannot contain null.", path); return; }
        if (attribute && node is not RbfValue and not RbfComment)
        {
            AddDiagnostic(RbfDiagnosticCodes.AttributeType, "An RBF attribute must be an integer, Boolean, float or string record; comments may separate attributes.", path);
            return;
        }
        if (node is RbfStructure structure)
        {
            if (depth > this.limits.MaximumDepth)
            {
                AddDiagnostic(RbfDiagnosticCodes.LimitDepth, "Traversal of this branch stopped at the container depth budget.", path);
                return;
            }
            if (!active.Add(structure))
            {
                AddDiagnostic(RbfDiagnosticCodes.StructureCycle, "RBF containers form a tree on disk; this reference creates a cycle.", path);
                return;
            }
            try
            { VisitStructure(structure, path, depth); }
            finally { active.Remove(structure); }
            return;
        }
        if (node is RbfRawNode raw)
        {
            if (Reserve(6L + raw.Data.Length, path))
                operations.Add(new(node));
            return;
        }
        var value = (RbfValue)node;
        if (attribute && value is RbfVector3Value)
            AddDiagnostic(RbfDiagnosticCodes.AttributeType, "The identified RAGE reader does not support vector records as attributes.", path);
        if (!attribute && value is RbfStringValue)
            AddDiagnostic(RbfDiagnosticCodes.StringElement, "String leaf records are readable, but RAGE export requires a string attribute or an explicit data container.", path);
        if (value is RbfStringValue text)
            RbfEncoding.ValidateAscii(text.Value, issues, RbfDiagnosticCodes.Text, path + ".Value");
        int payloadSize = value switch
        {
            RbfInt32Value or RbfFloatValue => 4,
            RbfVector3Value => 12,
            RbfStringValue textValue => textValue.Value.Length,
            _ => 0
        };
        AddNamed(value, path, (long)payloadSize + (value is RbfStringValue ? 2 : 0));
    }

    private void VisitStructure(RbfStructure structure, string path, int depth)
    {
        if (structure.Attributes.Count > this.limits.MaximumNodes - nodes)
        {
            Stop(RbfDiagnosticCodes.LimitNodes, "The attribute records exceed the remaining traversal budget.", path + ".Attributes");
            return;
        }
        int attributeCount = 0;
        foreach (RbfNode? attribute in structure.Attributes)
            if (attribute is not RbfComment)
                attributeCount++;
        if (attributeCount > ushort.MaxValue)
            AddDiagnostic(RbfDiagnosticCodes.AttributeCount, "The number of RBF attributes must fit an unsigned 16-bit count.", path + ".Attributes");
        AddNamed(structure, path, 6, attributeCount);
        for (int i = 0; i < structure.Attributes.Count && !this.stopped; i++)
            Visit(structure.Attributes[i], $"{path}.Attributes[{i}]", depth, attribute: true);
        bool hasData = false, hasElements = false;
        for (int i = 0; i < structure.Children.Count && !this.stopped; i++)
        {
            RbfNode? child = structure.Children[i];
            hasData |= child is RbfBytes;
            hasElements |= child is RbfNamedNode;
            Visit(child, $"{path}.Children[{i}]", depth + 1, attribute: false);
        }
        if (hasData && hasElements)
            AddDiagnostic(RbfDiagnosticCodes.ContentMixed, "The identified RAGE reader requires data content and child elements to be in separate containers.", path + ".Children");
        if (!this.stopped && Reserve(2, path))
            operations.Add(new(null));
    }

    private void AddNamed(RbfNamedNode node, string path, long payloadSize, int attributeCount = 0)
    {
        int issueCount = issues.Count;
        RbfEncoding.ValidateAscii(node.Name, issues, RbfDiagnosticCodes.Name, path + ".Name");
        if (issues.Count != issueCount)
            return;
        bool newName = !names.TryGetValue(node.Name, out uint nameId);
        if (newName)
        {
            if (names.Count == this.limits.MaximumNames)
            {
                Stop(RbfDiagnosticCodes.LimitNames, "RBF traversal stopped at the name budget.", path);
                return;
            }
            nameId = (uint)names.Count;
            names.Add(node.Name, nameId);
            payloadSize += 2L + node.Name.Length;
        }
        payloadSize += nameId < RbfEncoding.ShortNameLimit ? 2 : 6;
        if (Reserve(payloadSize, path))
            operations.Add(new(node, RbfEncoding.TypeOf(node), nameId, newName, attributeCount));
    }

    private bool Reserve(long bytes, string path)
    {
        if (bytes > this.limits.MaximumBytes - size)
        {
            Stop(RbfDiagnosticCodes.LimitBytes, "RBF traversal stopped because the encoded output exceeds its byte budget.", path);
            return false;
        }
        size += (int)bytes;
        return true;
    }

    private void AddDiagnostic(string code, string message, string path) => issues.Add(new(code, message, path));

    private void Stop(string code, string message, string path)
    {
        AddDiagnostic(code, message, path);
        this.stopped = true;
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
                RbfEncoding.WriteText(ref writer, node.Name);
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
                    RbfEncoding.WriteText(ref writer, value.Value);
                    break;
            }
        }
        return output;
    }

    private readonly record struct Operation(RbfNode? Node, RbfRecordType Type = default,
        uint NameId = 0, bool NewName = false, int AttributeCount = 0);
}
