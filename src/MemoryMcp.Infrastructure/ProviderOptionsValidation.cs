using MemoryMcp.Infrastructure.Embeddings;
using MemoryMcp.Infrastructure.Extraction;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Infrastructure;

/// <summary>
/// Shared startup validation for the two provider-backed option sections (Embeddings, Extraction), which
/// have the same Provider/Endpoint/Model shape. Deliberately does *not* require an ApiKey: leaving a
/// provider unconfigured is a supported mode (add_memory then falls back to a single un-extracted memory),
/// so only internally inconsistent configuration is rejected.
/// </summary>
internal static class ProviderOptionsValidation
{
    private static readonly string[] KnownProviders = ["OpenAI", "AzureOpenAI", "Gemini"];

    public static List<string> Validate(string section, string provider, string? endpoint, string model)
    {
        var failures = new List<string>();

        // Client construction switches on this string and falls through to plain OpenAI for anything
        // unrecognised, so without this check a typo ("Gemeni") silently ships the configured key to
        // api.openai.com instead of the intended provider.
        if (!KnownProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"{section}:Provider '{provider}' is not supported. Use one of: {string.Join(", ", KnownProviders)}.");
        }

        if (provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(endpoint))
        {
            failures.Add($"{section}:Endpoint is required when {section}:Provider is 'AzureOpenAI'.");
        }

        if (!string.IsNullOrWhiteSpace(endpoint) && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            failures.Add($"{section}:Endpoint '{endpoint}' is not an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            failures.Add($"{section}:Model must not be empty.");
        }

        return failures;
    }

    public static ValidateOptionsResult ToResult(List<string> failures) =>
        failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
}

internal sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        var failures = ProviderOptionsValidation.Validate(
            EmbeddingOptions.SectionName, options.Provider, options.Endpoint, options.Model);

        // The halfvec column has a fixed width baked into the schema, so a provider configured to emit a
        // different one would either fail on insert or (as happened before this check existed) leave the
        // space with mixed-width embeddings whose similarity scores are silently meaningless.
        if (options.Dimensions != VectorSettings.Dimensions)
        {
            failures.Add(
                $"{EmbeddingOptions.SectionName}:Dimensions is {options.Dimensions} but the schema stores " +
                $"halfvec({VectorSettings.Dimensions}). Changing the width requires a migration and a re-embed " +
                "of every stored memory — see VectorSettings.");
        }

        return ProviderOptionsValidation.ToResult(failures);
    }
}

internal sealed class ExtractionOptionsValidator : IValidateOptions<ExtractionOptions>
{
    public ValidateOptionsResult Validate(string? name, ExtractionOptions options) =>
        ProviderOptionsValidation.ToResult(ProviderOptionsValidation.Validate(
            ExtractionOptions.SectionName, options.Provider, options.Endpoint, options.Model));
}
