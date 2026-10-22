namespace CommonFury.Dlc;

// Only constants with the same meaning everywhere live here. Consuming libraries add their own through static
// extension members, e.g. `extension(DlcDataFileType) { public static DlcDataFileType Streaming => new("STREAMING_FILE"); }`,
// so callers keep writing DlcDataFileType.Streaming.

/// <summary>setup2.xml pack-type token; unrecognized values remain representable.</summary>
public readonly record struct DlcPackType(string Value)
{
    /// <summary>Returns the token, or an empty string for a default value.</summary>
    public override string ToString() => Value ?? "";
    /// <summary>Title-update content mounted at startup.</summary>
    public static DlcPackType TitleUpdate => new("EXTRACONTENT_TU_PACK");
    public static DlcPackType Level => new("EXTRACONTENT_LEVEL_PACK");
    public static DlcPackType Compat => new("EXTRACONTENT_COMPAT_PACK");
    /// <summary>Entitlement-controlled content.</summary>
    public static DlcPackType Paid => new("EXTRACONTENT_PAID_PACK");
}

/// <summary>Activation-group token; unrecognized values remain representable.</summary>
public readonly record struct DlcContentGroup(string Value)
{
    /// <inheritdoc cref="DlcPackType.ToString"/>
    public override string ToString() => Value ?? "";
    /// <summary>Early activation during core pack processing.</summary>
    public static DlcContentGroup EarlyOn => new("GROUP_EARLY_ON");
    public static DlcContentGroup Startup => new("GROUP_STARTUP");
}

/// <summary>content.xml data-file type token; unrecognized values remain representable.</summary>
public readonly record struct DlcDataFileType(string Value)
{
    /// <inheritdoc cref="DlcPackType.ToString"/>
    public override string ToString() => Value ?? "";
    /// <summary>Streaming archive.</summary>
    public static DlcDataFileType Rpf => new("RPF_FILE");
    /// <summary>Permanently loaded archetype package.</summary>
    public static DlcDataFileType PermanentItyp => new("PERMANENT_ITYP_FILE");
}

/// <summary>content.xml contents-category token (CONTENTS_*); consuming libraries provide the constants.</summary>
public readonly record struct DlcDataFileContents(string Value)
{
    /// <inheritdoc cref="DlcPackType.ToString"/>
    public override string ToString() => Value ?? "";
}
