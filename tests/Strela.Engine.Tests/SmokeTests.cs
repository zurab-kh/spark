namespace Strela.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void NullLog_DoesNotThrow()
    {
        NullLog.Instance.Error("x", new Exception());
    }
}
