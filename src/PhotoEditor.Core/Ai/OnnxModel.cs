using Microsoft.ML.OnnxRuntime;

namespace PhotoEditor.Core.Ai;

/// <summary>Which hardware runs a model.</summary>
public enum InferenceDevice
{
    /// <summary>GPU via DirectML (Windows, any DirectX 12 GPU).</summary>
    DirectML,
    Cpu,
}

/// <summary>A float tensor: row-major data plus its shape.</summary>
public sealed record Tensor(float[] Data, long[] Shape)
{
    public long Length => Shape.Aggregate(1L, (a, b) => a * b);
}

/// <summary>
/// A loaded ONNX model. Tries the GPU (DirectML) first and falls back to the CPU when DirectML is not
/// available (other OS, no DirectX 12 GPU, driver problem) or the model fails to load on it.
/// </summary>
public sealed class OnnxModel : IDisposable
{
    private readonly InferenceSession _session;

    private OnnxModel(InferenceSession session, InferenceDevice device, string? fallbackReason)
    {
        _session = session;
        Device = device;
        FallbackReason = fallbackReason;
    }

    /// <summary>Where the model runs.</summary>
    public InferenceDevice Device { get; }

    /// <summary>Why the GPU is not used (null when it is, or when the CPU was requested).</summary>
    public string? FallbackReason { get; }

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
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    };
                    options.AppendExecutionProvider_DML(0);
                    return new OnnxModel(new InferenceSession(path, options), InferenceDevice.DirectML, null);
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

        var cpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new OnnxModel(new InferenceSession(path, cpu), InferenceDevice.Cpu, reason);
    }

    /// <summary>Runs the model with named float inputs; returns all outputs as float tensors.</summary>
    public IReadOnlyDictionary<string, Tensor> Run(IReadOnlyDictionary<string, Tensor> inputs)
    {
        var values = new List<OrtValue>();
        try
        {
            foreach (var (_, tensor) in inputs)
            {
                if (tensor.Data.Length != tensor.Length)
                    throw new ArgumentException($"Tensor data has {tensor.Data.Length} values, shape needs {tensor.Length}.");
                values.Add(OrtValue.CreateTensorValueFromMemory(tensor.Data, tensor.Shape));
            }
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, inputs.Keys.ToList(), values, _session.OutputNames);
            var result = new Dictionary<string, Tensor>();
            for (int i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                var shape = output.GetTensorTypeAndShape().Shape;
                result[_session.OutputNames[i]] = new Tensor(output.GetTensorDataAsSpan<float>().ToArray(), shape);
            }
            return result;
        }
        finally
        {
            foreach (var v in values)
                v.Dispose();
        }
    }

    public void Dispose() => _session.Dispose();
}
