using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
  // Models
  private const string DefaultModel = "gpt-4.1-2025-04-14";       // default non-reasoning model
  private const string MiniModel = "gpt-4.1-mini-2025-04-14";    // mini non-reasoning model
  private const string ReasoningModel = "gpt-5-2025-08-07";      // reasoning model
  private const string TtsModel = "gpt-4o-mini-tts";             // text-to-speech model
  private const string FileName = "prompt.md";

  static async Task Main(string[] args)
  {
    try
    {
      string? apiKey = null;
      if (string.IsNullOrWhiteSpace(apiKey))
      {
        Console.WriteLine("Need API key");
        Environment.Exit(1);
      }

      // Flags/options
      bool useReasoning = false;       // -r
      bool useMini = false;            // -m
      bool includeImages = false;      // --images
      double? temperature = null;      // -t
      string? verbosity = null;        // -v
      string? reasoningEffort = null;  // -e
      string? audioInstruction = null; // --audio [string]

      // Parse args
      for (int i = 0; i < args.Length; i++)
      {
        string raw = args[i];
        string arg = raw.ToLowerInvariant();

        switch (arg)
        {
          case "-r": useReasoning = true; break;
          case "-m": useMini = true; break;
          case "--images":
            includeImages = true;
            break;
          case "-t":
            if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -t.");
            if (!double.TryParse(args[++i], out var t))
              throw new ArgumentException("Invalid temperature value after -t.");
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
              audioInstruction = ""; // request audio but no extra string
            break;
          default:
            throw new ArgumentException($"Unknown argument: {raw}");
        }
      }

      // Resolve model
      string model;
      bool isReasoningModel;
      if (useReasoning)
      {
        model = ReasoningModel;
        isReasoningModel = true;
        if (useMini) Console.WriteLine("Note: -r takes precedence; -m ignored.");
      }
      else if (useMini) { model = MiniModel; isReasoningModel = false; }
      else { model = DefaultModel; isReasoningModel = false; }

      // Read prompt
      string prompt = await File.ReadAllTextAsync(FileName);

      // Load PNG images if requested
      var imagesDir = Path.Combine(Directory.GetCurrentDirectory(), "images");
      var imageArchiveDir = Path.Combine(Directory.GetCurrentDirectory(), "image-archive");
      List<(string filePath, string dataUrl)> pngImages = new();

      if (includeImages)
      {
        if (!Directory.Exists(imagesDir))
        {
          Console.WriteLine("Note: --images specified but ./images folder not found; continuing without images.");
        }
        else
        {
          foreach (var file in Directory.EnumerateFiles(imagesDir))
          {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".png")
            {
              byte[] bytes;
              try
              {
                bytes = await File.ReadAllBytesAsync(file);
              }
              catch (Exception ex)
              {
                Console.WriteLine($"Warning: could not read {file}: {ex.Message}");
                continue;
              }
              string b64 = Convert.ToBase64String(bytes);
              string dataUrl = $"data:image/png;base64,{b64}";
              pngImages.Add((file, dataUrl));
            }
          }

          if (pngImages.Count == 0)
          {
            Console.WriteLine("Note: --images specified but no PNG files found in ./images; continuing without images.");
          }
        }
      }

      // Build chat request
      var requestDict = new Dictionary<string, object?>();
      requestDict["model"] = model;

      // Build message content with optional images
      // For OpenAI Chat Completions with image input, content can be an array of parts:
      // - { type: "text", text: "..." }
      // - { type: "image_url", image_url: { url: "data:image/png;base64,..." } }
      var contentParts = new List<Dictionary<string, object?>>();
      contentParts.Add(new Dictionary<string, object?> {
        { "type", "text" },
        { "text", prompt }
      });

      if (pngImages.Count > 0)
      {
        foreach (var (_, dataUrl) in pngImages)
        {
          contentParts.Add(new Dictionary<string, object?> {
            { "type", "image_url" },
            { "image_url", new Dictionary<string, object?> {
                { "url", dataUrl }
              }
            }
          });
        }
      }

      requestDict["messages"] = new object[]
      {
        new Dictionary<string, object?> {
          { "role", "user" },
          { "content", contentParts }
        }
      };

      if (isReasoningModel)
      {
        if (temperature.HasValue) Console.WriteLine("Note: -t ignored for gpt-5.");
        if (!string.IsNullOrWhiteSpace(verbosity)) requestDict["verbosity"] = verbosity;
        if (!string.IsNullOrWhiteSpace(reasoningEffort)) requestDict["reasoning_effort"] = reasoningEffort;
      }
      else
      {
        requestDict["temperature"] = temperature ?? 0.1;
      }

      // Send chat request
      var (responseText, usage, statusCode, rawResponse) = await PostChatAsync(apiKey, requestDict);

      Console.WriteLine("=== CHAT RESPONSE ===");
      Console.WriteLine($"Status: {(int)statusCode} ({statusCode})");

      if ((int)statusCode >= 200 && (int)statusCode < 300)
      {
        using var writer = File.AppendText(FileName);
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("---");
        await writer.WriteLineAsync((responseText ?? "").Trim());
        await writer.WriteLineAsync($"`{model}`,`{{tokens: {usage.total}/{usage.prompt}/{usage.completion}}}`");
        await writer.WriteLineAsync("---");
        Console.WriteLine("Response appended to prompt.md.");

        // If images were included and call succeeded, archive them
        if (pngImages.Count > 0)
        {
          Directory.CreateDirectory(imageArchiveDir);
          foreach (var (filePath, _) in pngImages)
          {
            try
            {
              var destPath = Path.Combine(imageArchiveDir, Path.GetFileName(filePath));
              // If a file with the same name exists, append a timestamp to avoid collision
              if (File.Exists(destPath))
              {
                var name = Path.GetFileNameWithoutExtension(filePath);
                var ext = Path.GetExtension(filePath);
                var stamped = $"{name}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}";
                destPath = Path.Combine(imageArchiveDir, stamped);
              }
              File.Move(filePath, destPath);
            }
            catch (Exception ex)
            {
              Console.WriteLine($"Warning: failed to archive {filePath}: {ex.Message}");
            }
          }
          Console.WriteLine("Images moved to image-archive.");
        }
      }

      // If audio requested, send TTS request
      if (audioInstruction != null && !string.IsNullOrWhiteSpace(responseText))
      {
        var audioBytes = await PostTtsAsync(apiKey, responseText, "alloy");
        if (audioBytes != null)
        {
          string audioFile = "output.wav";
          await File.WriteAllBytesAsync(audioFile, audioBytes);
          Console.WriteLine($"Audio written to {audioFile}");
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error: {ex.Message}");
      Environment.Exit(1);
    }
  }

  private static async Task<(string response, (int total, int prompt, int completion) usage, System.Net.HttpStatusCode statusCode, string raw)>
    PostChatAsync(string apiKey, Dictionary<string, object?> requestDict)
  {
    string requestBody = JsonSerializer.Serialize(requestDict, new JsonSerializerOptions
    {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      PropertyNamingPolicy = null
    });

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
    var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
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
              // If the API returns content as array of parts, concatenate any text parts.
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

  private static async Task<byte[]?> PostTtsAsync(string apiKey, string text, string voice)
  {
    var requestDict = new Dictionary<string, object?>
    {
      { "model", TtsModel },
      { "voice", voice },  // alloy | verse | sage
      { "input", text },
      { "format", "wav" },
      { "speed_instructions", "fast" }
    };

    string requestBody = JsonSerializer.Serialize(requestDict);

    using var client = new HttpClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
    var response = await client.PostAsync("https://api.openai.com/v1/audio/speech", content);

    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadAsByteArrayAsync();
  }
}