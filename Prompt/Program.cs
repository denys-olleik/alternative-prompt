using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

class Program
{
  // Constants (preserve names and values)
  private const string DefaultModel = "gpt-4.1-2025-04-14";
  private const string MiniModel = "gpt-4.1-mini-2025-04-14";
  private const string ReasoningModel = "gpt-5-2025-08-07";
  private const string TtsModel = "gpt-4o-mini-tts";
  private const string FileName = "prompt.md";

  private const string AudioArchiveDirName = "audio-archive";
  private const string CurrentAudioFile = "output.wav";
  private const string AudioPrefix = "output";

  // Supported models and their endpoint type
  // true = /v1/responses, false = /v1/chat/completions
  private static readonly Dictionary<string, bool> SupportedModels = new(StringComparer.OrdinalIgnoreCase)
  {
    { DefaultModel, false },
    { MiniModel, false },
    { ReasoningModel, true },
    { TtsModel, false }, // TTS model is handled separately, but included for completeness
  };

  private static readonly JsonSerializerOptions JsonOpts = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = null
  };

  private static readonly HttpClient Http = new()
  {
    Timeout = TimeSpan.FromMinutes(5)
  };

  private record Options(
    string Model,
    bool IncludeImages,
    bool DoArchive,
    double? Temperature,
    string? Verbosity,
    string? ReasoningEffort,
    string? AudioInstruction
  );

  static async Task Main(string[] args)
  {
    try
    {
      string? apiKey = Environment.GetEnvironmentVariable("openai");
      if (string.IsNullOrWhiteSpace(apiKey))
      {
        Console.WriteLine("Need API key");
        Environment.Exit(1);
      }
      Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

      var opts = ParseArgs(args);

      // === Model validation ===
      if (!SupportedModels.ContainsKey(opts.Model))
      {
        Console.WriteLine("Error: model not supported.");
        Console.WriteLine("Supported models:");
        foreach (var model in SupportedModels.Keys)
          Console.WriteLine($"  - {model}");
        Environment.Exit(1);
      }

      // Determine endpoint and model type
      bool isResponsesApi = SupportedModels[opts.Model];

      // Read prompt
      if (!File.Exists(FileName))
      {
        Console.WriteLine($"Error: {FileName} not found in {Directory.GetCurrentDirectory()}");
        Environment.Exit(1);
      }
      string originalPrompt = await File.ReadAllTextAsync(FileName);

      // Prepare dirs
      var cwd = Directory.GetCurrentDirectory();
      var imagesDir = Path.Combine(cwd, "images");
      var imageArchiveDir = Path.Combine(cwd, "image-archive");
      var archiveDir = Path.Combine(cwd, "archive");
      Directory.CreateDirectory(archiveDir);

      // Compose the effective prompt (no file injection)
      string effectivePrompt = originalPrompt;

      // Load images (optional)
      var pngImages = opts.IncludeImages
        ? await LoadPngDataUrlsAsync(imagesDir)
        : new List<(string filePath, string dataUrl)>();

      // Build request
      var requestDict = isResponsesApi
        ? BuildResponsesRequest(opts.Model, effectivePrompt, pngImages, opts)
        : BuildChatRequest(opts.Model, effectivePrompt, pngImages, opts);

      // Send request to correct endpoint
      var (responseText, usage, statusCode, rawResponse) = await PostLlmAsync(requestDict, isResponsesApi);

      Console.WriteLine("=== CHAT RESPONSE ===");
      Console.WriteLine($"Status: {(int)statusCode} ({statusCode})");

      if ((int)statusCode >= 200 && (int)statusCode < 300)
      {
        // Append response and possibly archive
        var lastAppendedBlock = await AppendResponseBlockAsync(FileName, responseText, opts.Model, usage);

        if (pngImages.Count > 0)
        {
          await ArchiveImagesAsync(pngImages, imageArchiveDir);
          Console.WriteLine("Images moved to image-archive.");
        }

        if (opts.DoArchive)
        {
          await ArchiveConversationAsync(FileName, archiveDir, "prompt", lastAppendedBlock);
        }

        // Audio handling
        if (opts.AudioInstruction != null && !string.IsNullOrWhiteSpace(responseText))
        {
          try { await PrepareAudioArchiveOfCurrentAsync(); }
          catch (Exception ex) { Console.WriteLine($"Warning: audio pre-archive failed: {ex.Message}. Continuing to generate new audio."); }

          var audioBytes = await PostTtsAsync(responseText, "alloy");
          if (audioBytes != null)
          {
            await File.WriteAllBytesAsync(CurrentAudioFile, audioBytes);
            Console.WriteLine($"Audio written to {CurrentAudioFile}");
          }
          else
          {
            Console.WriteLine("Warning: TTS request failed; no audio generated.");
          }
        }
      }
      else
      {
        Console.WriteLine("Request failed; not appending or archiving.");
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error: {ex.Message}");
      Environment.Exit(1);
    }
  }

  // Parse CLI args: model is required first argument, then flags
  private static Options ParseArgs(string[] args)
  {
    if (args.Length == 0)
      throw new ArgumentException("First argument must be the model name (e.g., gpt-5-2025-08-07).");

    string model = args[0];
    bool includeImages = false;
    bool doArchive = false;
    double? temperature = null;
    string? verbosity = null;
    string? reasoningEffort = null;
    string? audioInstruction = null;

    for (int i = 1; i < args.Length; i++)
    {
      string raw = args[i];
      string arg = raw.ToLowerInvariant();

      switch (arg)
      {
        case "--images": includeImages = true; break;
        case "--archive": doArchive = true; break;
        case "-t":
          if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -t.");
          if (!double.TryParse(args[++i], out var t)) throw new ArgumentException("Invalid temperature value after -t.");
          temperature = t;
          break;
        case "-v":
          if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -v.");
          var v = args[++i].ToLowerInvariant();
          if (v != "low" && v != "medium" && v != "high")
            throw new ArgumentException("Invalid -v; allowed: low | medium | high.");
          verbosity = v;
          break;
        case "-e":
          if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -e.");
          var e = args[++i].ToLowerInvariant();
          var allowedE = new HashSet<string> { "minimal", "low", "medium", "high" };
          if (!allowedE.Contains(e))
            throw new ArgumentException("Invalid -e; allowed: minimal | low | medium | high.");
          reasoningEffort = e;
          break;
        case "--audio":
          if (audioInstruction != null) throw new ArgumentException("Duplicate --audio.");
          if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
            audioInstruction = args[++i];
          else
            audioInstruction = "";
          break;
        default:
          throw new ArgumentException($"Unknown argument: {raw}");
      }
    }

    return new Options(model, includeImages, doArchive, temperature, verbosity, reasoningEffort, audioInstruction);
  }

  private static async Task<List<(string filePath, string dataUrl)>> LoadPngDataUrlsAsync(string imagesDir)
  {
    var results = new List<(string filePath, string dataUrl)>();
    if (!Directory.Exists(imagesDir))
    {
      Console.WriteLine("Note: --images specified but ./images folder not found; continuing without images.");
      return results;
    }

    foreach (var file in Directory.EnumerateFiles(imagesDir))
    {
      var ext = Path.GetExtension(file).ToLowerInvariant();
      if (ext != ".png") continue;

      try
      {
        var bytes = await File.ReadAllBytesAsync(file);
        string dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        results.Add((file, dataUrl));
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Warning: could not read {file}: {ex.Message}");
      }
    }

    if (results.Count == 0)
      Console.WriteLine("Note: --images specified but no PNG files found in ./images; continuing without images.");

    return results;
  }

  // Build request for /v1/chat/completions endpoint (legacy models)
  private static Dictionary<string, object?> BuildChatRequest(
    string model,
    string prompt,
    List<(string filePath, string dataUrl)> pngImages,
    Options opts)
  {
    var contentParts = new List<Dictionary<string, object?>>()
    {
      new() { { "type", "text" }, { "text", prompt } }
    };

    if (pngImages.Count > 0)
    {
      foreach (var (_, dataUrl) in pngImages)
      {
        contentParts.Add(new Dictionary<string, object?>
        {
          { "type", "image_url" },
          { "image_url", new Dictionary<string, object?> { { "url", dataUrl } } }
        });
      }
    }

    var requestDict = new Dictionary<string, object?>
    {
      { "model", model },
      {
        "messages",
        new object[]
        {
          new Dictionary<string, object?>
          {
            { "role", "user" },
            { "content", contentParts }
          }
        }
      },
      { "temperature", opts.Temperature ?? 0.1 }
    };

    return requestDict;
  }

  // Build request for /v1/responses endpoint (modern models)
  private static Dictionary<string, object?> BuildResponsesRequest(
    string model,
    string prompt,
    List<(string filePath, string dataUrl)> pngImages,
    Options opts)
  {
    var contentParts = new List<Dictionary<string, object?>>()
    {
      new() { { "type", "text" }, { "text", prompt } }
    };

    if (pngImages.Count > 0)
    {
      foreach (var (_, dataUrl) in pngImages)
      {
        contentParts.Add(new Dictionary<string, object?>
        {
          { "type", "image_url" },
          { "image_url", new Dictionary<string, object?> { { "url", dataUrl } } }
        });
      }
    }

    var requestDict = new Dictionary<string, object?>
    {
      { "model", model },
      {
        "messages",
        new object[]
        {
          new Dictionary<string, object?>
          {
            { "role", "user" },
            { "content", contentParts }
          }
        }
      }
    };

    if (!string.IsNullOrWhiteSpace(opts.Verbosity)) requestDict["verbosity"] = opts.Verbosity;
    if (!string.IsNullOrWhiteSpace(opts.ReasoningEffort)) requestDict["reasoning_effort"] = opts.ReasoningEffort;
    // /v1/responses may not support temperature for all models; omit if not needed

    return requestDict;
  }

  // Send request to correct endpoint and parse response
  private static async Task<(string response, (int total, int prompt, int completion) usage, System.Net.HttpStatusCode statusCode, string raw)>
    PostLlmAsync(Dictionary<string, object?> requestDict, bool isResponsesApi)
  {
    string endpoint = isResponsesApi
      ? "https://api.openai.com/v1/responses"
      : "https://api.openai.com/v1/chat/completions";

    string requestBody = JsonSerializer.Serialize(requestDict, JsonOpts);
    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
    var response = await Http.PostAsync(endpoint, content);
    string raw = await response.Content.ReadAsStringAsync();

    string choiceText = "";
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;

    if (response.IsSuccessStatusCode)
    {
      try
      {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
          var msg = choices[0].GetProperty("message");
          if (msg.TryGetProperty("content", out var contentElem))
          {
            if (contentElem.ValueKind == JsonValueKind.String)
            {
              choiceText = contentElem.GetString() ?? "";
            }
            else if (contentElem.ValueKind == JsonValueKind.Array)
            {
              var sb = new StringBuilder();
              foreach (var part in contentElem.EnumerateArray())
              {
                if (part.TryGetProperty("type", out var t) && t.GetString() == "text")
                {
                  if (part.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
                }
              }
              if (sb.Length > 0) choiceText = sb.ToString();
            }
          }
        }

        if (root.TryGetProperty("usage", out var usageElem))
        {
          if (usageElem.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
          if (usageElem.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
          if (usageElem.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
        }
      }
      catch { }
    }

    return (choiceText, (totalTokens, promptTokens, completionTokens), response.StatusCode, raw);
  }

  private static async Task<string> AppendResponseBlockAsync(
    string filePath, string responseText, string model, (int total, int prompt, int completion) usage)
  {
    var trimmed = (responseText ?? "").Trim();
    var block = new StringBuilder()
      .AppendLine()
      .AppendLine("---")
      .AppendLine(trimmed)
      .AppendLine($"`{model}`,`{{tokens: {usage.total}/{usage.prompt}/{usage.completion}}}`")
      .AppendLine("---")
      .ToString();

    using (var writer = File.AppendText(filePath))
    {
      await writer.WriteAsync(block);
    }
    Console.WriteLine("Response appended to prompt.md.");
    return block;
  }

  private static async Task ArchiveImagesAsync(List<(string filePath, string dataUrl)> pngImages, string imageArchiveDir)
  {
    Directory.CreateDirectory(imageArchiveDir);
    foreach (var (filePath, _) in pngImages)
    {
      try
      {
        var destPath = Path.Combine(imageArchiveDir, Path.GetFileName(filePath));
        await MoveWithTimestampIfExistsAsync(filePath, destPath);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Warning: failed to archive {filePath}: {ex.Message}");
      }
    }
  }

  private static async Task ArchiveConversationAsync(string promptPath, string archiveDir, string prefix, string lastAppendedBlock)
  {
    try
    {
      // Read full file (request + just-appended response)
      string fullAfterAppend = await File.ReadAllTextAsync(promptPath, Encoding.UTF8);

      // Determine next archive name
      int nextNumber = await GetNextArchiveNumberByNumericSuffixAsync(archiveDir, prefix);
      string archivePath = Path.Combine(archiveDir, $"{prefix}{nextNumber}.md");

      // Guard against overwrite
      int guard = nextNumber;
      while (File.Exists(archivePath))
      {
        guard++;
        archivePath = Path.Combine(archiveDir, $"{prefix}{guard}.md");
      }

      await File.WriteAllTextAsync(archivePath, fullAfterAppend, Encoding.UTF8);
      Console.WriteLine($"Archived to {archivePath}");

      // Reset prompt.md to only the last response block
      await File.WriteAllTextAsync(promptPath, lastAppendedBlock, Encoding.UTF8);
      Console.WriteLine("prompt.md reset to last response block.");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Warning: archiving failed: {ex.Message}. Leaving prompt.md as-is.");
    }
  }

  // Shared numeric suffix scanner for text archives
  private static async Task<int> GetNextArchiveNumberByNumericSuffixAsync(string archiveDir, string prefix)
  {
    return await Task.Run(() =>
    {
      Directory.CreateDirectory(archiveDir);

      var numbers = Directory.EnumerateFiles(archiveDir, $"{prefix}*.md")
        .Select(path => Path.GetFileName(path)!)
        .Select(name => TryParseNumericSuffix(name, prefix, out var n) ? n : (int?)null)
        .Where(n => n.HasValue)
        .Select(n => n!.Value)
        .ToList();

      return numbers.Count == 0 ? 1 : numbers.Max() + 1;
    });
  }

  private static bool TryParseNumericSuffix(string fileName, string prefix, out int number)
  {
    number = 0;
    if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    var core = Path.GetFileNameWithoutExtension(fileName);
    if (core.Length <= prefix.Length) return false;
    var suffix = core.Substring(prefix.Length);
    return int.TryParse(suffix, out number);
  }

  // Audio archive mover for current wav
  private static async Task PrepareAudioArchiveOfCurrentAsync()
  {
    if (!File.Exists(CurrentAudioFile)) return;

    var cwd = Directory.GetCurrentDirectory();
    var audioArchiveDir = Path.Combine(cwd, AudioArchiveDirName);
    Directory.CreateDirectory(audioArchiveDir);

    int nextN = await GetNextAudioArchiveNumberAsync(audioArchiveDir, AudioPrefix);
    string archivedPath = Path.Combine(audioArchiveDir, $"{AudioPrefix}{nextN}.wav");

    File.Move(Path.Combine(cwd, CurrentAudioFile), archivedPath);
    Console.WriteLine($"Archived existing {CurrentAudioFile} to {archivedPath}");
  }

  private static async Task<int> GetNextAudioArchiveNumberAsync(string audioArchiveDir, string prefix)
  {
    return await Task.Run(() =>
    {
      Directory.CreateDirectory(audioArchiveDir);
      var regex = new Regex($"^{Regex.Escape(prefix)}(\\d+)\\.wav$", RegexOptions.IgnoreCase);

      var candidates = Directory.EnumerateFiles(audioArchiveDir, $"{prefix}*.wav")
        .Select(p => Path.GetFileName(p)!)
        .Select(name => (name, m: regex.Match(name)))
        .Where(x => x.m.Success)
        .Select(x => int.Parse(x.m.Groups[1].Value))
        .ToList();

      return candidates.Count == 0 ? 1 : candidates.Max() + 1;
    });
  }

  private static async Task MoveWithTimestampIfExistsAsync(string srcPath, string destPath)
  {
    if (!File.Exists(destPath))
    {
      File.Move(srcPath, destPath);
      return;
    }

    var dir = Path.GetDirectoryName(destPath)!;
    var name = Path.GetFileNameWithoutExtension(destPath);
    var ext = Path.GetExtension(destPath);
    var stamped = Path.Combine(dir, $"{name}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    File.Move(srcPath, stamped);
    await Task.CompletedTask;
  }

  private static async Task<byte[]?> PostTtsAsync(string text, string voice)
  {
    var requestDict = new Dictionary<string, object?>
    {
      { "model", TtsModel },
      { "voice", voice },  // alloy | verse | sage
      { "input", text },
      { "format", "wav" }
    };

    string requestBody = JsonSerializer.Serialize(requestDict, JsonOpts);
    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
    var response = await Http.PostAsync("https://api.openai.com/v1/audio/speech", content);

    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadAsByteArrayAsync();
  }
}