using System.Net;
using System.Net.Http;
using System.Text.Json;
using zStudio.Automation;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class UpdateCheckTests
{
    // Must-have: the comparison decides whether the user is told to update, the page rule what the click may open, and a
    // private profile (every test run) must never contact GitHub.
    [Fact]
    public async Task ReleasesCompareAsSemVerAndOnlyZStudioReleasePagesOpen()
    {
        string[] ordered = ["0.9.6", "0.9.7-alpha", "0.9.7-alpha.1", "0.9.7-alpha.beta", "0.9.7-beta", "0.9.7-beta.2", "0.9.7-beta.11", "0.9.7-rc.1", "0.9.7", "0.9.10", "0.10.0", "1.0.0"];
        for (int i = 0; i < ordered.Length; i++)
            for (int j = 0; j < ordered.Length; j++)
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(Version(ordered[i]).CompareTo(Version(ordered[j]))));
        Assert.Equal(0, Version("v0.9.7").CompareTo(Version("0.9.7+7282c49")));
        foreach (string invalid in new[] { "", "0.9", "0.9.7.1", "01.9.7", "0.9.7-", "0.9.7-01", "0.9.7+", "x0.9.7", "0.9.7 ", "0.9.-7" })
            Assert.False(ReleaseVersion.TryParse(invalid, out _), invalid);
        Assert.True(new UpdateChecker("0.9.7-beta.1", null).IsNewer("v0.9.7"));
        Assert.False(new UpdateChecker("0.9.7", null).IsNewer("v0.9.7"));
        Assert.False(new UpdateChecker("0.9.7", null).IsNewer("v0.9.6"));
        Assert.Null(new UpdateChecker("0.9.7", null).IsNewer("latest"));

        const string page = "https://github.com/Vorta/zStudio/releases/tag/v0.9.8";
        Assert.Equal(page, UpdateChecker.ReleasePage(page));
        foreach (string refused in new[] { "http://github.com/Vorta/zStudio/releases/tag/v0.9.8", "https://github.com/Vorta/zStudio/releases/../../other/releases/tag/v1",
            "https://github.com.example.com/Vorta/zStudio/releases/tag/v1", "https://user@github.com/Vorta/zStudio/releases/tag/v1", "https://github.com:444/Vorta/zStudio/releases/tag/v1",
            UpdateChecker.ReleasePagePrefix, "file:///C:/Windows/notepad.exe", "javascript:alert(1)" })
            Assert.Null(UpdateChecker.ReleasePage(refused));
        // A release whose page is elsewhere still counts; its click opens zStudio's latest release instead.
        var github = new FakeGitHub { Next = () => FakeGitHub.Release("v0.9.8", "https://example.com/Vorta/zStudio/releases/tag/v0.9.8") };
        var result = await new UpdateChecker("0.9.7", github).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("0.9.8", UpdateChecker.LatestReleasePage), (result.Release!.Version, result.Release.Url));

        Assert.True(StudioProfile.IsPrivate);
        Assert.False(UpdateChecker.ForThisProcess("0.9.7").CanCheck);
        Assert.Equal(UpdateChecker.PrivateProfileReason, (await UpdateChecker.ForThisProcess("0.9.7").CheckAsync(TestContext.Current.CancellationToken)).Failure);
    }

    private static ReleaseVersion Version(string text) => ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);
}

/// <summary>GitHub's latest-release endpoint for tests: answers with <see cref="Next"/> and records each request's headers and thread.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Func<HttpResponseMessage> Next { get; set; } = () => new(HttpStatusCode.NotFound);
    public sealed record Request(Uri? Uri, string UserAgent, string Accept, string ApiVersion, int Thread);
    public List<Request> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
        Requests.Add(new(request.RequestUri, Header("User-Agent"), Header("Accept"), Header("X-GitHub-Api-Version"), Environment.CurrentManagedThreadId));
        return Task.FromResult(Next());
    }
    public static HttpResponseMessage Release(string tag, string url) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = tag, html_url = url, draft = false, prerelease = false, body = "Notes" })) };
    public static HttpResponseMessage RateLimited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        return response;
    }
}
