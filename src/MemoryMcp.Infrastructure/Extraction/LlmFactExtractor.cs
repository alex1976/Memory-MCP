using System.Text.Json;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace MemoryMcp.Infrastructure.Extraction;

/// <summary>
/// Splits saved content into atomic facts and classifies each fact's relation to the supplied
/// candidate memories, using the OpenAI SDK's chat client with JSON Schema structured output (to
/// avoid brittle text parsing). The same client backs "OpenAI", "AzureOpenAI", and "Gemini" (and any
/// other OpenAI-compatible endpoint, e.g. a self-hosted Ollama/vLLM/LM Studio model) — see
/// DependencyInjection for provider-specific client construction.
/// </summary>
public sealed class LlmFactExtractor(Lazy<ChatClient> client, IOptions<ExtractionOptions> options) : IFactExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly ChatCompletionOptions CompletionOptions = new()
    {
        ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "memory_facts",
            jsonSchema: BinaryData.FromString(FactsJsonSchema),
            jsonSchemaFormatDescription: "Atomic facts extracted from saved content, with relations to existing candidate memories.",
            jsonSchemaIsStrict: true),
    };

    public async Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string content, IReadOnlyList<MemoryCandidateDto> relatedCandidates, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            throw new ExtractorNotConfiguredException("Extraction:ApiKey is not configured.");
        }

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(BuildUserPrompt(content, relatedCandidates)),
        };

        var completion = await client.Value.CompleteChatAsync(messages, CompletionOptions, cancellationToken);
        if (completion.Value.Content.Count == 0)
        {
            throw new InvalidOperationException("Extraction chat completion returned no content (possibly filtered or refused).");
        }

        var json = completion.Value.Content[0].Text;

        var parsed = JsonSerializer.Deserialize<FactsResponse>(json, JsonOptions) ?? new FactsResponse([]);
        return parsed.Facts.Select(ToExtractedFact).ToList();
    }

    private static ExtractedFact ToExtractedFact(FactItem item)
    {
        var relations = item.Relations
            .Where(r => Guid.TryParse(r.ExistingMemoryId, out _) && Enum.TryParse<RelationType>(r.RelationType, ignoreCase: true, out _))
            // The prompt asks for a short note, but nothing in the protocol enforces a length: the
            // clamp to MemoryEdge.NoteMaxLength lives in the entity, so no caller can overflow the column.
            .Select(r => new ExtractedRelation(
                Guid.Parse(r.ExistingMemoryId),
                Enum.Parse<RelationType>(r.RelationType, ignoreCase: true),
                r.Note))
            .ToList();

        return new ExtractedFact(item.Text, item.Category, relations);
    }

    private static string BuildUserPrompt(string content, IReadOnlyList<MemoryCandidateDto> relatedCandidates)
    {
        var candidateLines = relatedCandidates.Count == 0
            ? "(none)"
            : string.Join('\n', relatedCandidates.Select(c => $"- {c.Id}: {c.Text}" + (c.Category is null ? string.Empty : $" [{c.Category}]")));

        return $"""
            Content to extract facts from:
            {content}

            Candidate existing memories:
            {candidateLines}
            """;
    }

    private const string SystemPrompt =
        "You extract atomic, self-contained facts from a piece of saved content. For each fact, compare it " +
        "against the supplied candidate memories and classify its relation to any of them: 'Updates' if the " +
        "fact contradicts or replaces a candidate, 'Extends' if it adds detail to a candidate without " +
        "invalidating it, or 'Derives' if it is inferred by combining two or more candidates. Omit the relation " +
        "entirely for candidates the fact is unrelated to. Only ever reference candidate ids that were supplied " +
        "verbatim; never invent an id. For every relation, set 'note' to one short sentence (at most 300 " +
        "characters, in the language of the content) stating why that relation type applies — what the fact " +
        "contradicts, what detail it adds, or which candidates it was inferred from. The note is read by humans " +
        "auditing why a memory was superseded, so be specific rather than restating the relation type.";

    private const string FactsJsonSchema = """
        {
          "type": "object",
          "properties": {
            "facts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "text": { "type": "string" },
                  "category": { "type": ["string", "null"] },
                  "relations": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "existingMemoryId": { "type": "string" },
                        "relationType": { "type": "string", "enum": ["Updates", "Extends", "Derives"] },
                        "note": { "type": ["string", "null"] }
                      },
                      "required": ["existingMemoryId", "relationType", "note"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["text", "category", "relations"],
                "additionalProperties": false
              }
            }
          },
          "required": ["facts"],
          "additionalProperties": false
        }
        """;

    private sealed record FactsResponse(List<FactItem> Facts);

    private sealed record FactItem(string Text, string? Category, List<RelationItem> Relations);

    private sealed record RelationItem(string ExistingMemoryId, string RelationType, string? Note);
}
