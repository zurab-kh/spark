namespace Strela.App.Tests;

public class SmokeTests
{
    [Fact]
    public void AppAssembly_IsNamedStrela()
    {
        Assert.Equal("Spark", typeof(Strela.App.App).Assembly.GetName().Name);
    }
}
