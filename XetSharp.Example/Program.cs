using System;
using System.IO;
using System.Text.Json;
using XetSharp;

// XetSharp demo / entry point.
//
// Enter a command to drive the native engine directly:
//   dotnet run --project XetTest -- upload   <casDir> <file>
//   dotnet run --project XetTest -- download <casDir> <hash> <dest> [size]
//   dotnet run --project XetTest -- log      <path>
// With no command it runs a self-contained upload -> download round-trip demo.

var client = new XetClient();

try
{
    return args.Length == 0 ? RunDemo(client) : RunCommand(client, args);
}
catch (XetException ex)
{
    Console.Error.WriteLine($"xet error (code {ex.Code}): {ex.Message}");
    return ex.Code;
}

static int RunCommand(XetClient client, string[] args)
{
    switch (args[0].ToLowerInvariant())
    {
        case "upload" when args.Length == 3:
            Console.WriteLine(client.UploadFile(args[1], args[2]));
            return 0;

        case "download" when args.Length is 4 or 5:
            ulong size = args.Length == 5 ? ulong.Parse(args[4]) : 0;
            Console.WriteLine(client.DownloadFile(args[1], args[2], args[3], size));
            return 0;

        case "log" when args.Length == 2:
            client.InitLogging(args[1]);
            Console.WriteLine($"logging to {Path.GetFullPath(args[1])}");
            return 0;

        default:
            Console.Error.WriteLine(
                "usage:\n" +
                "  upload   <casDir> <file>\n" +
                "  download <casDir> <hash> <dest> [size]\n" +
                "  log      <path>");
            return 2;
    }
}

static int RunDemo(XetClient client)
{
    // 0. Logging -> <appname>.log in the working directory.
    string appName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "xetsharp";
    string logPath = Path.GetFullPath(appName + ".log");
    client.InitLogging(logPath);
    Console.WriteLine($"Logging to: {logPath}");

    string workDir = Path.Combine(Path.GetTempPath(), "xetsharp-demo");
    Directory.CreateDirectory(workDir);
    string casDir = Path.Combine(workDir, "cas");
    string sampleFile = Path.Combine(workDir, "sample.bin");

    // ~4 MiB of repetitive data so deduplication has something to find.
    byte[] block = new byte[64 * 1024];
    new Random(42).NextBytes(block);
    using (var fs = File.Create(sampleFile))
        for (int i = 0; i < 64; i++)
            fs.Write(block, 0, block.Length);
    Console.WriteLine($"Sample file: {sampleFile} ({new FileInfo(sampleFile).Length:N0} bytes)");

    // 1. Local upload -> download round-trip.
    Console.WriteLine($"\n[local] CAS dir: {casDir}");
    string uploadJson = client.UploadFile(casDir, sampleFile);
    Console.WriteLine("[local] upload: " + uploadJson);

    using var doc = JsonDocument.Parse(uploadJson);
    string hash = doc.RootElement.GetProperty("hash").GetString()!;
    ulong size = doc.RootElement.GetProperty("file_size").GetUInt64();

    string restored = Path.Combine(workDir, "restored.bin");
    File.Delete(restored);
    Console.WriteLine("[local] download: " + client.DownloadFile(casDir, hash, restored, size));

    bool identical = File.ReadAllBytes(sampleFile).AsSpan().SequenceEqual(File.ReadAllBytes(restored));
    Console.WriteLine($"[local] round-trip bytes identical: {identical}");

    // 2. Remote upload (real if env vars set; otherwise exercise the FFI error path).
    string? endpoint = Environment.GetEnvironmentVariable("XET_ENDPOINT");
    string? token = Environment.GetEnvironmentVariable("XET_TOKEN");
    string? repo = Environment.GetEnvironmentVariable("XET_REPO");

    if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(token))
    {
        Console.WriteLine($"\n[remote] uploading to {endpoint}");
        Console.WriteLine("[remote] result: " + client.UploadFileRemote(endpoint, token, sampleFile, repo));
    }
    else
    {
        Console.WriteLine("\n[remote] XET_ENDPOINT / XET_TOKEN not set — exercising FFI error path.");
        try
        {
            client.UploadFileRemote(endpoint: "https://cas-server.huggingface.co", token: "", filePath: sampleFile);
            Console.WriteLine("[remote] unexpectedly succeeded");
        }
        catch (XetException ex)
        {
            Console.WriteLine($"[remote] handled failure (code {ex.Code}): {ex.Message}");
        }
    }

    return identical ? 0 : 1;
}
