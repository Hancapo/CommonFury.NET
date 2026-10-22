namespace CommonFury.Vertices;

/// <summary>Identifies a vertex attribute by its RAGE declaration slot (0–51). Unnamed slots keep their numeric value.</summary>
public enum VertexSemantic : byte
{
    Position = 0,
    Normal = 4,
    /// <summary>First tangent, usually with handedness in W.</summary>
    Tangent = 8,
    Tangent1 = 9,
    Binormal = 12,
    BlendWeights = 16,
    BlendWeights1 = 17,
    BlendIndices = 20,
    BlendIndices1 = 21,
    Color0 = 24,
    Color1 = 25,
    Color2 = 26,
    TexCoord0 = 28,
    TexCoord1 = 29,
    TexCoord2 = 30,
    TexCoord3 = 31,
    TexCoord4 = 32,
    TexCoord5 = 33,
    TexCoord6 = 34,
    TexCoord7 = 35
}
