using System.Security.Cryptography;

namespace PhotoEditor.Core.Ai;

/// <summary>A downloadable model file.</summary>
/// <param name="Id">Stable identifier, also the file name in the model folder (e.g. "mobile-sam-encoder.onnx").</param>
/// <param name="Url">Where to download it from.</param>
/// <param name="Sha256">Expected SHA-256 (hex); the download is rejected if it differs.</param>
/// <param name="SizeBytes">Approximate size, shown before downloading.</param>
/// <param name="License">Licence of the model weights, shown to the user.</param>
public sealed record ModelInfo(string Id, string DisplayName, string Url, string Sha256, long SizeBytes, string License);

/// <summary>Progress of a model download (bytes so far, total if known).</summary>
public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

/// <summary>
/// Keeps AI models in a local folder (default: <c>%LOCALAPPDATA%\PhotoEditor\models</c>) and downloads
/// them on first use. Downloads go to a ".part" file and are checked against their SHA-256 before use.
/// </summary>
public sealed class ModelStore(string directory, HttpClient http)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "models");

    public string Directory { get; } = directory;

    public string PathOf(ModelInfo model) => Path.Combine(Directory, model.Id);

    public bool IsAvailable(ModelInfo model) => File.Exists(PathOf(model));

    /// <summary>Returns the local path of <paramref name="model"/>, downloading it first if needed.</summary>
    public async Task<string> GetAsync(ModelInfo model, IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        var path = PathOf(model);
        if (File.Exists(path))
            return path;

        System.IO.Directory.CreateDirectory(Directory);
        var part = path + ".part";
        try
        {
            using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(part);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    sha.AppendData(buffer, 0, read);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }

                var hash = Convert.ToHexString(sha.GetHashAndReset());
                if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Downloaded {model.DisplayName} is corrupt or changed (SHA-256 {hash}).");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    /// <summary>Deletes the local copy (e.g. to force a fresh download).</summary>
    public void Remove(ModelInfo model)
    {
        if (File.Exists(PathOf(model)))
            File.Delete(PathOf(model));
    }
}
