using CommonFury.Dlc;
using Xunit;

namespace CommonFury.Tests;

// How a game library adds its own token constants while callers keep the DlcDataFileType.X spelling.
internal static class GameDlcTokens
{
    extension(DlcDataFileType)
    {
        public static DlcDataFileType Streaming => new("STREAMING_FILE");
    }

    extension(DlcContentGroup)
    {
        public static DlcContentGroup Map => new("GROUP_MAP");
    }
}

public sealed class DlcTests
{
    [Fact]
    public void TokensAreOpenOrdinalValues()
    {
        Assert.Equal("EXTRACONTENT_TU_PACK", DlcPackType.TitleUpdate.ToString());
        Assert.Equal(DlcPackType.Paid, new DlcPackType("EXTRACONTENT_PAID_PACK"));
        Assert.NotEqual(DlcPackType.Paid, new DlcPackType("extracontent_paid_pack"));
        Assert.Equal("CUSTOM_FILE", new DlcDataFileType("CUSTOM_FILE").ToString());
        Assert.Equal("", default(DlcContentGroup).ToString());
        Assert.Equal("", default(DlcDataFileContents).ToString());
        Assert.Equal("GROUP_EARLY_ON", DlcContentGroup.EarlyOn.Value);
        Assert.Equal("PERMANENT_ITYP_FILE", DlcDataFileType.PermanentItyp.Value);
    }

    [Fact]
    public void GamesExtendTokensWithStaticExtensionMembers()
    {
        Assert.Equal("STREAMING_FILE", DlcDataFileType.Streaming.Value);
        Assert.Equal(new DlcContentGroup("GROUP_MAP"), DlcContentGroup.Map);
    }
}
