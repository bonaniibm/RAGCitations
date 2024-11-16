using Azure.AI.OpenAI;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Azure.Storage.Blobs;

namespace RAGCitations
{
    public class Program
    {
        public static async Task Main()
        {
            var host = new HostBuilder()
                .ConfigureFunctionsWebApplication()
                .ConfigureServices((context, services) =>
                {
                    services.AddApplicationInsightsTelemetryWorkerService();
                    services.ConfigureFunctionsApplicationInsights();

                    // Add Azure Settings
                    var aiSettings = new AzureAISettings
                    {
                        EmbeddingDeploymentName = Environment.GetEnvironmentVariable("EmbeddingDeploymentName") ?? "text-embedding-ada-002",
                        ChatCompletionDeploymentName = Environment.GetEnvironmentVariable("ChatCompletionDeploymentName") ?? "gpt-4"
                    };
                    services.AddSingleton(aiSettings);

                    // Add Azure OpenAI client
                    services.AddSingleton(sp =>
                    {
                        var openAIEndpoint = Environment.GetEnvironmentVariable("OpenAIEndpoint");
                        var openAIKey = Environment.GetEnvironmentVariable("OpenAIKey");
                        return new AzureOpenAIClient(new Uri(openAIEndpoint), new AzureKeyCredential(openAIKey));
                    });

                    // Add Search client
                    services.AddSingleton<SearchClient>(sp =>
                    {
                        var searchEndpoint = Environment.GetEnvironmentVariable("SearchServiceEndpoint");
                        var searchKey = Environment.GetEnvironmentVariable("SearchApiKey");
                        var indexName = Environment.GetEnvironmentVariable("SearchIndexName");
                        var searchClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchKey));
                        return searchClient.GetSearchClient(indexName);
                    });

                    // Add Chat Completion service
                    services.AddSingleton<AzureOpenAIChatCompletionService>(sp =>
                    {
                        var settings = sp.GetRequiredService<AzureAISettings>();
                        var openAIEndpoint = Environment.GetEnvironmentVariable("OpenAIEndpoint");
                        var openAIKey = Environment.GetEnvironmentVariable("OpenAIKey");
                        return new AzureOpenAIChatCompletionService(
                            deploymentName: settings.ChatCompletionDeploymentName,
                            endpoint: openAIEndpoint,
                            apiKey: openAIKey,
                            modelId: settings.ChatCompletionDeploymentName);
                    });

                    // Add Blob Storage client
                    services.AddSingleton<BlobServiceClient>(sp =>
                    {
                        var storageConnectionString = Environment.GetEnvironmentVariable("AzureBlobConnectionString");
                        return new BlobServiceClient(storageConnectionString);
                    });

                    // Add Document Viewer
                    services.AddSingleton<DocumentViewer>(sp =>
                    {
                        var functionBaseUrl = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") ?? "localhost:7074";
                        var blobStorageUrl = Environment.GetEnvironmentVariable("BlobStorageBaseUrl");
                        var viewerEndpoint = functionBaseUrl.StartsWith("http")
                            ? $"{functionBaseUrl}/api/ViewDocument"
                            : $"http://{functionBaseUrl}/api/ViewDocument";

                        return new DocumentViewer(blobStorageUrl, viewerEndpoint);
                    });

                    // Add Document Viewer Function
                    services.AddSingleton<DocumentViewerFunction>();

                    // Add Search Function
                    services.AddSingleton<Search>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}