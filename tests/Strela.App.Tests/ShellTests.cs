using Strela.App.Services;

namespace Strela.App.Tests;

public class ShellTests
{
    [Fact]
    public void SelectArguments_QuotesOnlyThePath()
    {
        // A name with spaces: quoted as a whole, "/select,..." is not a switch Explorer knows and it opens Documents.
        Assert.Equal(@"/select,""C:\Users\Zu\Downloads\IniBuilds Global Jet Manual v1.0.pdf""",
            Shell.SelectArguments(@"C:\Users\Zu\Downloads\IniBuilds Global Jet Manual v1.0.pdf"));
    }
}
