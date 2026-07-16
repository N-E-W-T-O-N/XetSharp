using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using XetSharp;

namespace Huggingface;

/// <summary>Which side of a transfer a Xet CAS token is minted for.</summary>
public enum XetOperation
{
    /// <summary>Reading (download) — uses the Hub's <c>xet-read-token</c> route.</summary>
    Download,
    /// <summary>Writing (upload) — uses the Hub's <c>xet-write-token</c> route.</summary>
    Upload,
}

/// <summary>
/// Short-lived Xet CAS connection details minted by the Hub (the <c>xet-{read,write}-token</c>
/// response): the CAS server URL, a JWT, and its expiry. Fed into the XetSharp transfer engine.
/// </summary>
public sealed record XetConnectionInfo(
    [property: JsonPropertyName("casUrl")] string CasUrl,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("exp")] long Expiration);

/// <summary>
/// Minimal <c>huggingface_hub</c>-style client: talks to the HF Hub for repo info, Xet detection,
/// and CAS-token minting, and routes large-file transfers through the XetSharp engine (Xet) or a
/// classic HTTP <c>resolve</c> download. Your HF user token (<c>hf_…</c>) authenticates the Hub
/// calls; the Hub returns the short-lived CAS JWT that the transfer actually uses.
/// </summary>
public sealed class HubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Hub base URL (default <c>https://huggingface.co</c>).</summary>
    public string Endpoint { get; }

    /// <summary>HF user token (<c>hf_…</c>) used to authenticate Hub API calls; may be null for public repos.</summary>
    public string? Token { get; }

    public HubClient(string? token = null, string endpoint = "https://huggingface.co", HttpClient? httpClient = null)
    {
        Token = token;
        Endpoint = endpoint.TrimEnd('/');
        _http = httpClient ?? new HttpClient();
        _ownsHttp = httpClient is null;
    }

    /// <summary>
    /// Returns true if the repo/revision is Xet-backed (the Hub advertises <c>xetEnabled</c>), i.e.
    /// eligible for chunked/deduplicated transfer instead of classic LFS.
    /// </summary>
    public async Task<bool> IsXetEnabledAsync(
        string repoId, string repoType = "model", string revision = "main", CancellationToken ct = default)
    {
        string url = $"{Endpoint}/api/{repoType}s/{repoId}/revision/{Uri.EscapeDataString(revision)}";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("xetEnabled", out var x) && x.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Mints a short-lived Xet CAS token for the given operation via the Hub's
    /// <c>xet-{read|write}-token</c> route.
    /// </summary>
    public async Task<XetConnectionInfo> GetXetConnectionInfoAsync(
        string repoId, XetOperation operation, string repoType = "model", string revision = "main",
        CancellationToken ct = default)
    {
        string tokenType = operation == XetOperation.Upload ? "write" : "read";
        string url = $"{Endpoint}/api/{repoType}s/{repoId}/xet-{tokenType}-token/{Uri.EscapeDataString(revision)}";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var info = await JsonSerializer.DeserializeAsync<XetConnectionInfo>(stream, cancellationToken: ct);
        return info ?? throw new InvalidOperationException("Hub returned an empty xet token response.");
    }

    /// <summary>
    /// Downloads a file over classic HTTP via the Hub's <c>resolve</c> endpoint (works for any file,
    /// Xet-backed or not — Xet is an optimization, not a requirement for correctness).
    /// </summary>
    public async Task<string> DownloadFileAsync(
        string repoId, string filename, string destPath, string repoType = "model",
        string revision = "main", CancellationToken ct = default)
    {
        string prefix = repoType == "model" ? "" : $"{repoType}s/";
        string url = $"{Endpoint}/{prefix}{repoId}/resolve/{Uri.EscapeDataString(revision)}/{filename}";

        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(destPath))
        {
            await src.CopyToAsync(dst, ct);
        }
        return destPath;
    }

    /// <summary>
    /// Xet-accelerated download: reconstructs a file from its Merkle <paramref name="fileHash"/> via
    /// the XetSharp engine, using a Hub-minted CAS token that auto-refreshes on expiry.
    /// </summary>
    /// <remarks>
    /// Resolving a filename to its Xet <paramref name="fileHash"/> requires the file's Xet metadata
    /// from the Hub (a follow-up); once wired, callers pass the filename instead of the hash.
    /// </remarks>
    public async Task<string> DownloadFileXet(
        string repoId, string fileHash, string destPath, long fileSize = 0,
        string repoType = "model", string revision = "main")
    {
        var info = await GetXetConnectionInfoAsync(repoId, XetOperation.Download, repoType, revision);

        // TokenRefreshCallback is a SYNCHRONOUS delegate: the native engine calls it through a
        // function pointer and reads the (token, expiry) back immediately — it cannot await a Task,
        // so an `async` lambda won't compile here (CS4010). We block on the async Hub call with
        // GetAwaiter().GetResult() (which rethrows the real exception, unlike .Result/.Wait()).
        TokenRefreshCallback refresh = () =>
        {
            var fresh = GetXetConnectionInfoAsync(repoId, XetOperation.Download, repoType, revision)
                .GetAwaiter().GetResult();
            return (fresh.AccessToken, fresh.Expiration);
        };

        // DownloadFileRemote is a blocking native call; run it off the async caller's thread.
        var xet = new XetClient();
        return await Task.Run(() => xet.DownloadFileRemote(
            endpoint: info.CasUrl,
            token: info.AccessToken,
            hash: fileHash,
            destPath: destPath,
            tokenRefresh: refresh,
            repo: repoId,
            tokenExpiration: (ulong)info.Expiration,
            fileSize: (ulong)fileSize));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
