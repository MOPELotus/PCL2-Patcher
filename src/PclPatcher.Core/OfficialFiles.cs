using System.IO.Compression;

namespace PclPatcher.Core;

public record OfficialFile(string Version, string Commit, string Sha256)
{
    public string FileName => $"PCL-{Version}.exe";
    public string Url => $"https://raw.githubusercontent.com/Meloong-Git/PCL/{Commit}/{Uri.EscapeDataString("最新正式版.zip")}";
}

public static class OfficialFiles
{
    public static IReadOnlyList<OfficialFile> Releases { get; } = Array.AsReadOnly<OfficialFile>(
    [
        new("2.99.0.0", "6b9a50df911c9d68b69b24c979efba477fb14b67", "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386"),
        new("2.14.0.0", "5ef81e490ae46b1100d61e33d0183094c7a80ca8", "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36")
    ]);
    public static async Task<IReadOnlyList<string>> FetchAsync(string directory, CancellationToken cancellation = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(directory);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        var paths = new List<string>();
        foreach (var release in Releases)
        {
            var path = Path.GetFullPath(Path.Combine(directory, release.FileName));
            if (File.Exists(path))
            {
                if (new FileInfo(path).LinkTarget != null || Il.Hash(await File.ReadAllBytesAsync(path, cancellation)) != release.Sha256)
                    throw new InvalidDataException("Existing official fixture differs; move it aside before fetching: " + path);
                log?.Invoke($"[cache] verified version={release.Version}; bytes={new FileInfo(path).Length}; sha256={release.Sha256}; path={path}");
                paths.Add(path); continue;
            }
            log?.Invoke($"[download] version={release.Version}; officialCommit={release.Commit}; source={release.Url}; expectedSHA256={release.Sha256}");
            var archive = await client.GetByteArrayAsync(release.Url, cancellation);
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var entry = zip.GetEntry("Plain Craft Launcher 2.exe") ?? throw new InvalidDataException("Official archive lacks launcher EXE");
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Official EXE exceeds expected size limit");
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, cancellation);
            var exe = bytes.ToArray();
            if (Il.Hash(exe) != release.Sha256) throw new InvalidDataException("Official download integrity check failed");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, exe, cancellation);
                File.Move(temp, path); // No overwrite of a concurrent download or user file.
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            log?.Invoke($"[download] complete; ZIP bytes={archive.Length}; EXE bytes={exe.Length}; SHA256 verified; saved={path}");
            paths.Add(path);
        }
        return paths;
    }
}
