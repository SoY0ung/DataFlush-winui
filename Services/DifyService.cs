using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataFlushWinUI.Models;

namespace DataFlushWinUI.Services;

public sealed class DifyService
{
    private readonly HttpClient _httpClient = new();

    public static string AiJsonPath(string csvDir, string fileName) => Path.Combine(csvDir, "AI_landmark", $"{fileName}.json");

    public async Task<string> CheckHealthAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
    }

    public async Task<AiPayload> RunWorkflowAsync(string datasetDir, string csvDir, string fileName, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var imagePath = Path.Combine(datasetDir, fileName);
        var uploadId = await UploadFileAsync(imagePath, baseUrl, apiKey, cancellationToken);
        var raw = await RunWorkflowAsync(uploadId, baseUrl, apiKey, cancellationToken);
        var result = ParseAiResult(raw);
        var payload = new AiPayload { FileName = fileName, Result = result, Raw = JsonSerializer.Deserialize<object>(raw.GetRawText()) };

        var outputPath = AiJsonPath(csvDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temp = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(payload, AppState.JsonOptions), Encoding.UTF8, cancellationToken);
        File.Move(temp, outputPath, overwrite: true);
        return payload;
    }

    private async Task<string> UploadFileAsync(string imagePath, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(imagePath);
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(imagePath));
        form.Add(new StringContent("dataflush-local"), "user");
        form.Add(streamContent, "file", Path.GetFileName(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/files/upload");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = form;
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("id", out var id))
        {
            return id.GetString() ?? throw new InvalidOperationException("上传响应 id 为空。");
        }

        throw new InvalidOperationException("上传响应中没有 id。");
    }

    private async Task<JsonElement> RunWorkflowAsync(string uploadFileId, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            inputs = new
            {
                face = new
                {
                    type = "image",
                    transfer_method = "local_file",
                    upload_file_id = uploadFileId
                }
            },
            response_mode = "blocking",
            user = "dataflush-local"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/workflows/run");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    public static AiResult ParseAiResult(JsonElement response)
    {
        foreach (var candidate in EnumerateCandidates(response))
        {
            if (candidate.ValueKind == JsonValueKind.Object && (candidate.TryGetProperty("class_id", out _) || candidate.TryGetProperty("class_name", out _)))
            {
                return Normalize(candidate);
            }

            if (candidate.ValueKind == JsonValueKind.String)
            {
                var text = candidate.GetString()?.Trim() ?? "";
                if (text.StartsWith("```"))
                {
                    text = text.Trim('`').Trim();
                    if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[4..].Trim();
                    }
                }

                try
                {
                    using var document = JsonDocument.Parse(text);
                    return Normalize(document.RootElement);
                }
                catch
                {
                    // Try the next candidate.
                }
            }
        }

        return Normalize(default);
    }

    private static IEnumerable<JsonElement> EnumerateCandidates(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "result", "text", "answer", "output", "json" })
            {
                if (outputs.TryGetProperty(key, out var value))
                {
                    yield return value;
                }
            }

            foreach (var property in outputs.EnumerateObject())
            {
                yield return property.Value;
            }
        }

        yield return response;
    }

    private static AiResult Normalize(JsonElement element)
    {
        var classId = TryGetString(element, "class_id").PadLeft(2, '0');
        var classIndex = FaceClasses.IndexFromCode(classId);
        if (classIndex is null)
        {
            classId = "14";
            classIndex = 14;
        }

        var certainty = TryGetString(element, "certainty");
        if (certainty is not ("低" or "中" or "高"))
        {
            certainty = "中";
        }

        return new AiResult
        {
            ClassId = classId,
            ClassName = FaceClasses.ByIndex(classIndex.Value).Name,
            Certainty = certainty,
            Info = TryGetString(element, "info")
        };
    }

    private static string TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        }

        return "";
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream"
    };
}

