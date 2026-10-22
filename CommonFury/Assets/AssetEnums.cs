namespace CommonFury.Assets;

/// <summary>A drawable's level of detail, from high to very low.</summary>
public enum DrawableLod
{
    High,
    Medium,
    Low,
    VeryLow
}

/// <summary>Bone flag bits 0–10. Higher bits vary by format; they are preserved but unnamed.</summary>
[Flags]
public enum BoneChannelFlags : uint
{
    None = 0,
    RotateX = 0x1,
    RotateY = 0x2,
    RotateZ = 0x4,
    /// <summary>Rotation is clamped by joint limits.</summary>
    LimitRotation = 0x8,
    TranslateX = 0x10,
    TranslateY = 0x20,
    TranslateZ = 0x40,
    /// <summary>Translation is clamped by joint limits.</summary>
    LimitTranslation = 0x80,
    ScaleX = 0x100,
    ScaleY = 0x200,
    ScaleZ = 0x400,
    Rotation = RotateX | RotateY | RotateZ,
    Translation = TranslateX | TranslateY | TranslateZ,
    Scale = ScaleX | ScaleY | ScaleZ,
    /// <summary>The default for authored bones.</summary>
    Animatable = Rotation | Translation,
    /// <summary>Bits named by this enum.</summary>
    SharedMask = 0x7FF
}

/// <summary>Map-level streaming markers. Unnamed bits are preserved.</summary>
[Flags]
public enum YmapFlags : uint
{
    None = 0,
    /// <summary>Loaded on request rather than by spatial streaming.</summary>
    ManualStreamOnly = 1,
    /// <summary>Other maps name this map as their parent.</summary>
    IsParent = 2
}

/// <summary>Authored content categories of a map, not recomputed from its contents. Unnamed bits are preserved.</summary>
[Flags]
public enum YmapContentFlags : uint
{
    /// <summary>No category is marked; the map is not necessarily empty.</summary>
    None = 0,
    /// <summary>Includes orphan high-detail placements.</summary>
    EntitiesHd = 0x1,
    EntitiesLod = 0x2,
    /// <summary>Container (SLOD) entities; does not imply container LOD records.</summary>
    EntitiesContainerLod = 0x4,
    /// <summary>MLO interior instances.</summary>
    Mlo = 0x8,
    /// <summary>Export marker; does not mean the block info structure is present.</summary>
    BlockInfo = 0x10,
    /// <summary>Box or model occlusion data.</summary>
    Occluder = 0x20,
    EntitiesUsingPhysics = 0x40,
    LodLights = 0x80,
    DistantLodLights = 0x100,
    EntitiesCritical = 0x200,
    /// <summary>Instanced prop or grass lists, separate from the entity list.</summary>
    InstanceList = 0x400,
    NoAutomaticStreaming = 0x800
}
