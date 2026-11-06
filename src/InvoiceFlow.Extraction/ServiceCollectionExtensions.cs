using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Extraction.AzureOpenAi;
using InvoiceFlow.Extraction.Replay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlow.Extraction;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureOpenAiInvoiceExtractor(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureOpenAiExtractorOptions>()
            .Bind(configuration.GetSection(AzureOpenAiExtractorOptions.SectionName));
        services.AddSingleton<IInvoiceExtractor, AzureOpenAiInvoiceExtractor>();
        return services;
    }

    /// <summary>
    /// Chooses the extractor from configuration (same logic for API and worker):
    /// <c>Extraction:Provider=replay</c> → dataset replay (Development only);
    /// <c>Extraction:AzureOpenAI:Endpoint</c> set → Azure OpenAI;
    /// otherwise none (documents go to review for manual entry).
    /// </summary>
    public static IServiceCollection AddInvoiceExtraction(this IServiceCollection services, IConfiguration configuration, string contentRoot, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration["Extraction:Provider"] == "replay")
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("Extraction:Provider=replay is only allowed in the Development environment.");
            }

            var dataset = Path.GetFullPath(configuration["Extraction:Replay:DatasetDir"] ?? "../../evals/data", contentRoot);
            return services.AddSingleton<IInvoiceExtractor>(new ReplayInvoiceExtractor(dataset));
        }

        if (!string.IsNullOrWhiteSpace(configuration[$"{AzureOpenAiExtractorOptions.SectionName}:Endpoint"]))
        {
            services.AddAzureOpenAiInvoiceExtractor(configuration);
        }

        return services;
    }
}
