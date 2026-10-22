namespace CommonFury.Collisions;

/// <summary>Primitive kind of a 16-byte collision polygon record. The tag position within the record varies by format.</summary>
public enum BoundPolygonKind : byte
{
    /// <summary>Three indexed vertices and three adjacency words.</summary>
    Triangle,
    /// <summary>Indexed center and radius.</summary>
    Sphere,
    /// <summary>Two indexed endpoints and radius.</summary>
    Capsule,
    /// <summary>Four indexed points defining a box.</summary>
    Box,
    /// <summary>Two indexed endpoints and radius.</summary>
    Cylinder
}
