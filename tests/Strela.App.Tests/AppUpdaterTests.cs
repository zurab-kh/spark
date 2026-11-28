using System.Net;
using System.Net.Http;
using Strela.App.Services;

namespace Strela.App.Tests;

public class AppUpdaterTests
{
    [Fact]
    public void Source_IsADirectReleaseLink_NotTheRateLimitedApi()
    {
        Assert.StartsWith("https://github.com/", AppUpdater.ReleasesUrl);
        Assert.EndsWith("/releases/latest/download/", AppUpdater.ReleasesUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub временно ограничил проверки. Spark попробует снова позже")]
    [InlineData(HttpStatusCode.TooManyRequests, "GitHub временно ограничил проверки. Spark попробует снова позже")]
    [InlineData(HttpStatusCode.NotFound, "Новая версия сейчас выкладывается. Попробуйте через несколько минут")]
    [InlineData(HttpStatusCode.InternalServerError, "Не удалось проверить обновления. Подробности в журнале Spark")]
    public void Describe_HttpStatus(HttpStatusCode status, string expected) =>
        Assert.Equal(expected, AppUpdater.Describe(new HttpRequestException("Response status code does not indicate success", null, status)));

    [Fact]
    public void Describe_LooksInsideWrappedErrors_AndNeverShowsTheRawMessage()
    {
        var wrapped = new InvalidOperationException("Velopack failed", new HttpRequestException("rate limit exceeded", null, HttpStatusCode.Forbidden));
        Assert.Equal("GitHub временно ограничил проверки. Spark попробует снова позже", AppUpdater.Describe(wrapped));

        Assert.Equal("Нет связи с GitHub. Проверьте интернет", AppUpdater.Describe(new HttpRequestException("No such host is known")));
        Assert.DoesNotContain("Response status code", AppUpdater.Describe(new Exception("Response status code does not indicate success")));
    }
}
