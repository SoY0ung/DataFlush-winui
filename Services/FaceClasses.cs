using DataFlushWinUI.Models;

namespace DataFlushWinUI.Services;

public static class FaceClasses
{
    public static readonly IReadOnlyList<FaceClass> All =
    [
        new(0, "00", "正常", "00_normal.csv"),
        new(1, "01", "部分遮挡", "01_partial_occlusion.csv"),
        new(2, "02", "双眼遮挡", "02_both_eyes_occluded.csv"),
        new(3, "03", "墨镜遮挡", "03_sunglasses.csv"),
        new(4, "04", "帽子双眼", "04_hat_both_eyes.csv"),
        new(5, "05", "中间角度", "05_middle_angle.csv"),
        new(6, "06", "大角度上", "06_large_angle_up.csv"),
        new(7, "07", "大角度下", "07_large_angle_down.csv"),
        new(8, "08", "大角度左", "08_large_angle_left.csv"),
        new(9, "09", "大角度右", "09_large_angle_right.csv"),
        new(10, "10", "戴口罩", "10_mask.csv"),
        new(11, "11", "过亮", "11_overexposed.csv"),
        new(12, "12", "过暗", "12_underexposed.csv"),
        new(13, "13", "模糊", "13_blurry.csv"),
        new(14, "14", "非人脸", "14_non_face.csv"),
        new(15, "15", "未处理数据", "15_unprocessed.csv"),
        new(16, "h0", "帽子正常", "h0_hat_normal.csv"),
        new(17, "h1", "帽子遮挡", "h1_hat_occlusion.csv"),
        new(18, "h5", "帽子中间角度", "h5_hat_middle_angle.csv"),
    ];

    public static readonly IReadOnlyList<FaceClass> Core = All.Take(16).ToArray();

    public static readonly IReadOnlyList<FaceClass> Classifiable = Core.Take(15).ToArray();

    public static readonly IReadOnlyList<FaceClass> HatExtension = All.Skip(16).ToArray();

    public static readonly IReadOnlyList<FaceClass> HatPageClasses =
    [
        All[0], All[1], All[4], All[5], All[14], All[15], All[16], All[17], All[18]
    ];

    public static readonly IReadOnlyDictionary<int, int> HatPairMap = new Dictionary<int, int>
    {
        [0] = 16,
        [16] = 0,
        [1] = 17,
        [17] = 1,
        [5] = 18,
        [18] = 5,
        [15] = 4,
        [4] = 15
    };

    public static readonly IReadOnlyList<int> HatTargets = [16, 17, 18, 4];

    public static readonly IReadOnlyList<int> OriginalHatSources = [0, 1, 5, 14, 15];

    public static FaceClass ByIndex(int index) => All[index];

    public static bool IsHatEnabled(string csvPath)
        => !string.IsNullOrWhiteSpace(csvPath)
           && Directory.Exists(csvPath)
           && HatExtension.All(c => File.Exists(Path.Combine(csvPath, c.CsvFileName)));

    public static int? IndexFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.PadLeft(2, '0');
        return All.FirstOrDefault(c => c.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))?.Index;
    }
}
