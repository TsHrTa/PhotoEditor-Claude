using Microsoft.ML.OnnxRuntime;

namespace PhotoEditor.Core.Ai;

/// <summary>Which hardware runs a model.</summary>
public enum InferenceDevice
{
    /// <summary>GPU via DirectML (Windows, any DirectX 12 GPU).</summary>
    DirectML,
    Cpu,
}

/// <summary>A float tensor (or, with <see cref="LongData"/>, an int64 one): row-major data plus its shape.</summary>
public sealed record Tensor(float[] Data, long[] Shape)
{
    public long Length => Shape.Aggregate(1L, (a, b) => a * b);

    /// <summary>Set for int64 inputs (e.g. point labels); <see cref="Data"/> is then empty.</summary>
    public long[]? LongData { get; init; }

    public static Tensor Int64(long[] data, long[] shape) => new([], shape) { LongData = data };
}

/// <summary>
/// A loaded ONNX model. Tries the GPU (DirectML) first and falls back to the CPU when DirectML is not
/// available (other OS, no DirectX 12 GPU, driver problem), the model fails to load on it, or a run fails on it
/// (some GPUs / drivers reject operators of some models): then the model is reloaded on the CPU and the run repeated.
/// </summary>
public sealed class OnnxModel : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private InferenceSession _session;

    private OnnxModel(string path, InferenceSession session, InferenceDevice device, string? fallbackReason)
    {
        _path = path;
        _session = session;
        Device = device;
        FallbackReason = fallbackReason;
    }

    /// <summary>Where the model runs.</summary>
    public InferenceDevice Device { get; private set; }

    /// <summary>Why the GPU is not used (null when it is, or when the CPU was requested).</summary>
    public string? FallbackReason { get; private set; }

    public IReadOnlyList<string> InputNames => _session.InputNames;
    public IReadOnlyList<string> OutputNames => _session.OutputNames;

    /// <summary>True when this ONNX Runtime build includes DirectML (the Windows package).</summary>
    public static bool IsDirectMLAvailable =>
        OperatingSystem.IsWindows() && OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider");

    /// <summary>Loads the model at <paramref name="path"/>, preferring <paramref name="preferred"/>.</summary>
    public static OnnxModel Load(string path, InferenceDevice preferred = InferenceDevice.DirectML)
    {
        string? reason = null;
        if (preferred == InferenceDevice.DirectML)
        {
            if (IsDirectMLAvailable)
            {
                try
                {
                    var options = new SessionOptions
                    {
                        // Required by the DirectML execution provider.
                        EnableMemoryPattern = false,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        // Only basic optimizations: the extended ones fuse nodes (e.g. LayerNormFusion) into
                        // operators that DirectML rejects on some GPUs ("The parameter is incorrect").
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                    };
                    options.AppendExecutionProvider_DML(0);
                    return new OnnxModel(path, new InferenceSession(path, options), InferenceDevice.DirectML, null);
                }
                catch (OnnxRuntimeException ex)
                {
                    reason = $"DirectML failed: {ex.Message}";
                }
            }
            else
            {
                reason = "DirectML is not available on this system";
            }
        }

        return new OnnxModel(path, CpuSession(path), InferenceDevice.Cpu, reason);
    }

    private static InferenceSession CpuSession(string path) =>
        new(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });

    /// <summary>
    /// Runs the model with named float inputs; returns all outputs as float tensors. If the run fails on the GPU,
    /// the model moves to the CPU (for the rest of the session) and the run is repeated there.
    /// </summary>
    public IReadOnlyDictionary<string, Tensor> Run(IReadOnlyDictionary<string, Tensor> inputs)
    {
        InferenceSession session;
        lock (_lock)
            session = _session;
        try
        {
            return Run(session, inputs);
        }
        catch (OnnxRuntimeException ex) when (Device == InferenceDevice.DirectML)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = CpuSession(_path);
                    Device = InferenceDevice.Cpu;
                    FallbackReason = $"the GPU could not run this model ({FirstLine(ex.Message)})";
                    session.Dispose();
                }
                session = _session;
            }
            return Run(session, inputs);
        }
    }

    private static string FirstLine(string message)
    {
        int end = message.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? message : message[..end];
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static IReadOnlyDictionary<string, Tensor> Run(InferenceSession session, IReadOnlyDictionary<string, Tensor> inputs)
    {
        var values = new List<OrtValue>();
        try
        {
            foreach (var (_, tensor) in inputs)
            {
                int count = tensor.LongData?.Length ?? tensor.Data.Length;
                if (count != tensor.Length)
                    throw new ArgumentException($"Tensor data has {count} values, shape needs {tensor.Length}.");
                values.Add(tensor.LongData is { } longs
                    ? OrtValue.CreateTensorValueFromMemory(longs, tensor.Shape)
                    : OrtValue.CreateTensorValueFromMemory(tensor.Data, tensor.Shape));
            }
            using var runOptions = new RunOptions();
            using var outputs = session.Run(runOptions, inputs.Keys.ToList(), values, session.OutputNames);
            var result = new Dictionary<string, Tensor>();
            for (int i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                var shape = output.GetTensorTypeAndShape().Shape;
                result[session.OutputNames[i]] = new Tensor(output.GetTensorDataAsSpan<float>().ToArray(), shape);
            }
            return result;
        }
        finally
        {
            foreach (var v in values)
                v.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _session.Dispose();
    }
}
