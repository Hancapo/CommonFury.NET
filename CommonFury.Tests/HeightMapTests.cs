using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Binary;
using CommonFury.HeightMaps;
using Xunit;

namespace CommonFury.Tests;

public sealed class HeightMapTests
{
    // GTA V runtime limits, so game-level validation is exercised.
    private static readonly HeightMapExportLimits GtaLimits = new()
    {
        MinimumDimension = 10, MaximumDimension = 1000, MaximumBoundsMagnitude = 16000,
        RequiredVersion = 1, RequireByteCells = true, RejectInvertedCells = true
    };

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "ReferenceData", "heightmaps");
    private static IEnumerable<TheoryDataRow<string>> Files(string directory) =>
        Directory.GetFiles(Path.Combine(Fixtures, directory), "*.hmap").Select(Path.GetFileName).Order()
            .Select(name => new TheoryDataRow<string>(name!));
    public static IEnumerable<TheoryDataRow<string>> ValidFiles => Files("valid");
    public static IEnumerable<TheoryDataRow<string>> InvalidFiles => Files("invalid");

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void RoundtripMatchesReference(string name)
    {
        string path = Path.Combine(Fixtures, "valid", name);
        byte[] input = File.ReadAllBytes(path);
        switch (input[5])
        {
            case 1:
                Verify<byte>(path, input);
                break;
            case 2:
                Verify<ushort>(path, input);
                break;
            case 4:
                Verify<float>(path, input);
                break;
            default:
                throw new InvalidDataException("Unknown fixture cell size.");
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void RejectsMalformedInput(string name)
    {
        byte[] input = File.ReadAllBytes(Path.Combine(Fixtures, "invalid", name));
        // Inverted cells are representable: they are rejected only on request.
        bool reject = name == "inverted_cell.hmap";
        Exception? error = Record.Exception(() =>
        {
            if (input.Length > 5 && input[5] == 4)
                HeightMapCodec.Read<float>(input, rejectInvertedCells: reject);
            else if (input.Length > 5 && input[5] == 2)
                HeightMapCodec.Read<ushort>(input, rejectInvertedCells: reject);
            else
                HeightMapCodec.Read<byte>(input, rejectInvertedCells: reject);
        });
        Assert.True(error is InvalidDataException or ValidationException, $"Expected format/validation failure, got {error?.GetType().Name ?? "no exception"}.");
    }

    [Fact]
    public void InvertedCellFixtureIsPreservedByDefault()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(Fixtures, "invalid", "inverted_cell.hmap"));
        var report = input[5] switch
        {
            1 => HeightMapCodec.Read<byte>(input).Validate(),
            2 => HeightMapCodec.Read<ushort>(input).Validate(),
            _ => HeightMapCodec.Read<float>(input).Validate()
        };
        Assert.True(report.IsValid);
        Assert.Contains(report.Diagnostics, d => d.Code == HeightMapDiagnosticCodes.CellInverted && d.Severity == DiagnosticSeverity.Warning);
    }

    private static void Verify<T>(string path, byte[] input) where T : unmanaged, INumber<T>
    {
        HeightMap<T> map = HeightMapCodec.Read<T>(input);
        // Canonical form: trimmed rows for row-compressed sources, dense otherwise.
        HeightMapLayout canonicalLayout = map.Flags.HasFlag(HeightMapFlags.RleData) ? HeightMapLayout.TrimmedRows : HeightMapLayout.Dense;
        byte[] canonical = HeightMapCodec.Write(map, layout: canonicalLayout);
        Assert.Equal<byte>(File.ReadAllBytes(path + ".canonical"), canonical);
        byte[] dense = HeightMapCodec.Write(map, layout: HeightMapLayout.Dense, byteOrder: ByteOrder.Big);
        Assert.Equal<byte>(File.ReadAllBytes(path + ".dense"), dense);
        // A canonical source is reproduced byte for byte with the recorded layout and byte order.
        if (map.Layout != HeightMapLayout.TrimmedRows || input.AsSpan().SequenceEqual(canonical))
            Assert.Equal<byte>(input, HeightMapCodec.Write(map));
    }

    [Fact]
    public void RejectingInvertedCellsKeepsUnknownFlags()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(Fixtures, "valid", "UINT8_BIG_False_False_31x17.hmap"));
        Assert.True(input.AsSpan(0, 4).SequenceEqual("HMAP"u8));
        input[11] |= 0x40; // big-endian flags at offset 8: set an undocumented bit
        Assert.Equal((HeightMapFlags)0x40, HeightMapCodec.Read<byte>(input).Flags & (HeightMapFlags)0x40);
        Assert.Equal((HeightMapFlags)0x40, HeightMapCodec.Read<byte>(input, rejectInvertedCells: true).Flags & (HeightMapFlags)0x40);
    }

    [Fact]
    public void ModelInvariants()
    {
        var map = new HeightMap<byte>(10, 10, new(new(0, 0, 0), new(10, 10, 255)));
        Assert.True(map.Validate(GtaLimits).IsValid, "Valid UINT8 model rejected.");
        map.MinimumCells[5] = 4;
        Assert.True(map.Validate(GtaLimits).Diagnostics.Any(x => x.Code == "heightmap.cell.inverted" && x.Path == "cells[5]"), "Missing cell diagnostic.");
        Assert.Throws<ValidationException>(() => HeightMapCodec.Write(map, GtaLimits));
        Assert.True(map.Validate().IsValid, "Structural validation must only warn about inverted cells.");
        map.MaximumCells[5] = 5;
        map.Flags = (HeightMapFlags)0x80;
        Assert.True(map.Validate().IsValid && !map.Validate(GtaLimits).IsValid, "Unknown flag policy mismatch.");
        map.Flags = HeightMapFlags.RleData;
        Assert.Throws<ArgumentException>(() => map.WaterCells = new bool[99]);
        map.WaterCells = new bool[100];
        map.WaterCells[99] = true;
        byte[] encoded = HeightMapCodec.Write(map, GtaLimits);
        Assert.True(HeightMapCodec.Read<byte>(encoded).WaterCells![99], "Last mask bit lost.");
        Assert.True(map.Flags == HeightMapFlags.RleData, "Writer mutated input flags.");
        map.Version = 2;
        Assert.True(!map.Validate(GtaLimits).IsValid && map.Validate().IsValid, "Version policy mismatch.");
        var floatMap = new HeightMap<float>(1, 1, map.Bounds, [0], [1]);
        Assert.True(floatMap.Validate().IsValid && !floatMap.Validate(GtaLimits).IsValid, "Tool format policy mismatch.");
        floatMap.MinimumCells[0] = float.NaN;
        Assert.Throws<ValidationException>(() => HeightMapCodec.Write(floatMap));
        // Cover vector boundaries and tails; require every invalid cell's diagnostic.
        foreach (int length in new[] { 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 257 })
        {
            var cells = new HeightMap<ushort>(length, 1, map.Bounds);
            for (int i = 0; i < length; i++)
            {
                cells.MinimumCells[i] = ushort.MaxValue;
                var report = cells.Validate();
                Assert.True(report.Diagnostics.Count == 1 && report.Diagnostics[0].Path == $"cells[{i}]", "Vector validation missed a cell.");
                cells.MinimumCells[i] = 0;
            }
            Array.Fill(cells.MinimumCells, ushort.MaxValue);
            Assert.True(cells.Validate().Diagnostics.Count == length, "Validation lost diagnostics after the first error.");
        }
    }

    [Fact]
    public void WritesHeaderRowTableAndCellsInEveryLayout()
    {
        var map = new HeightMap<byte>(4, 2, new(new(-10, -20, -57), new(30, 0, 729))) { ByteOrder = ByteOrder.Little };
        map.SetHeight(1, 0, 0, 100);
        map.SetHeight(2, 0, 0, 0);
        map.SetHeight(3, 1, 729, 729);
        Assert.Equal((18, 51), (map.MinimumCells[1], map.MaximumCells[1]));
        Assert.Equal((255, 255), (map.MinimumCells[7], map.MaximumCells[7]));
        Assert.Equal(new Vector2(10, 10), map.CellSize);
        Assert.True(map.TryGetCell(new Vector2(29.9f, -0.1f), out int column, out int row));
        Assert.Equal((3, 1), (column, row));
        Assert.False(map.TryGetCell(new Vector2(30, -5), out _, out _));
        Assert.Null(map.GetHeightRange(0, 0));
        Assert.Equal(729, map.GetHeightRangeAt(new Vector2(25, -5))!.Value.Minimum, 0.001f);

        byte[] trimmed = HeightMapCodec.Write(map);
        Assert.Equal("PAMH"u8.ToArray(), trimmed[..4]);
        Assert.Equal(new byte[] { 1, 1, 0, 0 }, trimmed[4..8]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(trimmed.AsSpan(8)));
        Assert.Equal((4, 2), (BinaryPrimitives.ReadUInt16LittleEndian(trimmed.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(trimmed.AsSpan(14))));
        Assert.Equal(-57f, BinaryPrimitives.ReadSingleLittleEndian(trimmed.AsSpan(24)));
        // Row 0 stores columns 1..2 at compact 0, row 1 stores column 3 at compact 2: offset = compact - start.
        Assert.Equal(16 + 3 * 2, (int)BinaryPrimitives.ReadUInt32LittleEndian(trimmed.AsSpan(40)));
        Assert.Equal(new byte[] { 1, 0, 2, 0, 255, 255, 255, 255, 3, 0, 1, 0, 255, 255, 255, 255 }, trimmed[44..60]);
        Assert.Equal(new byte[] { 51, 19, 255, 18, 18, 255 }, trimmed[60..]);

        foreach (var layout in Enum.GetValues<HeightMapLayout>())
            foreach (var order in Enum.GetValues<ByteOrder>())
            {
                map.Layout = layout;
                map.ByteOrder = order;
                byte[] bytes = HeightMapCodec.Write(map);
                var read = HeightMapCodec.Read<byte>(bytes);
                Assert.Equal(map.MinimumCells, read.MinimumCells);
                Assert.Equal(map.MaximumCells, read.MaximumCells);
                Assert.Equal((layout, order), (read.Layout, read.ByteOrder));
                Assert.Equal(bytes, HeightMapCodec.Write(read));
            }
        map.ByteOrder = ByteOrder.Big;
        Assert.Equal("HMAP"u8.ToArray(), HeightMapCodec.Write(map)[..4]);
    }

    [Fact]
    public void PreservesInvertedCellsReservedBytesAndRejectsMalformedRows()
    {
        var map = new HeightMap<byte>(2, 2, new(new(0, 0, 0), new(2, 2, 255))) { ByteOrder = ByteOrder.Little };
        map.MinimumCells[3] = 9;
        map.MaximumCells[3] = 4;
        Assert.Throws<ArgumentException>(() => map.SetHeight(0, 0, 5, 4));
        byte[] bytes = HeightMapCodec.Write(map);
        var read = HeightMapCodec.Read<byte>(bytes);
        Assert.Equal((9, 4), (read.MinimumCells[3], read.MaximumCells[3]));
        var diagnostic = Assert.Single(read.Validate().Diagnostics);
        Assert.Equal((HeightMapDiagnosticCodes.CellInverted, DiagnosticSeverity.Warning), (diagnostic.Code, diagnostic.Severity));
        Assert.True(read.Validate().IsValid);
        Assert.Throws<ValidationException>(() => HeightMapCodec.Read<byte>(bytes, rejectInvertedCells: true));

        byte[] reserved = (byte[])bytes.Clone();
        reserved[6] = 1;
        Assert.Equal(reserved, HeightMapCodec.Write(HeightMapCodec.Read<byte>(reserved)));
        byte[] wide = (byte[])bytes.Clone();
        wide[5] = 2;
        Assert.Throws<InvalidDataException>(() => HeightMapCodec.Read<byte>(wide));
        Assert.Throws<InvalidDataException>(() => HeightMapCodec.Read<byte>(bytes.AsSpan(0, bytes.Length - 1)));
        byte[] row = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(46), 3);
        Assert.Throws<InvalidDataException>(() => HeightMapCodec.Read<byte>(row));
    }

    [Fact]
    public void SaveRequiresOverwriteForExistingFiles()
    {
        var map = new HeightMap<byte>(2, 2, new(new(0, 0, 0), new(2, 2, 255)));
        map.SetHeight(1, 1, 10, 20);
        string directory = Directory.CreateTempSubdirectory("commonfury-heightmap-").FullName;
        try
        {
            string path = Path.Combine(directory, "heightmap.dat");
            HeightMapCodec.Save(map, path);
            Assert.Equal(HeightMapCodec.Write(map), File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => HeightMapCodec.Save(map, path));
            HeightMapCodec.Save(map, path, overwrite: true);
        }
        finally { Directory.Delete(directory, true); }
    }
}
