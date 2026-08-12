using DataFlushWinUI.Models;

namespace DataFlushWinUI.Services;

public static class OnnxClasses
{
    public static readonly IReadOnlyList<OnnxClass> All =
    [
        new(0, "Contact", "normal", "正常人脸", "正常人脸"),
        new(1, "ContactPresence", "half", "不完整人脸", "半脸 / 人脸不完整"),
        new(2, "DockBottom", "mask", "口罩遮挡", "口罩遮挡"),
        new(3, "Cloud", "occlusion", "其他遮挡", "其他遮挡"),
        new(4, "Hide", "eye_blocked", "眼部遮挡", "眼部遮挡"),
        new(5, "Contrast", "sunglasses", "墨镜遮挡", "墨镜"),
        new(6, "Brightness", "bright", "图片过亮", "过亮，占位类别"),
        new(7, "QuietHours", "dark_blur", "过暗模糊", "过暗 / 模糊"),
        new(8, "Blocked", "nonface", "非人脸", "非人脸"),
    ];

    public static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> DefaultMapping = new Dictionary<int, IReadOnlyList<int>>
    {
        [0] = [0],
        [1] = [5, 6, 7, 8, 9, 14],
        [2] = [10],
        [3] = [1, 2, 4, 14],
        [4] = [1, 2, 4],
        [5] = [3],
        [6] = [11, 14],
        [7] = [3, 12, 13, 14],
        [8] = [14],
    };

    public static OnnxClass ById(int id) => All.FirstOrDefault(x => x.Id == id) ?? All[^1];

    public static int? IdFromResult(AiResult? result)
    {
        if (result is null)
        {
            return null;
        }

        if (int.TryParse(result.ClassId, out var id) && id >= 0 && id < All.Count)
        {
            return id;
        }

        return All.FirstOrDefault(x => x.PlatformName.Equals(result.PlatformName, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
