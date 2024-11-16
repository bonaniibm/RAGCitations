# RAGCitations

This project is an Azure Function application which performs basic chat with data and sends back citations and exact links of the references.
## Project Structure

- **AzureFunction/RAGCitations.csproj**: The main project file containing all the dependencies and configurations.
- **host.json**: Configuration file for Azure Functions host.
- **local.settings.json**: Local settings for Azure Functions (not published).

## Dependencies

The project includes the following key dependencies:

- `Azure.AI.DocumentIntelligence`
- `Azure.AI.OpenAI`
- `Azure.Search.Documents`
- `Azure.Storage.Blobs`
- `Azure.Storage.Files.Shares`
- `Azure.Storage.Queues`
- `Microsoft.ApplicationInsights.WorkerService`
- `Microsoft.Azure.Functions.Worker`
- `Microsoft.Azure.Functions.Worker.ApplicationInsights`
- `Microsoft.Azure.Functions.Worker.Extensions.Http`
- `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`
- `Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs`
- `Microsoft.Azure.Functions.Worker.Sdk`
- `Microsoft.Extensions.Azure`
- `Microsoft.Extensions.Configuration.UserSecrets`
- `Microsoft.SemanticKernel`

## Getting Started

1. **Clone the repository**:
    
2. **Open the project in Visual Studio**.

3. **Restore the dependencies**:
    
4. **Run the project**:
    
## Configuration

- **User Secrets**: The project uses user secrets for sensitive configuration. Ensure you have the correct secrets set up in your development environment.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.

## License

This project is licensed under the MIT License.
