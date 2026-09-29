namespace PhotoEditor.Core.Ai;

/// <summary>
/// The AI models the app can download. Every file is pinned to a repository revision and SHA-256.
/// Licences are listed in THIRD-PARTY-NOTICES.md.
/// </summary>
public static class ModelCatalog
{
    private const string Sam21Tiny = "https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX/resolve/814a066640debee5a91e70aa401fb8e17e030503/onnx/";
    private const string Sam21License = "Apache-2.0 (Meta SAM 2.1; ONNX conversion by onnx-community)";

    /// <summary>SAM 2.1 tiny image encoder (runs once per photo).</summary>
    public static readonly ModelInfo SamEncoder = new("sam2.1-tiny/vision_encoder.onnx", "Select Object: image encoder",
        Sam21Tiny + "vision_encoder.onnx", "4f30aacd3aaefbca81a0b7fe4c1fc96345570ea0a6f80ced599493d1b3be2e8c", 354_238, Sam21License);

    public static readonly ModelInfo SamEncoderData = new("sam2.1-tiny/vision_encoder.onnx_data", "Select Object: image encoder weights",
        Sam21Tiny + "vision_encoder.onnx_data", "e83df9866a5afe68ea7f0f721f18f65137fc3acbf0da1c74e946d363e09c69cc", 134_084_864, Sam21License);

    /// <summary>SAM 2.1 tiny prompt encoder + mask decoder (runs per click / box).</summary>
    public static readonly ModelInfo SamDecoder = new("sam2.1-tiny/prompt_encoder_mask_decoder.onnx", "Select Object: mask decoder",
        Sam21Tiny + "prompt_encoder_mask_decoder.onnx", "874414704c5d686db7d206a35f6e15d26563d50c8c4468fccc6739bd7e491dcf", 213_114, Sam21License);

    public static readonly ModelInfo SamDecoderData = new("sam2.1-tiny/prompt_encoder_mask_decoder.onnx_data", "Select Object: mask decoder weights",
        Sam21Tiny + "prompt_encoder_mask_decoder.onnx_data", "e9874d900dd4134ed60eab1e97910327c2419e0b2954485d8fd6e7f1a1470f47", 20_958_208, Sam21License);

    /// <summary>All files needed for click / box selection (≈ 155 MB).</summary>
    public static IReadOnlyList<ModelInfo> SelectObject { get; } = [SamEncoder, SamEncoderData, SamDecoder, SamDecoderData];

    /// <summary>BiRefNet-lite (salient object / subject segmentation), 1024 × 1024 input.</summary>
    public static readonly ModelInfo Subject = new("birefnet-lite/model.onnx", "Select Subject",
        "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/de15b22ba131738a16dff04aab8bdf8dc32e3ac1/onnx/model.onnx",
        "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", 224_005_088,
        "MIT (BiRefNet by Zheng Peng et al.; ONNX conversion by onnx-community)");

    /// <summary>Files needed for Select Subject (≈ 224 MB).</summary>
    public static IReadOnlyList<ModelInfo> SelectSubject { get; } = [Subject];
}
