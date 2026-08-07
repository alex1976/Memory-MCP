using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Embeddings;
using MemoryMcp.Infrastructure.Persistence;
using MemoryMcp.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
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

        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));

        // Lazy: constructing the client validates provider config (e.g. requires a non-empty API key).
        // Deferring that until an embedding is actually requested keeps tools that don't need
        // embeddings (listMemories, listDocuments, ...) working even when no provider is configured.
        services.AddSingleton(sp => new Lazy<EmbeddingClient>(() => CreateEmbeddingClient(sp)));
        services.AddScoped<IEmbeddingProvider, OpenAiCompatibleEmbeddingProvider>();

        return services;
    }

    private static EmbeddingClient CreateEmbeddingClient(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

        if (options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = options.Endpoint
                ?? throw new InvalidOperationException("Embeddings:Endpoint is required when Embeddings:Provider is 'AzureOpenAI'.");

            return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(options.ApiKey))
                .GetEmbeddingClient(options.Model);
        }

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey))
            .GetEmbeddingClient(options.Model);
    }
}
