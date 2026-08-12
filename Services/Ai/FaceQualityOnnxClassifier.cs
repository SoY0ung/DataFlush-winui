using DataFlushWinUI.Models;
using DataFlushWinUI.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace DataFlushWinUI.Services.Ai;

public sealed class FaceQualityOnnxClassifier : IDisposable
{
    private static readonly bool UseNchw = true;
    private static readonly bool UseRgb = true;
    private static readonly bool DivideBy255 = true;
    private static readonly bool UseLetterboxPadding = true;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private const int DefaultInputWidth = 224;
    private const int DefaultInputHeight = 224;
    private const byte PaddingValue = 0;

    private readonly InferenceSession _session;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private bool _disposed;

    public FaceQualityOnnxClassifier(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("模型路径不能为空。", nameof(modelPath));
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("找不到 ONNX 模型文件。", modelPath);
        }

        _session = new InferenceSession(modelPath);
        InputName = _session.InputMetadata.Keys.First();
        InputShape = _session.InputMetadata[InputName].Dimensions.ToArray();
        OutputName = _session.OutputMetadata.Keys.First();
        OutputShape = _session.OutputMetadata[OutputName].Dimensions.ToArray();

        (_inputWidth, _inputHeight) = ResolveInputSize(InputShape);
    }

    public string InputName { get; }

    public int[] InputShape { get; }

    public string OutputName { get; }

    public int[] OutputShape { get; }

    public AiFaceQualityResult Predict(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tensor = Preprocess(imagePath);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(InputName, tensor)]);
        var logits = results.First(x => x.Name == OutputName).AsTensor<float>().ToArray();
        if (logits.Length == 0)
        {
            throw new InvalidOperationException("模型输出为空。");
        }

        var probabilities = Softmax(logits);
        var classIndex = ArgMax(probabilities);
        var modelClass = OnnxClasses.ById(classIndex);
        var probability = probabilities[classIndex];

        return new AiFaceQualityResult
        {
            ClassIndex = classIndex,
            ClassId = classIndex.ToString(),
            PlatformName = modelClass.PlatformName,
            ClassName = modelClass.DetailName,
            Certainty = CertaintyFromProbability(probability),
            Probability = probability,
            Logits = logits,
            Probabilities = probabilities
        };
    }

    public Task<AiFaceQualityResult> PredictAsync(string imagePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Predict(imagePath);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);

    private DenseTensor<float> Preprocess(string imagePath)
    {
        using var bgr = ImReadUnicode(imagePath);
        if (bgr.Empty())
        {
            throw new InvalidOperationException($"无法读取图片：{imagePath}");
        }

        using var preparedBgr = ResizeAndPad(bgr, _inputWidth, _inputHeight);
        using var inputColor = new Mat();
        if (UseRgb)
        {
            Cv2.CvtColor(preparedBgr, inputColor, ColorConversionCodes.BGR2RGB);
        }
        else
        {
            preparedBgr.CopyTo(inputColor);
        }

        return UseNchw
            ? ToNchwTensor(inputColor, _inputWidth, _inputHeight)
            : ToNhwcTensor(inputColor, _inputWidth, _inputHeight);
    }

    private static Mat ImReadUnicode(string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        return Cv2.ImDecode(bytes, ImreadModes.Color);
    }

    private static Mat ResizeAndPad(Mat bgr, int inputW, int inputH)
    {
        if (bgr.Rows == inputH && bgr.Cols == inputW)
        {
            return bgr.Clone();
        }

        if (!UseLetterboxPadding)
        {
            var direct = new Mat();
            Cv2.Resize(bgr, direct, new Size(inputW, inputH), 0, 0, InterpolationFlags.Linear);
            return direct;
        }

        var canvas = new Mat(new Size(inputW, inputH), MatType.CV_8UC3, Scalar.All(PaddingValue));
        var maxLen = Math.Max(bgr.Rows, bgr.Cols);
        var ratio = (double)inputW / maxLen;
        var h = Math.Max(1, (int)(bgr.Rows * ratio));
        var w = Math.Max(1, (int)(bgr.Cols * ratio));

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(w, h), 0, 0, InterpolationFlags.Linear);
        var x = (inputW - w) / 2;
        var y = (inputH - h) / 2;
        using var roi = new Mat(canvas, new Rect(x, y, w, h));
        resized.CopyTo(roi);
        return canvas;
    }

    private static DenseTensor<float> ToNchwTensor(Mat rgb, int inputW, int inputH)
    {
        var tensor = new DenseTensor<float>([1, 3, inputH, inputW]);
        for (var y = 0; y < inputH; y++)
        {
            for (var x = 0; x < inputW; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = Normalize(pixel.Item0, 0);
                tensor[0, 1, y, x] = Normalize(pixel.Item1, 1);
                tensor[0, 2, y, x] = Normalize(pixel.Item2, 2);
            }
        }

        return tensor;
    }

    private static DenseTensor<float> ToNhwcTensor(Mat rgb, int inputW, int inputH)
    {
        var tensor = new DenseTensor<float>([1, inputH, inputW, 3]);
        for (var y = 0; y < inputH; y++)
        {
            for (var x = 0; x < inputW; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, y, x, 0] = Normalize(pixel.Item0, 0);
                tensor[0, y, x, 1] = Normalize(pixel.Item1, 1);
                tensor[0, y, x, 2] = Normalize(pixel.Item2, 2);
            }
        }

        return tensor;
    }

    private static float Normalize(byte value, int channel)
    {
        var normalized = DivideBy255 ? value / 255.0f : value;
        return AppState.Current.UseOnnxMeanStdPreprocessing
            ? (normalized - Mean[channel]) / Std[channel]
            : normalized;
    }

    private static (int Width, int Height) ResolveInputSize(int[] shape)
    {
        if (shape.Length >= 4)
        {
            var h = UseNchw ? shape[^2] : shape[^3];
            var w = UseNchw ? shape[^1] : shape[^2];
            if (h > 0 && w > 0)
            {
                return (w, h);
            }
        }

        return (DefaultInputWidth, DefaultInputHeight);
    }

    private static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exp = new float[logits.Length];
        var sum = 0f;
        for (var i = 0; i < logits.Length; i++)
        {
            exp[i] = MathF.Exp(logits[i] - max);
            sum += exp[i];
        }

        if (sum <= 0)
        {
            return exp;
        }

        for (var i = 0; i < exp.Length; i++)
        {
            exp[i] /= sum;
        }

        return exp;
    }

    private static int ArgMax(float[] values)
    {
        var index = 0;
        var best = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > best)
            {
                best = values[i];
                index = i;
            }
        }

        return index;
    }

    private static string CertaintyFromProbability(float probability)
    {
        if (probability >= 0.80f)
        {
            return "高";
        }

        return probability >= 0.50f ? "中" : "低";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}
