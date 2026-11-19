using System.Text.Json;
using GitBench.Services.Ai;

namespace GitBench.Services.Editor.Ai;

/// <summary>
/// One tool-less, structured-output request to whichever editor AI backend is configured (<c>Ai:Editor:Backend</c>).
/// Used by features that need a single JSON answer rather than the editor's tool loop, like AI pull request review.
/// </summary>
public sealed record StructuredAiRequest(string Feature, string System, string User,
    IReadOnlyDictionary<string, JsonElement> Schema, int? UserId = null, int? ProjectId = null, int MaxTokens = 16000);

/// <summary>
/// A backend that can answer a <see cref="StructuredAiRequest"/>: no tools, no workspace, nothing it can change. Implemented by
/// <see cref="ApiEditorBackend"/> (through the same <see cref="IEditorChatModel"/> seam the editor uses) and
/// <see cref="ClaudeCodeEditorBackend"/> (headless, every tool disallowed, empty working directory).
/// Never throws for refusals, truncation or malformed JSON: check <see cref="AiJsonResult{T}.Ok"/>.
/// </summary>
public interface IStructuredAiBackend
{
    Task<AiJsonResult<T>> CompleteJsonAsync<T>(StructuredAiRequest request, CancellationToken ct) where T : class;
}

/// <summary>Picks the editor AI backend from <c>Ai:Editor:Backend</c>; shared by the editor and every feature built on its backends.</summary>
public static class EditorAiBackendSelector
{
    /// <summary>The configured (or, for Auto, detected) backend that is usable right now; null when none is.</summary>
    public static async Task<IEditorAiBackend?> SelectAsync(IEnumerable<IEditorAiBackend> backends, EditorAiBackendKind configured, CancellationToken ct = default)
    {
        var list = backends.ToList();
        var order = configured switch
        {
            EditorAiBackendKind.Api => [EditorAiBackendKind.Api],
            EditorAiBackendKind.ClaudeCode => [EditorAiBackendKind.ClaudeCode],
            _ => new[] { EditorAiBackendKind.ClaudeCode, EditorAiBackendKind.Api },
        };
        foreach (var kind in order)
            if (list.FirstOrDefault(b => b.Kind == kind) is { } b && await b.IsAvailableAsync(ct)) return b;
        return null;
    }
}

public static class AiJsonText
{
    /// <summary>
    /// The JSON object in a model reply: the text between the first '{' and the last '}', so a Markdown fence or a sentence
    /// around it doesn't matter. Text without an object comes back unchanged (and fails to parse, which is reported).
    /// </summary>
    public static string ExtractObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
