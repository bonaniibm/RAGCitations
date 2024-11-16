using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using System.Text;
using System.Text.Json;

namespace RAGCitations
{
    public class Search
    {
        private readonly ILogger<Search> _logger;
        private readonly AzureOpenAIClient _openAIClient;
        private readonly SearchClient _searchClient;
        private readonly AzureAISettings _settings;
        private readonly AzureOpenAIChatCompletionService _chatCompletionService;
        private readonly DocumentViewer _documentViewer;

        public Search(
            ILogger<Search> logger,
            AzureOpenAIClient openAIClient,
            SearchClient searchClient,
            AzureAISettings settings,
            AzureOpenAIChatCompletionService chatCompletionService,
            DocumentViewer documentViewer)
        {
            _logger = logger;
            _openAIClient = openAIClient;
            _searchClient = searchClient;
            _settings = settings;
            _chatCompletionService = chatCompletionService;
            _documentViewer = documentViewer;

            _logger.LogInformation($"Initialized with embedding model: {_settings.EmbeddingDeploymentName}");
        }

        [Function("SearchFunc")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
        {
            try
            {
                _logger.LogInformation("Processing search request");

                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var searchRequest = JsonSerializer.Deserialize<SearchRequest>(requestBody);

                if (string.IsNullOrWhiteSpace(searchRequest?.Query))
                {
                    return new BadRequestObjectResult("Query cannot be empty");
                }

                var result = await PerformSemanticSearchAsync(
                    searchRequest.Query,
                    searchRequest.SystemMessage ?? GetDefaultSystemMessage(),
                    searchRequest.KNN ?? 3,
                    searchRequest.MinimumRelevanceScore ?? 0.7);

                return new OkObjectResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in search function: {ex.Message}");
                return new StatusCodeResult(500);
            }
        }

        private async Task<SearchResponse> PerformSemanticSearchAsync(
      string query,
      string systemMessage,
      int knn,
      double minimumRelevanceScore)
        {
            try
            {
                var embeddingClient = _openAIClient.GetEmbeddingClient(_settings.EmbeddingDeploymentName);
                var embedding = embeddingClient.GenerateEmbedding(query).Value;
                var searchOptions = new SearchOptions
                {
                    Size = knn * 2,
                    QueryType = SearchQueryType.Semantic,
                    SemanticSearch = new()
                    {
                        SemanticConfigurationName = "default"
                    },
                    Select = {
        // Base fields
        "Content",
        "DocumentId",
        "DocumentTitle",
        
        // Section fields
        "SectionTitle",
        "SectionNumber",
        "SubsectionTitle",
        "SubsectionNumber",
        "SectionType",
        
        // Metadata fields
        "PageNumber",
        "ChunkIndex",
        "IsTable",
        "FileType",
        "LastModified",
        
        // Structural fields
        "HasStructuredSections",
        "ContentType",
        
        // Title related fields
        "EffectiveTitle",
        "TitleSource",
        "ContextualHeader",
        
        // New semantic fields
        "PrecedingContext",
        "FollowingContext",
        "Keywords",
        "SemanticType",
        "HeadingLevel",
        
        // Scoring fields
        "type_score",
        "heading_score",
        "keyword_score"
    },
                    VectorSearch = new VectorSearchOptions
                    {
                        Queries = { new VectorizedQuery(embedding.ToFloats())
        {
            KNearestNeighborsCount = knn * 2,
            Fields = { "ContentVector" }
        }}
                    }
                };
                SearchResults<SearchDocument> response = await _searchClient.SearchAsync<SearchDocument>(query, searchOptions);
                var results = response.GetResults().ToList();

                if (!results.Any())
                {
                    return new SearchResponse
                    {
                        Answer = "No relevant results found.",
                        RelevantSections = []
                    };
                }

                // Determine document structure and update semantic configuration
                bool hasStructuredDocs = results.Any(r =>
                    r.Document != null &&
                    r.Document.ContainsKey("HasStructuredSections") &&
                    bool.TryParse(r.Document.SafeGetString("HasStructuredSections"), out bool isStructured) &&
                    isStructured);

                // Update semantic configuration based on document type
                searchOptions.SemanticSearch.SemanticConfigurationName = hasStructuredDocs ? "structured" : "unstructured";

                results = results
                    .Where(r => r.Document != null)  // Filter out any null documents
                    .OrderByDescending(r => CalculateRelevanceScore(r, query))
                    .Take(knn)
                    .Where(r => CalculateRelevanceScore(r, query) > minimumRelevanceScore * results[0].Score)
                    .ToList();

                var aggregatedContent = new StringBuilder();
                var relevantSections = new List<DocumentSection>();

                foreach (var result in results)
                {
                    if (result.Document == null) continue;

                    var doc = result.Document;
                    var documentId = doc.SafeGetString("DocumentId");
                    var fileType = doc.SafeGetString("FileType");

                    if (string.IsNullOrEmpty(documentId) || string.IsNullOrEmpty(fileType))
                    {
                        _logger.LogWarning("Missing required document properties: DocumentId or FileType");
                        continue;
                    }

                    var fullDocumentName = $"{documentId}{fileType}";

                    var section = new DocumentSection
                    {
                        // Existing core properties
                        DocumentTitle = doc.SafeGetString("DocumentTitle"),
                        SectionTitle = doc.SafeGetString("SectionTitle"),
                        SectionNumber = doc.SafeGetString("SectionNumber"),
                        SubsectionTitle = doc.SafeGetString("SubsectionTitle"),
                        SubsectionNumber = doc.SafeGetString("SubsectionNumber"),
                        Content = doc.SafeGetString("Content"),
                        PageNumber = doc.SafeGetValue("PageNumber", 1),
                        ContentType = doc.SafeGetString("ContentType"),
                        EffectiveTitle = doc.SafeGetString("EffectiveTitle"),
                        ContextualHeader = doc.SafeGetString("ContextualHeader"),

                        // URL properties
                        DocumentUrl = _documentViewer.GetDocumentUrl(doc.SafeGetString("DocumentId")),
                        ViewerUrl = _documentViewer.GetViewerUrl(
                        doc.SafeGetString("DocumentId"),
                        doc.SafeGetValue("PageNumber", 1),
                        doc),

                        // New enhanced properties
                        PrecedingContext = doc.SafeGetString("PrecedingContext"),
                        FollowingContext = doc.SafeGetString("FollowingContext"),
                        Keywords = doc.SafeGetValue("Keywords", Array.Empty<string>()),
                        SemanticType = doc.SafeGetString("SemanticType"),
                        HeadingLevel = doc.SafeGetValue("HeadingLevel", 0),
                        SemanticScores = new Dictionary<string, double>
                        {
                            ["type_score"] = doc.SafeGetValue("type_score", 1.0),
                            ["heading_score"] = doc.SafeGetValue("heading_score", 1.0),
                            ["keyword_score"] = doc.SafeGetValue("keyword_score", 0.0)
                        },
                        RelevanceScore = doc.SafeGetValue("RelevanceScore", 0.0)
                    };

                    relevantSections.Add(section);

                    // Build context for AI
                    aggregatedContent.AppendLine($"Document: {section.DocumentTitle}");

                    // Build citation based on document type
                    if (!string.IsNullOrEmpty(section.SectionNumber))
                    {
                        // For structured documents
                        aggregatedContent.AppendLine($"Section {section.SectionNumber}: {section.SectionTitle}");
                        if (!string.IsNullOrEmpty(section.SubsectionNumber))
                        {
                            aggregatedContent.AppendLine($"Subsection {section.SubsectionNumber}: {section.SubsectionTitle}");
                        }
                    }
                    else if (!string.IsNullOrEmpty(section.ContextualHeader))
                    {
                        // For unstructured documents with headers
                        aggregatedContent.AppendLine($"Context: {section.ContextualHeader}");
                    }

                    // Check for table content
                    var isTable = doc.SafeGetValue("IsTable", false);
                    if (isTable)
                    {
                        aggregatedContent.AppendLine("Content Type: Table");
                    }

                    aggregatedContent.AppendLine($"Page: {section.PageNumber}");
                    aggregatedContent.AppendLine($"ViewerUrl: {section.ViewerUrl}");

                    // Ensure content is not null before adding
                    if (!string.IsNullOrEmpty(section.Content))
                    {
                        aggregatedContent.AppendLine(section.Content);
                    }

                    aggregatedContent.AppendLine("\n---\n");
                }

                // Create appropriate system message based on document structure
                var adaptedSystemMessage = @"
The user asked: """ + query + @""". 
When referencing information, " + (hasStructuredDocs ? @"
use the following format for structured sections:
[Section X.X](ViewerUrl)
Example: [Section 6.8.1](https://viewer-url?doc=doc.pdf&page=25&section=6.8.1)

Each citation should include:
1. The section number and title
2. A clickable link to the specific section
3. The page number when relevant"
            : @"
use the following format:
[Page X](ViewerUrl)
Example: [Page 25](https://viewer-url?doc=doc.pdf&page=25)

Each citation should include:
1. The page number
2. A clickable link to the specific page
3. Any relevant context or headers") + @"

The following relevant information was found in the documents:";

                var chatHistory = new ChatHistory(systemMessage);
                chatHistory.AddSystemMessage(adaptedSystemMessage);
                chatHistory.AddUserMessage(
                [
                    new TextContent(aggregatedContent.ToString()),
            new TextContent(query)
                ]);

                var answer = await _chatCompletionService.GetChatMessageContentAsync(chatHistory);

                return new SearchResponse
                {
                    Answer = answer.Content,
                    RelevantSections = relevantSections
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in semantic search: {ex.Message}");
                throw;
            }
        }

        private static double CalculateRelevanceScore(SearchResult<SearchDocument> result, string query)
        {
            try
            {
                var doc = result.Document;
                double score = result.Score ?? 0;
                double relevance = score;

                // Add semantic type boost with validation
                var typeScore = doc.SafeGetValue("type_score", 1.0);
                relevance *= typeScore;

                // Add heading level boost with validation
                var headingScore = doc.SafeGetValue("heading_score", 1.0);
                relevance *= headingScore;

                // Add keyword match boost with validation
                var keywordScore = doc.SafeGetValue("keyword_score", 0.0);
                relevance *= (1 + keywordScore);

                // Add context matching with validation
                var precedingContext = doc.SafeGetString("PrecedingContext");
                var followingContext = doc.SafeGetString("FollowingContext");

                var queryTerms = query.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 3)
                    .ToList();

                double contextBoost = CalculateContextBoost(precedingContext, followingContext, queryTerms);
                relevance *= (1 + contextBoost);

                // Add structural boosting
                var sectionType = doc.SafeGetString("SectionType");
                double structuralBoost = CalculateStructuralBoost(sectionType, doc);
                relevance *= structuralBoost;

                return relevance;
            }
            catch (Exception ex)
            {
                return result.Score ?? 0; // Fallback to base score
            }
        }

        private static double CalculateContextBoost(string precedingContext, string followingContext, List<string> queryTerms)
        {
            double boost = 0;
            if (string.IsNullOrEmpty(precedingContext) && string.IsNullOrEmpty(followingContext))
                return boost;

            foreach (var term in queryTerms)
            {
                if (!string.IsNullOrEmpty(precedingContext) &&
                    precedingContext.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    boost += 0.05;
                }
                if (!string.IsNullOrEmpty(followingContext) &&
                    followingContext.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    boost += 0.05;
                }
            }

            return boost;
        }

        private static double CalculateStructuralBoost(string sectionType, SearchDocument doc)
        {
            return sectionType?.ToLower() switch
            {
                "mainsection" => 1.3,
                "subsection" => 1.2,
                "genericheading" => 1.1,
                _ => 1.0
            };
        }


        private static string GetDefaultSystemMessage()
        {
            return @"You are a helpful assistant that provides accurate and concise answers based on the provided document content. 
                    Always reference specific sections when providing information. 
                    If the information is not clear or complete in the provided content, acknowledge this in your response.";
        }
    }

    public class SearchRequest
    {
        public string Query { get; set; }
        public string SystemMessage { get; set; }
        public int? KNN { get; set; }
        public double? MinimumRelevanceScore { get; set; }
    }

    public class SearchResponse
    {
        public string Answer { get; set; }
        public List<DocumentSection> RelevantSections { get; set; }
        public Dictionary<string, double> SearchMetrics { get; set; } = new();
    }

    public class DocumentSection
    {
        public string DocumentTitle { get; set; }
        public string SectionTitle { get; set; }
        public string SectionNumber { get; set; }
        public string SubsectionTitle { get; set; }
        public string SubsectionNumber { get; set; }
        public string Content { get; set; }
        public int PageNumber { get; set; }
        public double RelevanceScore { get; set; }
        public string DocumentUrl { get; set; }
        public string ViewerUrl { get; set; }
        public string ContentType { get; set; }
        public string EffectiveTitle { get; set; }
        public string ContextualHeader { get; set; }

        public string PrecedingContext { get; set; }
        public string FollowingContext { get; set; }
        public string[] Keywords { get; set; }
        public string SemanticType { get; set; }
        public Dictionary<string, double> SemanticScores { get; set; }
        public int HeadingLevel { get; set; }
    }

    public class DocumentViewer
    {
        private readonly string _blobStorageUrl;
        private readonly string _viewerEndpoint;

        public DocumentViewer(string blobStorageUrl, string viewerEndpoint)
        {
            _blobStorageUrl = blobStorageUrl;
            _viewerEndpoint = viewerEndpoint;
        }

        public string GetDocumentUrl(string documentName)
        {
            var cleanDocName = RemoveDuplicateExtension(documentName);
            return $"{_blobStorageUrl}/{cleanDocName}";
        }

        public string GetViewerUrl(string documentName, int pageNumber, SearchDocument doc)
        {
            var cleanDocName = RemoveDuplicateExtension(documentName);
            var encodedDocName = Uri.EscapeDataString(cleanDocName);

            // Extract text to highlight with prioritization
            string textToHighlight = null;
            string highlightSource = null;

            if (!string.IsNullOrEmpty(doc.SafeGetString("EffectiveTitle")))
            {
                textToHighlight = doc.SafeGetString("EffectiveTitle");
                highlightSource = "title";
            }
            if (!string.IsNullOrEmpty(doc.SafeGetString("SectionTitle")))
            {
                textToHighlight = doc.SafeGetString("SectionTitle");
                highlightSource = "section";
            }
            if (!string.IsNullOrEmpty(doc.SafeGetString("ContextualHeader")))
            {
                // If we already have section title and this header contains it, prefer the section title
                if (textToHighlight == null || !doc.SafeGetString("ContextualHeader").Contains(textToHighlight))
                {
                    textToHighlight = doc.SafeGetString("ContextualHeader");
                    highlightSource = "header";
                }
            }

            var queryParams = new List<string>
    {
        $"doc={encodedDocName}",
        $"page={pageNumber}",
        $"contentType={Uri.EscapeDataString(doc.SafeGetString("ContentType"))}",
        $"hasStructured={doc.SafeGetValue("HasStructuredSections", false).ToString().ToLower()}",
        $"effectiveTitle={Uri.EscapeDataString(doc.SafeGetString("EffectiveTitle"))}",
        // Add both highlight text and its source
        $"highlight={Uri.EscapeDataString(textToHighlight ?? "")}",
        $"highlightSource={Uri.EscapeDataString(highlightSource ?? "")}"
    };

            // Add section number if available
            var sectionNumber = doc.SafeGetString("SectionNumber");
            if (!string.IsNullOrEmpty(sectionNumber))
            {
                queryParams.Add($"section={Uri.EscapeDataString(sectionNumber)}");
            }

            // Add contextual header if available and different from highlight text
            var contextualHeader = doc.SafeGetString("ContextualHeader");
            if (!string.IsNullOrEmpty(contextualHeader) && contextualHeader != textToHighlight)
            {
                queryParams.Add($"header={Uri.EscapeDataString(contextualHeader)}");
            }

            queryParams.Add($"titleSource={Uri.EscapeDataString(doc.SafeGetString("TitleSource"))}");

            return $"{_viewerEndpoint}?{string.Join("&", queryParams)}";
        }

        private static string GetLocationReference(SearchDocument doc)
        {
            // For structured documents
            if (!string.IsNullOrEmpty(doc["SectionNumber"].ToString()))
            {
                return doc["SectionNumber"].ToString();
            }
            // For documents with contextual headers
            else if (!string.IsNullOrEmpty(doc["ContextualHeader"].ToString()))
            {
                return Uri.EscapeDataString(doc["ContextualHeader"].ToString());
            }
            return string.Empty;
        }

        private string RemoveDuplicateExtension(string fileName)
        {
            if (fileName.EndsWith(".pdf.pdf", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^4];
            }

            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return fileName + ".pdf";
            }

            return fileName;
        }
       
    }
}