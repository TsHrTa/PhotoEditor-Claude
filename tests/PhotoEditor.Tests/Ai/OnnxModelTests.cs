using System.Net;
using System.Security.Cryptography;
using PhotoEditor.Core.Ai;

namespace PhotoEditor.Tests.Ai;

public class OnnxModelTests
{
    /// <summary>Test model: mask = sigmoid(2 · image), shape [1, 3, H, W].</summary>
    private static string TestModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "sigmoid2x.onnx");

    [Fact]
    public void Load_FallsBackToCpu_AndRuns()
    {
        using var model = OnnxModel.Load(TestModelPath);
        Assert.Equal(InferenceDevice.Cpu, model.Device); // no DirectML on the Linux test machine
        Assert.NotNull(model.FallbackReason);
        Assert.Equal(["image"], model.InputNames);
        Assert.Equal(["mask"], model.OutputNames);

        var input = new Tensor([0f, 1f, -1f, 0.5f, 2f, -3f], [1, 3, 1, 2]);
        var output = model.Run(new Dictionary<string, Tensor> { ["image"] = input })["mask"];
        Assert.Equal(input.Shape, output.Shape);
        for (int i = 0; i < input.Data.Length; i++)
            Assert.Equal(1f / (1f + MathF.Exp(-2f * input.Data[i])), output.Data[i], 5);
    }

    [Fact]
    public void Load_CpuRequested_HasNoFallbackReason()
    {
        using var model = OnnxModel.Load(TestModelPath, InferenceDevice.Cpu);
        Assert.Equal(InferenceDevice.Cpu, model.Device);
        Assert.Null(model.FallbackReason);
    }

    [Fact]
    public void Run_WrongDataLength_Throws()
    {
        using var model = OnnxModel.Load(TestModelPath, InferenceDevice.Cpu);
        Assert.Throws<ArgumentException>(() =>
            model.Run(new Dictionary<string, Tensor> { ["image"] = new Tensor([1f], [1, 3, 1, 2]) }));
    }
}

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-models-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Serves fixed bytes and counts requests.</summary>
    private sealed class FakeHandler(byte[] content) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private static readonly byte[] Payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();

    private static ModelInfo Info(string sha) =>
        new("test.onnx", "Test model", "https://example.invalid/test.onnx", sha, Payload.Length, "MIT");

    [Fact]
    public async Task Downloads_Verifies_AndCaches()
    {
        var handler = new FakeHandler(Payload);
        var store = new ModelStore(_dir, new HttpClient(handler));
        var model = Info(Convert.ToHexString(SHA256.HashData(Payload)));
        Assert.False(store.IsAvailable(model));

        var reports = new List<DownloadProgress>();
        var path = await store.GetAsync(model, new SyncProgress(reports.Add));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.True(store.IsAvailable(model));
        Assert.Equal(Payload.Length, reports[^1].Received);
        Assert.Equal(1.0, reports[^1].Fraction);

        await store.GetAsync(model); // cached: no second request
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task WrongHash_IsRejected_AndLeavesNoFile()
    {
        var store = new ModelStore(_dir, new HttpClient(new FakeHandler(Payload)));
        var model = Info(new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(model));
        Assert.False(store.IsAvailable(model));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
