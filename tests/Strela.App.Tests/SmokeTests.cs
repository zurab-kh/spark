namespace Strela.App.Tests;

public class SmokeTests
{
    [Fact]
    public void AppAssembly_IsNamedStrela()
    {
        Assert.Equal("Strela", typeof(Strela.App.App).Assembly.GetName().Name);
    }
}
