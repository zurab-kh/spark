using Strela.App.Services;

namespace Strela.App.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(900L, "900 Б")]
    [InlineData(6120328397L, "5.7 ГБ")]
    [InlineData(125829120L, "120 МБ")]
    public void Bytes(long? b, string s) => Assert.Equal(s, Format.Bytes(b));

    [Fact]
    public void Speed_Formats()
    {
        Assert.Equal("42 МБ/с", Format.Speed(44040192));
        Assert.Equal("", Format.Speed(0));
    }

    [Theory]
    [InlineData(45, "0:45")]
    [InlineData(3723, "1:02:03")]
    public void Eta(int s, string t) => Assert.Equal(t, Format.Eta(TimeSpan.FromSeconds(s)));

    [Fact]
    public void Eta_Null_Empty() => Assert.Equal("", Format.Eta(null));
}
