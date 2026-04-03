using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

internal sealed partial class BuildScript
{
    static readonly HttpClient DiscordOcrHttpClient = CreateDiscordOcrHttpClient();
    const int DiscordTesseractTimeoutMs = 2 * 60 * 1000;
    const int DiscordVisionTimeoutSeconds = 240;

    sealed record DiscordOcrAuditEntry(
        string ImagePath,
        string Engine,
        string Classification,
        int Confidence,
        bool WroteSidecar,
        string SidecarPath,
        string Error);

    void RunDiscordOcrPipeline(string guildDir, WorkspaceConfig workspaceConfig, bool full)
    {
        if (!Directory.Exists(guildDir))
        {
            return;
        }

        var guildName = Path.GetFileName(guildDir);
        var ocrRoot = ResolvePath(DiscordOcrOutRelativeDir);
        EnsureDir(ocrRoot);
        var guildOcrOut = Path.Combine(ocrRoot, guildName);
        EnsureDir(guildOcrOut);

        var mediaFiles = Directory.GetFiles(guildDir, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedOcrImageFile)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Media{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mediaFiles.Count == 0)
        {
            Log.Information($"No OCR media files found for {guildName}.");
            return;
        }

        var audit = new ConcurrentBag<DiscordOcrAuditEntry>();
        var tesseractPath = Path.Combine(ResolveDiscordToolDirectory("tesseract"), "tesseract.exe");
        var tesseractEnabled = File.Exists(tesseractPath);

        if (tesseractEnabled)
        {
            Parallel.ForEach(mediaFiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, imagePath =>
            {
                if (!full && HasOcrSidecarText(imagePath))
                {
                    return;
                }

                var result = RunTesseractOcr(imagePath, tesseractPath, DiscordTesseractMinDimension);
                if (result is not null)
                {
                    audit.Add(result);
                }
            });
        }
        else
        {
            Log.Information($"Tesseract binary not found; skipping tesseract OCR pass for {guildName}: {tesseractPath}");
        }

        var hasLlmConfig = !string.IsNullOrWhiteSpace(workspaceConfig.OpenApiUrl)
                           && !string.IsNullOrWhiteSpace(workspaceConfig.OpenApiModel);
        if (!hasLlmConfig)
        {
            Log.Information("Skipping LLM OCR pass (OpenApiUrl/OpenApiModel are not configured in .workspace/config.local.json).");
            WriteDiscordOcrAudit(guildOcrOut, audit);
            return;
        }

        var resolvedApiKey = ResolveConfigEnvValue(workspaceConfig.OpenApiKey, nameof(WorkspaceConfig.OpenApiKey));
        var visionCandidates = mediaFiles
            .Where(path => full || !HasOcrSidecarText(path))
            .ToList();
        if (visionCandidates.Count == 0)
        {
            WriteDiscordOcrAudit(guildOcrOut, audit);
            return;
        }

        Parallel.ForEach(visionCandidates, new ParallelOptions { MaxDegreeOfParallelism = 2 }, imagePath =>
        {
            var sidecarPath = imagePath + ".ocr.txt";
            var llm = RunLocalVisionOcr(
                imagePath,
                workspaceConfig.OpenApiUrl,
                resolvedApiKey,
                workspaceConfig.OpenApiModel,
                DiscordVisionTimeoutSeconds);
            if (!llm.Success)
            {
                audit.Add(new DiscordOcrAuditEntry(
                    ImagePath: imagePath,
                    Engine: "local-llm",
                    Classification: "error",
                    Confidence: 0,
                    WroteSidecar: false,
                    SidecarPath: sidecarPath,
                    Error: llm.Error));
                return;
            }

            var text = (llm.Text ?? string.Empty).Trim();
            var hasText = !string.IsNullOrWhiteSpace(text);
            var classification = NormalizeOcrClassification(llm.Classification, hasText);
            var confidence = Math.Clamp(llm.Confidence, 0, 100);
            var shouldWrite = hasText && confidence >= 60 && (classification is "full_text" or "partial_text" or "needs_review");
            var wroteSidecar = false;
            if (shouldWrite)
            {
                File.WriteAllText(sidecarPath, text + Environment.NewLine, Utf8NoBom);
                wroteSidecar = true;
            }

            audit.Add(new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "local-llm",
                Classification: classification,
                Confidence: confidence,
                WroteSidecar: wroteSidecar,
                SidecarPath: sidecarPath,
                Error: string.Empty));
        });

        WriteDiscordOcrAudit(guildOcrOut, audit);
    }

    static void WriteDiscordOcrAudit(string guildOcrOut, IEnumerable<DiscordOcrAuditEntry> entries)
    {
        var ordered = entries
            .OrderBy(x => x.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Engine, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = ordered.Select(entry => JsonSerializer.Serialize(entry)).ToList();
        var jsonlPath = Path.Combine(guildOcrOut, "ocr_image_results.jsonl");
        if (lines.Count == 0)
        {
            if (File.Exists(jsonlPath))
            {
                File.Delete(jsonlPath);
            }
        }
        else
        {
            File.WriteAllText(jsonlPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Utf8NoBom);
        }

        var summaryPath = Path.Combine(guildOcrOut, "ocr.summary.json");
        var summary = new
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Total = ordered.Count,
            Tesseract = ordered.Count(x => x.Engine.Equals("tesseract", StringComparison.OrdinalIgnoreCase)),
            LocalLlm = ordered.Count(x => x.Engine.Equals("local-llm", StringComparison.OrdinalIgnoreCase)),
            SidecarsWritten = ordered.Count(x => x.WroteSidecar),
            Errors = ordered.Count(x => !string.IsNullOrWhiteSpace(x.Error))
        };
        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            Utf8NoBom);
    }

    DiscordOcrAuditEntry? RunTesseractOcr(string imagePath, string tesseractPath, int minDimension)
    {
        var sidecarPath = imagePath + ".ocr.txt";
        if (!OperatingSystem.IsWindows())
        {
            return new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "tesseract",
                Classification: "skipped",
                Confidence: 0,
                WroteSidecar: false,
                SidecarPath: sidecarPath,
                Error: "unsupported_platform");
        }

        if (!File.Exists(imagePath))
        {
            return new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "tesseract",
                Classification: "error",
                Confidence: 0,
                WroteSidecar: false,
                SidecarPath: sidecarPath,
                Error: "image_missing");
        }

        if (!TryReadImageDimensions(imagePath, out var width, out var height))
        {
            width = minDimension;
            height = minDimension;
        }

        if (Math.Max(width, height) < minDimension)
        {
            return new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "tesseract",
                Classification: "skipped_small_image",
                Confidence: 0,
                WroteSidecar: false,
                SidecarPath: sidecarPath,
                Error: string.Empty);
        }

        var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tempResultPath = tempBase + ".txt";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tesseractPath,
                Arguments = $"{Quote(imagePath)} {Quote(tempBase)} -l eng --psm 3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new DiscordOcrAuditEntry(
                    ImagePath: imagePath,
                    Engine: "tesseract",
                    Classification: "error",
                    Confidence: 0,
                    WroteSidecar: false,
                    SidecarPath: sidecarPath,
                    Error: "start_failed");
            }

            if (!process.WaitForExit(DiscordTesseractTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }

                return new DiscordOcrAuditEntry(
                    ImagePath: imagePath,
                    Engine: "tesseract",
                    Classification: "error",
                    Confidence: 0,
                    WroteSidecar: false,
                    SidecarPath: sidecarPath,
                    Error: "timeout");
            }

            if (!File.Exists(tempResultPath))
            {
                return new DiscordOcrAuditEntry(
                    ImagePath: imagePath,
                    Engine: "tesseract",
                    Classification: "no_text",
                    Confidence: 0,
                    WroteSidecar: false,
                    SidecarPath: sidecarPath,
                    Error: string.Empty);
            }

            var text = File.ReadAllText(tempResultPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new DiscordOcrAuditEntry(
                    ImagePath: imagePath,
                    Engine: "tesseract",
                    Classification: "no_text",
                    Confidence: 0,
                    WroteSidecar: false,
                    SidecarPath: sidecarPath,
                    Error: string.Empty);
            }

            File.WriteAllText(sidecarPath, text, Utf8NoBom);
            return new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "tesseract",
                Classification: "full_text",
                Confidence: 90,
                WroteSidecar: true,
                SidecarPath: sidecarPath,
                Error: string.Empty);
        }
        catch (Exception ex)
        {
            return new DiscordOcrAuditEntry(
                ImagePath: imagePath,
                Engine: "tesseract",
                Classification: "error",
                Confidence: 0,
                WroteSidecar: false,
                SidecarPath: sidecarPath,
                Error: ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempResultPath))
                {
                    File.Delete(tempResultPath);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }

    static bool HasOcrSidecarText(string imagePath)
    {
        var sidecarPath = imagePath + ".ocr.txt";
        if (!File.Exists(sidecarPath))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(SafeReadAllText(sidecarPath));
    }

    static bool TryReadImageDimensions(string imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            return false;
        }

        try
        {
            using var image = System.Drawing.Image.FromFile(imagePath);
            width = image.Width;
            height = image.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    sealed record DiscordLlmOcrResult(bool Success, string Classification, int Confidence, string Text, string Error);

    DiscordLlmOcrResult RunLocalVisionOcr(
        string imagePath,
        string apiBase,
        string apiKey,
        string model,
        int timeoutSeconds)
    {
        try
        {
            var bytes = File.ReadAllBytes(imagePath);
            if (bytes.Length == 0)
            {
                return new DiscordLlmOcrResult(false, "error", 0, string.Empty, "empty_image");
            }

            var mime = GetImageMimeType(imagePath);
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            var requestPayload = new
            {
                model,
                temperature = 0,
                max_tokens = 1200,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text =
                                    "Return only minified JSON with keys classification,confidence,text,notes. classification must be one of no_text, partial_text, full_text, needs_review. confidence must be 0-100."
                            },
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = dataUrl
                                }
                            }
                        }
                    }
                }
            };

            var requestJson = JsonSerializer.Serialize(requestPayload);
            var endpoint = apiBase.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Utf8NoBom, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
            using var response = DiscordOcrHttpClient.SendAsync(request, cts.Token).GetAwaiter().GetResult();
            var payload = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                var shortPayload = payload.Length > 400 ? payload[..400] : payload;
                return new DiscordLlmOcrResult(false, "error", 0, string.Empty, $"http_{(int)response.StatusCode}:{shortPayload}");
            }

            if (!TryExtractChatContent(payload, out var modelText))
            {
                return new DiscordLlmOcrResult(false, "error", 0, string.Empty, "missing_choice_content");
            }

            if (!TryParseOcrPayload(modelText, out var classification, out var confidence, out var text))
            {
                return new DiscordLlmOcrResult(false, "error", 0, string.Empty, "invalid_json_payload");
            }

            return new DiscordLlmOcrResult(
                Success: true,
                Classification: classification,
                Confidence: confidence,
                Text: text,
                Error: string.Empty);
        }
        catch (Exception ex)
        {
            return new DiscordLlmOcrResult(false, "error", 0, string.Empty, ex.Message);
        }
    }

    static bool TryExtractChatContent(string payload, out string content)
    {
        content = string.Empty;
        try
        {
            var node = JsonNode.Parse(payload);
            var choices = node?["choices"]?.AsArray();
            if (choices is null || choices.Count == 0)
            {
                return false;
            }

            var messageNode = choices[0]?["message"]?["content"];
            if (messageNode is null)
            {
                return false;
            }

            if (messageNode is JsonValue value)
            {
                content = value.ToString();
                return !string.IsNullOrWhiteSpace(content);
            }

            if (messageNode is JsonArray contentArray)
            {
                var parts = contentArray
                    .Select(item => item?["text"]?.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                content = string.Join(Environment.NewLine, parts);
                return !string.IsNullOrWhiteSpace(content);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    static bool TryParseOcrPayload(string raw, out string classification, out int confidence, out string text)
    {
        classification = "needs_review";
        confidence = 0;
        text = string.Empty;
        var normalized = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = normalized.IndexOf('\n');
            if (firstNewLine > 0)
            {
                normalized = normalized[(firstNewLine + 1)..];
            }

            var fenceEnd = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                normalized = normalized[..fenceEnd];
            }

            normalized = normalized.Trim();
        }

        var start = normalized.IndexOf('{');
        var end = normalized.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            normalized = normalized[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            var root = doc.RootElement;
            classification = root.TryGetProperty("classification", out var classificationNode)
                ? NormalizeOcrClassification(classificationNode.ToString(), hasText: false)
                : "needs_review";
            confidence = root.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.TryGetInt32(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0, 100)
                : 0;
            text = root.TryGetProperty("text", out var textNode)
                ? (textNode.ValueKind == JsonValueKind.String ? textNode.GetString() ?? string.Empty : textNode.ToString())
                : string.Empty;
            classification = NormalizeOcrClassification(classification, !string.IsNullOrWhiteSpace(text));
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string NormalizeOcrClassification(string value, bool hasText)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "full_text" => "full_text",
            "partial_text" => "partial_text",
            "needs_review" => "needs_review",
            "no_text" => "no_text",
            _ => hasText ? "partial_text" : "no_text"
        };
    }

    static string GetImageMimeType(string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    static HttpClient CreateDiscordOcrHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(DiscordVisionTimeoutSeconds)
        };
    }
}
