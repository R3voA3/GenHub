using GenHub.Core.Interfaces.Services;
using GenHub.Core.Options;
using GenHub.Features.Tools.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenHub.Infrastructure.DependencyInjection;

/// <summary>
/// Dependency injection module for UploadThing services.
/// </summary>
public static class UploadThingModule
{
    /// <summary>
    /// Registers UploadThing services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddUploadThingServices(this IServiceCollection services)
    {
        services.AddOptions<UploadThingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                // Try configuration first (appsettings.json, environment variables via IConfiguration)
                options.ApiToken = configuration["UPLOADTHING_TOKEN"] ?? 
                                 configuration["GENHUB_UPLOADTHING_TOKEN"];
            });

        services.AddHttpClient<IUploadThingService, UploadThingService>();

        return services;
    }
}
