using MemoryMcp.Application.Documents;
using MemoryMcp.Application.Memories;
using MemoryMcp.Application.Spaces;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryMcp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddMemoryMcpApplication(this IServiceCollection services)
    {
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IMemoryService, MemoryService>();
        services.AddScoped<IMemoryGraphService, MemoryGraphService>();
        services.AddScoped<ISpaceService, SpaceService>();
        return services;
    }
}
