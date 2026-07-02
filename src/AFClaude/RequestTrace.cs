using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Chat;

namespace AFClaude;

// Opt-in wire-level tracing (set AFClaude__TraceDir=<directory>): per /v1/messages
// call, dumps the raw Anthropic request, the translated Azure request, Azure's
// response, and the Anthropic-shaped reply. Exists to diagnose translation defects
// against real deployments, where the only other evidence is model behaviour.
// Trace files contain full conversation content — point it at a private directory.
internal sealed class RequestTrace(string? dir)
{
    private int _seq;

    public bool Enabled => dir is not null;

    public int Next() => Interlocked.Increment(ref _seq);

    public void Write(int seq, string name, string content)
    {
        if (dir is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{seq:D3}-{name}"), content);
        }
        catch
        {
            // Tracing must never break a request.
        }
    }

    public void WriteJson(int seq, string name, object payload)
        => Write(seq, name, SafeJson(payload));

    public void WriteAzureRequest(int seq, IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options)
        => WriteJson(seq, "azure-request.json", new
        {
            tools = options.Tools.Select(t => t.FunctionName).ToArray(),
            max_output_tokens = options.MaxOutputTokenCount,
            temperature = options.Temperature,
            messages = messages.Select(ModelJson).ToArray(),
        });

    public void WriteAzureResponse(int seq, ChatCompletion completion)
        => WriteJson(seq, "azure-response.json", ModelJson(completion));

    // The OpenAI SDK types serialize to their true wire format via ModelReaderWriter.
    private static object ModelJson(object model)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(ModelReaderWriter.Write(model).ToString());
        }
        catch (Exception ex)
        {
            return $"<unserializable {model.GetType().Name}: {ex.Message}>";
        }
    }

    private static string SafeJson(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"<serialization failed: {ex.Message}>";
        }
    }
}
