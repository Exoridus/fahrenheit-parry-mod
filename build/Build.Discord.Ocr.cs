using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

internal sealed partial class BuildScript
{
    static readonly HttpClient DiscordOcrHttpClient = CreateDiscordOcrHttpClient();
    const int DiscordTesseractTimeoutMs = 2 * 60 * 1000;
    const int DiscordVisionTimeoutSeconds = 240;
    const string DiscordOcrIndexFileName = "ocr.index.csv";

    sealed record DiscordOcrAuditEntry(
        string ImagePath,
        string Engine,
        string Classification,
        int Confidence,
        bool WroteSidecar,
        string SidecarPath,
        string Error);

    sealed class DiscordOcrIndexRow
    {
        public string RelativePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string ImageSha256 { get; set; } = string.Empty;
        public string LastEngine { get; set; } = string.Empty;
        public string LastClassification { get; set; } = string.Empty;
        public int LastConfidence { get; set; }
        public bool HasSidecarText { get; set; }
        public string LastError { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }

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
        var ocrIndexPath = Path.Combine(guildDir, DiscordOcrIndexFileName);
        var ocrIndex = LoadDiscordOcrIndex(ocrIndexPath);
        var skipByIndex = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var tesseractPath = Path.Combine(ResolveDiscordToolDirectory("tesseract"), "tesseract.exe");
        var tesseractEnabled = File.Exists(tesseractPath);

        if (tesseractEnabled)
        {
            Parallel.ForEach(mediaFiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, imagePath =>
            {
                if (!full && ShouldSkipOcrFromIndex(guildDir, imagePath, ocrIndex))
                {
                    skipByIndex.TryAdd(imagePath, 0);
                    audit.Add(new DiscordOcrAuditEntry(
                        ImagePath: imagePath,
                        Engine: "index",
                        Classification: "cached_no_text",
                        Confidence: 0,
                        WroteSidecar: false,
                        SidecarPath: imagePath + ".ocr.txt",
                        Error: string.Empty));
                    return;
                }

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

        var hasLlmConfig = !string.IsNullOrWhiteSpace(workspaceConfig.VisionApiUrl)
                           && !string.IsNullOrWhiteSpace(workspaceConfig.VisionModel);
        if (!hasLlmConfig)
        {
            Log.Information("Skipping LLM OCR pass (VisionApiUrl/VisionModel are not configured in .workspace/config.local.json).");
            WriteDiscordOcrAudit(guildOcrOut, audit);
            SaveDiscordOcrIndex(ocrIndexPath, guildDir, mediaFiles, ocrIndex, audit);
            return;
        }

        var resolvedApiKey = ResolveConfigEnvValue(workspaceConfig.VisionApiKey, nameof(WorkspaceConfig.VisionApiKey));
        var visionCandidates = mediaFiles
            .Where(path => !skipByIndex.ContainsKey(path))
            .Where(path => full || !HasOcrSidecarText(path))
            .ToList();
        if (visionCandidates.Count == 0)
        {
            WriteDiscordOcrAudit(guildOcrOut, audit);
            SaveDiscordOcrIndex(ocrIndexPath, guildDir, mediaFiles, ocrIndex, audit);
            return;
        }

        Parallel.ForEach(visionCandidates, new ParallelOptions { MaxDegreeOfParallelism = 2 }, imagePath =>
        {
            var sidecarPath = imagePath + ".ocr.txt";
            var llm = RunLocalVisionOcr(
                imagePath,
                workspaceConfig.VisionApiUrl,
                resolvedApiKey,
                workspaceConfig.VisionModel,
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
        SaveDiscordOcrIndex(ocrIndexPath, guildDir, mediaFiles, ocrIndex, audit);
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
        var confidenceRows = ordered.Where(x => x.Confidence > 0).Select(x => x.Confidence).ToList();
        var successfulRows = ordered.Where(x => string.IsNullOrWhiteSpace(x.Error)).ToList();
        var summary = new
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Total = ordered.Count,
            Tesseract = ordered.Count(x => x.Engine.Equals("tesseract", StringComparison.OrdinalIgnoreCase)),
            LocalLlm = ordered.Count(x => x.Engine.Equals("local-llm", StringComparison.OrdinalIgnoreCase)),
            SidecarsWritten = ordered.Count(x => x.WroteSidecar),
            Errors = ordered.Count(x => !string.IsNullOrWhiteSpace(x.Error)),
            AvgConfidence = confidenceRows.Count == 0 ? 0 : Math.Round(confidenceRows.Average(), 2),
            MaxConfidence = confidenceRows.Count == 0 ? 0 : confidenceRows.Max(),
            MinConfidence = confidenceRows.Count == 0 ? 0 : confidenceRows.Min(),
            SuccessRate = ordered.Count == 0 ? 0 : Math.Round(successfulRows.Count / (double)ordered.Count, 4)
        };
        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            Utf8NoBom);
    }

    static Dictionary<string, DiscordOcrIndexRow> LoadDiscordOcrIndex(string indexPath)
    {
        var map = new Dictionary<string, DiscordOcrIndexRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(indexPath))
        {
            return map;
        }

        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("RelativePath,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Count < 10)
            {
                continue;
            }

            if (!long.TryParse(fields[1], out var size))
            {
                size = 0;
            }

            if (!long.TryParse(fields[2], out var ticks))
            {
                ticks = 0;
            }

            if (!int.TryParse(fields[6], out var confidence))
            {
                confidence = 0;
            }

            var row = new DiscordOcrIndexRow
            {
                RelativePath = fields[0],
                FileSizeBytes = size,
                LastWriteUtcTicks = ticks,
                ImageSha256 = fields[3],
                LastEngine = fields[4],
                LastClassification = fields[5],
                LastConfidence = confidence,
                HasSidecarText = fields[7].Equals("true", StringComparison.OrdinalIgnoreCase),
                LastError = fields[8],
                UpdatedAtUtc = fields[9]
            };

            if (!string.IsNullOrWhiteSpace(row.RelativePath))
            {
                map[row.RelativePath] = row;
            }
        }

        return map;
    }

    static bool ShouldSkipOcrFromIndex(string guildDir, string imagePath, IReadOnlyDictionary<string, DiscordOcrIndexRow> index)
    {
        var relativePath = Path.GetRelativePath(guildDir, imagePath).Replace('\\', '/');
        if (!index.TryGetValue(relativePath, out var row))
        {
            return false;
        }

        var file = new FileInfo(imagePath);
        if (!file.Exists)
        {
            return false;
        }

        if (row.FileSizeBytes != file.Length || row.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks)
        {
            return false;
        }

        if (row.HasSidecarText)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(row.LastError))
        {
            return false;
        }

        return row.LastClassification.Equals("no_text", StringComparison.OrdinalIgnoreCase)
               || row.LastClassification.Equals("skipped_small_image", StringComparison.OrdinalIgnoreCase)
               || row.LastClassification.Equals("skipped", StringComparison.OrdinalIgnoreCase)
               || row.LastClassification.Equals("cached_no_text", StringComparison.OrdinalIgnoreCase);
    }

    void SaveDiscordOcrIndex(
        string indexPath,
        string guildDir,
        IReadOnlyCollection<string> mediaFiles,
        IDictionary<string, DiscordOcrIndexRow> existingIndex,
        IEnumerable<DiscordOcrAuditEntry> auditEntries)
    {
        var now = DateTime.UtcNow.ToString("O");
        var auditByImage = auditEntries
            .GroupBy(entry => entry.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                PickBestAuditEntry,
                StringComparer.OrdinalIgnoreCase);

        foreach (var imagePath in mediaFiles)
        {
            if (!File.Exists(imagePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(guildDir, imagePath).Replace('\\', '/');
            var file = new FileInfo(imagePath);
            if (!existingIndex.TryGetValue(relativePath, out var row))
            {
                row = new DiscordOcrIndexRow { RelativePath = relativePath };
            }

            row.FileSizeBytes = file.Length;
            row.LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
            row.HasSidecarText = HasOcrSidecarText(imagePath);
            row.UpdatedAtUtc = now;

            if (auditByImage.TryGetValue(imagePath, out var audit))
            {
                row.LastEngine = audit.Engine;
                row.LastClassification = audit.Classification;
                row.LastConfidence = audit.Confidence;
                row.LastError = audit.Error ?? string.Empty;
                row.ImageSha256 = ComputeFileSha256Hex(imagePath);
            }
            else if (string.IsNullOrWhiteSpace(row.LastClassification))
            {
                row.LastClassification = row.HasSidecarText ? "full_text" : "unknown";
                row.LastEngine = row.HasSidecarText ? "existing-sidecar" : "none";
            }

            existingIndex[relativePath] = row;
        }

        var lines = new List<string>
        {
            "RelativePath,FileSizeBytes,LastWriteUtcTicks,ImageSha256,LastEngine,LastClassification,LastConfidence,HasSidecarText,LastError,UpdatedAtUtc"
        };
        foreach (var row in existingIndex.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(string.Join(",",
                CsvEscape(row.RelativePath),
                row.FileSizeBytes.ToString(),
                row.LastWriteUtcTicks.ToString(),
                CsvEscape(row.ImageSha256),
                CsvEscape(row.LastEngine),
                CsvEscape(row.LastClassification),
                row.LastConfidence.ToString(),
                row.HasSidecarText ? "true" : "false",
                CsvEscape(row.LastError),
                CsvEscape(row.UpdatedAtUtc)));
        }

        File.WriteAllText(indexPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Utf8NoBom);
    }

    static DiscordOcrAuditEntry PickBestAuditEntry(IEnumerable<DiscordOcrAuditEntry> entries)
    {
        DiscordOcrAuditEntry? best = null;
        var bestScore = int.MinValue;
        foreach (var entry in entries)
        {
            var score = entry.Engine switch
            {
                "local-llm" => 20,
                "tesseract" => 10,
                _ => 0
            };
            if (string.IsNullOrWhiteSpace(entry.Error))
            {
                score += 5;
            }

            if (entry.WroteSidecar)
            {
                score += 3;
            }

            if (entry.Confidence > 0)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        return best ?? entries.First();
    }

    static string ComputeFileSha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string CsvEscape(string value)
    {
        var input = value ?? string.Empty;
        if (!input.Contains(',') && !input.Contains('"') && !input.Contains('\n') && !input.Contains('\r'))
        {
            return input;
        }

        return "\"" + input.Replace("\"", "\"\"") + "\"";
    }

    static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        if (line is null)
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
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
