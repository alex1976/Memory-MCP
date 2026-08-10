using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Embeddings;
using MemoryMcp.Infrastructure.Extraction;
using MemoryMcp.Infrastructure.Persistence;
using MemoryMcp.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace MemoryMcp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMemoryMcpInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Default' configuration value.");

        services.AddDbContext<MemoryDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<ISpaceRepository, SpaceRepository>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IMemoryRepository, MemoryRepository>();
        services.AddScoped<IMemoryEdgeRepository, MemoryEdgeRepository>();

        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));

        // Lazy: constructing the client validates provider config (e.g. requires a non-empty API key).
        // Deferring that until an embedding is actually requested keeps tools that don't need
        // embeddings (listMemories, listDocuments, ...) working even when no provider is configured.
        services.AddSingleton(sp => new Lazy<EmbeddingClient>(() => CreateEmbeddingClient(sp)));
        services.AddScoped<IEmbeddingProvider, OpenAiCompatibleEmbeddingProvider>();

        services.Configure<ExtractionOptions>(configuration.GetSection(ExtractionOptions.SectionName));

        // Same lazy pattern: LlmFactExtractor itself checks ApiKey before touching the client, so this
        // is only ever forced when extraction is actually configured and invoked.
        services.AddSingleton(sp => new Lazy<ChatClient>(() => CreateChatClient(sp)));
        services.AddScoped<IFactExtractor, LlmFactExtractor>();

        return services;
    }

    // Gemini's OpenAI-compatible API (https://ai.google.dev/gemini-api/docs/openai) exposes /embeddings
    // under this base URL, so the OpenAI SDK's client works against it unmodified once pointed here.
    private const string GeminiOpenAiCompatibleEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    private static EmbeddingClient CreateEmbeddingClient(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

        if (options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(options.Endpoint))
            {
                throw new InvalidOperationException("Embeddings:Endpoint is required when Embeddings:Provider is 'AzureOpenAI'.");
            }

            return new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey))
                .GetEmbeddingClient(options.Model);
        }

        if (options.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = string.IsNullOrEmpty(options.Endpoint) ? GeminiOpenAiCompatibleEndpoint : options.Endpoint;
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions).GetEmbeddingClient(options.Model);
        }

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey))
            .GetEmbeddingClient(options.Model);
    }

    private static ChatClient CreateChatClient(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<ExtractionOptions>>().Value;

        if (options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(options.Endpoint))
            {
                throw new InvalidOperationException("Extraction:Endpoint is required when Extraction:Provider is 'AzureOpenAI'.");
            }

            return new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey))
                .GetChatClient(options.Model);
        }

        if (options.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = string.IsNullOrEmpty(options.Endpoint) ? GeminiOpenAiCompatibleEndpoint : options.Endpoint;
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions).GetChatClient(options.Model);
        }

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey))
            .GetChatClient(options.Model);
    }
}
