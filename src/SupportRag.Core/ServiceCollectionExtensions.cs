using Anthropic;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI;
using SupportRag.Core.Data;
using SupportRag.Core.Embeddings;
using SupportRag.Core.Generation;
using SupportRag.Core.Ingest;
using SupportRag.Core.Options;
using SupportRag.Core.Pipeline;
using SupportRag.Core.Retrieval;

namespace SupportRag.Core;

/// <summary>Composition root shared by the API, the eval runner and the tests: one call, every dependency wired.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSupportRag(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.Embedder != EmbedderKind.AzureOpenAI || !string.IsNullOrWhiteSpace(o.AzureOpenAI.Endpoint), "Rag:AzureOpenAI:Endpoint is required for the AzureOpenAI embedder")
            .Validate(o => o.Embedder != EmbedderKind.WorkersAi || !string.IsNullOrWhiteSpace(o.WorkersAi.AccountId), "Rag:WorkersAi:AccountId is required for the WorkersAi embedder")
            .Validate(o => o.Generator != GeneratorKind.WorkersAi || !string.IsNullOrWhiteSpace(o.WorkersAi.AccountId), "Rag:WorkersAi:AccountId is required for the WorkersAi generator")
            .ValidateOnStart();

        // One NpgsqlDataSource for the process, with the pgvector type handler; EF Core and the health check share it.
        services.AddSingleton(sp =>
        {
            var cs = configuration.GetConnectionString("Rag") ?? throw new InvalidOperationException("ConnectionStrings:Rag is not configured.");
            var builder = new NpgsqlDataSourceBuilder(cs);
            builder.UseVector();
            return builder.Build();
        });
        services.AddDbContext<RagDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector()));

        // One named client for everything Workers AI: embeddings and, when configured, generation.
        services.AddHttpClient("workers-ai", (sp, http) =>
        {
            http.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            var token = configuration["CLOUDFLARE_API_TOKEN"];
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            }
        });

        services.AddSingleton<AnthropicClient>(_ => new AnthropicClient()); // ANTHROPIC_API_KEY from the environment
        services.AddEmbedder(configuration);
        services.AddAnswerer();

        services.AddScoped<Retriever>();
        services.AddScoped<SupportAssistant>();
        services.AddScoped<CorpusIngestor>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IServiceCollection AddNightlyIngest(this IServiceCollection services)
    {
        services.AddHostedService<NightlyIngestService>();
        return services;
    }

    /// <summary>The generator is a configuration value, like the embedder: Claude by default, an open-weight model on Workers AI by choice.</summary>
    private static void AddAnswerer(this IServiceCollection services)
    {
        services.AddSingleton<IAnswerer>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<RagOptions>>().Value;
            switch (o.Generator)
            {
                case GeneratorKind.WorkersAi:
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("workers-ai");
                    var completions = new Uri($"accounts/{o.WorkersAi.AccountId}/ai/v1/chat/completions", UriKind.Relative);
                    return new OpenAiCompatibleAnswerer(http, completions, o.WorkersAi.ChatModel, "cloudflare.workers_ai", o.MaxAnswerTokens);
                case GeneratorKind.Anthropic:
                default:
                    return new ClaudeAnswerer(sp.GetRequiredService<AnthropicClient>(), sp.GetRequiredService<IOptions<RagOptions>>());
            }
        });
    }

    private static void AddEmbedder(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IEmbedder>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<RagOptions>>().Value;
            switch (o.Embedder)
            {
                case EmbedderKind.WorkersAi:
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("workers-ai");
                    return new WorkersAiEmbedder(http, o.WorkersAi.AccountId!, o.WorkersAi.Model);
                case EmbedderKind.OpenAI:
                    var openai = new OpenAIClient(configuration["OPENAI_API_KEY"] ?? throw new InvalidOperationException("OPENAI_API_KEY is not set."));
                    IEmbeddingGenerator<string, Embedding<float>> direct = openai.GetEmbeddingClient(o.OpenAI.Model).AsIEmbeddingGenerator();
                    return new OpenAIEmbedder(direct, $"openai/{o.OpenAI.Model}");
                case EmbedderKind.AzureOpenAI:
                default:
                    var endpoint = new Uri(o.AzureOpenAI.Endpoint!);
                    var client = o.AzureOpenAI.UseManagedIdentity
                        ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
                        : new AzureOpenAIClient(endpoint, new AzureKeyCredential(configuration["AZURE_OPENAI_API_KEY"] ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set.")));
                    IEmbeddingGenerator<string, Embedding<float>> generator = client.GetEmbeddingClient(o.AzureOpenAI.Deployment).AsIEmbeddingGenerator();
                    return new OpenAIEmbedder(generator, $"azure-openai/{o.AzureOpenAI.Deployment}");
            }
        });
    }
}
