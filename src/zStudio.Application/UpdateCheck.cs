using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace zStudio.Automation;

/// <summary>
/// A version as zStudio's builds and release tags write it (SemVer 2.0: <c>MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]</c>,
/// a tag optionally with a leading <c>v</c>). Build metadata never counts; a prerelease sorts before its release.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    private const int MaximumLength = 64;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength) return false;
        ReadOnlySpan<char> span = text;
        if (span[0] is 'v' or 'V') span = span[1..];
        int plus = span.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(span[(plus + 1)..], numericRule: false)) return false;
            span = span[..plus];
        }
        string prerelease = "";
        int dash = span.IndexOf('-');
        if (dash >= 0)
        {
            if (!ValidIdentifiers(span[(dash + 1)..], numericRule: true)) return false;
            prerelease = span[(dash + 1)..].ToString();
            span = span[..dash];
        }
        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3) return false;
        if (!Number(span[parts[0]], out int major) || !Number(span[parts[1]], out int minor) || !Number(span[parts[2]], out int patch)) return false;
        version = new(major, minor, patch, prerelease);
        return true;
    }

    private static bool Number(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        // SemVer: no leading zeros; nine digits keep it an int.
        if (text.Length is 0 or > 9 || text.Length > 1 && text[0] == '0') return false;
        foreach (char c in text) if (!char.IsAsciiDigit(c)) return false;
        value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool ValidIdentifiers(ReadOnlySpan<char> text, bool numericRule)
    {
        if (text.IsEmpty) return false;
        foreach (var range in text.Split('.'))
        {
            var identifier = text[range];
            if (identifier.IsEmpty) return false;
            bool numeric = true;
            foreach (char c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
                numeric &= char.IsAsciiDigit(c);
            }
            if (numericRule && numeric && identifier.Length > 1 && identifier[0] == '0') return false;
        }
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        // A release is newer than any of its prereleases.
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0) return other.Prerelease.Length.CompareTo(Prerelease.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };
        string[] mine = Prerelease.Split('.'), theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            bool myNumber = mine[i].All(char.IsAsciiDigit), theirNumber = theirs[i].All(char.IsAsciiDigit);
            result = (myNumber, theirNumber) switch
            {
                // Numeric identifiers compare by value: without leading zeros, a longer one is larger.
                (true, true) => mine[i].Length != theirs[i].Length ? mine[i].Length.CompareTo(theirs[i].Length) : string.CompareOrdinal(mine[i], theirs[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(mine[i], theirs[i])
            };
            if (result != 0) return Math.Sign(result);
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>The latest published release, as GitHub reported it at <paramref name="CheckedUtc"/>.</summary>
public sealed record UpdateRelease(string Version, string Url, DateTimeOffset CheckedUtc);

/// <summary>A finished check: the latest release, or why it is not known (a failure is never thrown).</summary>
public sealed record UpdateCheckResult(UpdateRelease? Release, string? Failure);

/// <summary>
/// Asks GitHub for zStudio's latest published release (drafts and prereleases excluded). One bounded request per check:
/// a timeout, bounded response, no credentials; every failure becomes <see cref="UpdateCheckResult.Failure"/>.
/// A private test profile (<see cref="StudioProfile.IsPrivate"/>) never contacts GitHub: tests give their own handler.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/Vorta/zStudio/releases/latest";
    /// <summary>Every release page starts with this; a reported page that doesn't is replaced with <see cref="LatestReleasePage"/>.</summary>
    public const string ReleasePagePrefix = "https://github.com/Vorta/zStudio/releases/";
    public const string LatestReleasePage = ReleasePagePrefix + "latest";
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumUrlLength = 2048;
    private readonly HttpClient? client;

    /// <param name="currentVersion">This build's version (its informational version without the build metadata).</param>
    /// <param name="handler">The connection to GitHub; without one every check reports that it can't contact GitHub.</param>
    public UpdateChecker(string currentVersion, HttpMessageHandler? handler)
    {
        CurrentVersion = currentVersion;
        if (handler != null)
            client = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumResponseBytes };
    }

    /// <summary>The checker of this process: GitHub over the system's proxy settings, or none in a private test profile.</summary>
    public static UpdateChecker ForThisProcess(string currentVersion) => new(currentVersion, StudioProfile.IsPrivate ? null
        : new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = Timeout, UseCookies = false, Credentials = null });

    public string CurrentVersion { get; }
    /// <summary>Whether checks contact GitHub (false in a private test profile).</summary>
    public bool CanCheck => client != null;
    public const string PrivateProfileReason = "This process uses a private test profile, which never contacts GitHub.";

    /// <summary>Whether <paramref name="latest"/> is newer than this build; null when either version can't be compared.</summary>
    public bool? IsNewer(string latest) => ReleaseVersion.TryParse(latest, out var l) && ReleaseVersion.TryParse(CurrentVersion, out var c) ? l.CompareTo(c) > 0 : null;

    /// <summary>The release page to open: <paramref name="url"/> when it is a page of zStudio's releases on GitHub, otherwise null.</summary>
    public static string? ReleasePage(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaximumUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        // The normalized form, so "releases/../.." can't leave the releases.
        string page = uri.AbsoluteUri;
        return page.Length > ReleasePagePrefix.Length && page.Length <= MaximumUrlLength && page.StartsWith(ReleasePagePrefix, StringComparison.Ordinal) ? page : null;
    }

    /// <summary>One check; only <paramref name="token"/> cancels it (thrown), everything else is reported in the result.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        if (client == null) return new(null, PrivateProfileReason);
        if (!ReleaseVersion.TryParse(CurrentVersion, out _)) return new(null, $"This build's version ({Bounded(CurrentVersion, 64)}) can't be compared with releases.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            // GitHub refuses requests without a User-Agent.
            request.Headers.TryAddWithoutValidation("User-Agent", "zStudio/" + string.Concat(CurrentVersion.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "GitHub lists no published zStudio release.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
                return new(null, "GitHub's limit of update checks from this network is used up for the hour; try again later.");
            if (response.StatusCode != HttpStatusCode.OK)
                return new(null, $"GitHub answered {string.Join(' ', ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), Bounded(response.ReasonPhrase ?? "", 64)).TrimEnd()}.");
            byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return Parse(body);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(null, $"GitHub didn't answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or NotSupportedException or System.Security.Authentication.AuthenticationException)
        {
            return new(null, "Couldn't reach GitHub: " + Bounded(ex.Message, 240));
        }
    }

    private static UpdateCheckResult Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                return new(null, "GitHub's answer holds no release.");
            if (Flag(root, "draft") || Flag(root, "prerelease")) return new(null, "GitHub's latest release is not a published release.");
            string tagName = tag.GetString()!;
            if (!ReleaseVersion.TryParse(tagName, out var version)) return new(null, $"The latest release's tag ({Bounded(tagName, 64)}) is not a version.");
            string? page = root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String ? ReleasePage(url.GetString()) : null;
            return new(new(version.ToString(), page ?? LatestReleasePage, DateTimeOffset.UtcNow), null);
        }
        catch (JsonException) { return new(null, "GitHub's answer is not valid JSON."); }

        static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string Bounded(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
