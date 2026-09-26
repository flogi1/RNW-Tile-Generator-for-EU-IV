using System.Net;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v0.1.0", 0, 1, 0)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("1.4.2-beta.1", 1, 4, 2)]
    [InlineData("1.4.2+abc123", 1, 4, 2)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void TryParse_liest_gueltige_Versionen(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("-1.2.3")]
    public void TryParse_lehnt_ungueltige_Versionen_ab(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Fact]
    public void Vergleich_ordnet_numerisch_nicht_textuell()
    {
        Assert.True(new AppVersion(0, 10, 0) > new AppVersion(0, 9, 0));
        Assert.True(new AppVersion(1, 0, 0) > new AppVersion(0, 99, 99));
        Assert.True(new AppVersion(1, 2, 3) == new AppVersion(1, 2, 3));
    }
}

public class ReleaseParserTests
{
    [Fact]
    public void Parse_liest_Tag_und_Url()
    {
        var release = ReleaseParser.Parse("""{"tag_name":"v1.2.0","html_url":"https://github.com/o/r/releases/tag/v1.2.0","draft":false,"prerelease":false}""");
        Assert.NotNull(release);
        Assert.Equal(new AppVersion(1, 2, 0), release.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.2.0", release.HtmlUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v1.2.0","html_url":"u","prerelease":true}""")]
    [InlineData("""{"tag_name":"v1.2.0","html_url":"u","draft":true}""")]
    [InlineData("""{"html_url":"u"}""")]
    [InlineData("""{"tag_name":"nightly","html_url":"u"}""")]
    [InlineData("""{"tag_name":null,"html_url":"u"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("kein json")]
    public void Parse_liefert_null_bei_unbrauchbaren_Daten(string json)
    {
        Assert.Null(ReleaseParser.Parse(json));
    }
}

public class UpdateCheckerTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private const string ReleaseJson = """{"tag_name":"v0.2.0","html_url":"https://example/release"}""";

    [Fact]
    public async Task Neuere_Version_wird_als_Update_gemeldet()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson);
        var result = await new UpdateChecker(new HttpClient(handler), "o/r").CheckAsync(new AppVersion(0, 1, 0));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("https://example/release", result.Release!.HtmlUrl);
        Assert.Equal("https://api.github.com/repos/o/r/releases/latest", handler.LastRequest!.RequestUri!.ToString());
        Assert.NotEmpty(handler.LastRequest.Headers.UserAgent);
    }

    [Fact]
    public async Task Gleiche_oder_aeltere_Version_ist_aktuell()
    {
        var result = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, ReleaseJson)), "o/r").CheckAsync(new AppVersion(0, 2, 0));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Fehlendes_Release_ist_kein_Fehler()
    {
        var result = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.NotFound, "{}")), "o/r").CheckAsync(new AppVersion(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task Serverfehler_und_Netzausfall_werden_als_Failed_gemeldet()
    {
        var serverError = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError, "")), "o/r").CheckAsync(new AppVersion(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.Failed, serverError.Status);

        var offline = await new UpdateChecker(new HttpClient(new ThrowingHandler()), "o/r").CheckAsync(new AppVersion(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.Failed, offline.Status);
        Assert.Equal("offline", offline.Error);
    }

    [Fact]
    public async Task Unlesbare_Antwort_ist_Failed()
    {
        var result = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, "<html>")), "o/r").CheckAsync(new AppVersion(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }
}

public class UpdateScheduleTests
{
    [Fact]
    public void Faellig_ohne_letzte_Pruefung_nach_24_Stunden_und_bei_Zeitsprung()
    {
        var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateSchedule.IsDue(null, now));
        Assert.True(UpdateSchedule.IsDue(now.AddHours(-24), now));
        Assert.False(UpdateSchedule.IsDue(now.AddHours(-23), now));
        Assert.True(UpdateSchedule.IsDue(now.AddHours(2), now));
    }
}
