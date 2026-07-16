using System.Text.Json;
using Xunit;
using XetSharp;

namespace XetSharp.Tests;

/// <summary>
/// Exercises the .NET ↔ Rust FFI end-to-end: every test crosses the P/Invoke boundary into the
/// native xet-core engine, so a green run proves the interop (marshalling, async bridge, error
/// propagation, memory ownership) works — not just the managed code.
/// </summary>
public sealed class InteropTests : IDisposable
{
    private readonly XetClient _client = new();
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "xetsharp-tests", Guid.NewGuid().ToString("N"));

    public InteropTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string CasDir => Path.Combine(_workDir, "cas");

    private string WriteSampleFile(string name, int blocks = 16, int seed = 7)
    {
        string path = Path.Combine(_workDir, name);
        byte[] block = new byte[64 * 1024];
        new Random(seed).NextBytes(block);
        using var fs = File.Create(path);
        for (int i = 0; i < blocks; i++)
            fs.Write(block);
        return path;
    }

    [Fact]
    public void UploadFile_ReturnsHashSizeAndMetrics()
    {
        string file = WriteSampleFile("model.bin");

        string json = _client.UploadFile(CasDir, file);

        using var doc = JsonDocument.Parse(json);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("hash").GetString()));
        Assert.Equal((ulong)new FileInfo(file).Length, doc.RootElement.GetProperty("file_size").GetUInt64());
        Assert.True(doc.RootElement.GetProperty("metrics").GetProperty("total_chunks").GetUInt64() > 0);
    }

    [Fact]
    public void UploadThenDownload_RoundTripsBytesExactly()
    {
        string file = WriteSampleFile("source.bin");
        byte[] original = File.ReadAllBytes(file);

        using var uploaded = JsonDocument.Parse(_client.UploadFile(CasDir, file));
        string hash = uploaded.RootElement.GetProperty("hash").GetString()!;
        ulong size = uploaded.RootElement.GetProperty("file_size").GetUInt64();

        string dest = Path.Combine(_workDir, "restored.bin");
        using var downloaded = JsonDocument.Parse(_client.DownloadFile(CasDir, hash, dest, size));

        Assert.Equal((ulong)original.Length, downloaded.RootElement.GetProperty("bytes_written").GetUInt64());
        Assert.Equal(original, File.ReadAllBytes(dest));
    }

    [Fact]
    public void UploadFile_SameContentTwice_DeduplicatesSecondUpload()
    {
        string file = WriteSampleFile("dupe.bin");

        _client.UploadFile(CasDir, file); // populate CAS
        using var second = JsonDocument.Parse(_client.UploadFile(CasDir, file));

        // Everything is already stored, so the second upload contributes no new chunks/bytes.
        JsonElement metrics = second.RootElement.GetProperty("metrics");
        Assert.Equal(0ul, metrics.GetProperty("new_chunks").GetUInt64());
        Assert.Equal(0ul, metrics.GetProperty("new_bytes").GetUInt64());
    }

    [Fact]
    public void UploadFile_MissingFile_ThrowsWithErrorCode()
    {
        var ex = Assert.Throws<XetException>(() =>
            _client.UploadFile(CasDir, Path.Combine(_workDir, "does-not-exist.bin")));

        Assert.Equal(1, ex.Code);
        Assert.Contains("file not found", ex.Message);
    }

    [Fact]
    public void DownloadFile_EmptyHash_ThrowsWithErrorCode()
    {
        var ex = Assert.Throws<XetException>(() =>
            _client.DownloadFile(CasDir, hash: "", destPath: Path.Combine(_workDir, "out.bin")));

        Assert.Equal(1, ex.Code);
    }

    [Fact]
    public void UploadFileRemote_EmptyToken_ThrowsGracefully()
    {
        // Proves the multi-arg remote FFI marshals and propagates errors without crashing,
        // via the fast input-validation path (no network).
        var ex = Assert.Throws<XetException>(() =>
            _client.UploadFileRemote(
                endpoint: "https://cas-server.huggingface.co",
                token: "",
                filePath: WriteSampleFile("remote.bin")));

        Assert.Equal(1, ex.Code);
        Assert.Contains("token", ex.Message);
    }

    [Fact]
    public void UploadFileRemote_InvokesTokenRefreshCallbackAcrossFfi()
    {
        // An already-expired token (epoch second 1) forces xet-core to call our refresher on the
        // first authenticated request. The callback throwing makes the op fail fast without a real
        // hub — what we assert is that the callback crossed the FFI boundary and was invoked.
        int refreshCalls = 0;
        TokenRefreshCallback refresh = () =>
        {
            Interlocked.Increment(ref refreshCalls);
            throw new InvalidOperationException("no real hub in test");
        };

        var ex = Assert.Throws<XetException>(() =>
            _client.UploadFileRemote(
                endpoint: "http://127.0.0.1:9",     // nothing listening -> fast failure
                token: "expired",
                filePath: WriteSampleFile("refresh.bin", blocks: 1),
                tokenRefresh: refresh,
                tokenExpiration: 1));

        Assert.True(refreshCalls >= 1, $"token refresh callback was not invoked (calls={refreshCalls})");
        Assert.NotEqual(0, ex.Code);
    }

    [Fact]
    public void InitLogging_WritesLogFile()
    {
        string logPath = Path.Combine(_workDir, "xet.log");
        _client.InitLogging(logPath);

        // Do some work so the engine emits tracing events.
        _client.UploadFile(CasDir, WriteSampleFile("logged.bin"));

        Assert.True(File.Exists(logPath));
        // tracing-appender flushes asynchronously; the file exists immediately, content follows.
    }
}
