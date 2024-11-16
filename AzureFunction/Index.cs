using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using Azure.AI.DocumentIntelligence;

namespace RAGCitations
{
    public class Index
    {
        private readonly ILogger<Index> _logger;
        private readonly DocumentIntelligenceClient _documentAnalysisClient;
        private readonly AzureOpenAIClient _openAIClient;
        private readonly EmbeddingClient _embeddingClient;
        private readonly SearchIndexClient _searchIndexClient;
        private readonly SearchClient _searchClient;
        private readonly string _indexName;

        private static readonly HashSet<string> StopWords =
        [
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from",
            "has", "he", "in", "is", "it", "its", "of", "on", "that", "the",
            "to", "was", "were", "will", "with", "the", "this", "but", "they",
            "have", "had", "what", "when", "where", "who", "which", "why", "how"
        ];

        public Index(ILogger<Index> logger)
        {
            _logger = logger;

            string formRecognizerEndpoint = Environment.GetEnvironmentVariable("FormRecognizerEndpoint");
            string formRecognizerKey = Environment.GetEnvironmentVariable("FormRecognizerKey");
            _documentAnalysisClient = new DocumentIntelligenceClient(new Uri(formRecognizerEndpoint), new AzureKeyCredential(formRecognizerKey));

            string openAIEndpoint = Environment.GetEnvironmentVariable("OpenAIEndpoint");
            string openAIKey = Environment.GetEnvironmentVariable("OpenAIKey");
            string embeddingDeploymentName = Environment.GetEnvironmentVariable("EmbeddingDeploymentName");
            _openAIClient = new AzureOpenAIClient(new Uri(openAIEndpoint), new AzureKeyCredential(openAIKey));
            _embeddingClient = _openAIClient.GetEmbeddingClient(embeddingDeploymentName);

            string searchServiceEndpoint = Environment.GetEnvironmentVariable("SearchServiceEndpoint");
            string searchApiKey = Environment.GetEnvironmentVariable("SearchApiKey");
            _indexName = Environment.GetEnvironmentVariable("SearchIndexName");

            _searchIndexClient = new SearchIndexClient(new Uri(searchServiceEndpoint), new AzureKeyCredential(searchApiKey));
            _searchClient = _searchIndexClient.GetSearchClient(_indexName);
        }

        [Function(nameof(Index))]
        public async Task Run([BlobTrigger("documentcont/{name}", Connection = "AzureBlobConnectionString")] Stream stream, string name, Uri uri)
        {
            _logger.LogInformation($"Starting to process document: {name}");
            _logger.LogInformation($"Stream length: {stream.Length} bytes");

            try
            {
                _logger.LogInformation("Creating or updating search index...");
                await CreateIndexIfNotExistsAsync();

                _logger.LogInformation("Beginning document content extraction...");
                var analysisResult = await ExtractContentFromDocumentAsync(uri, name);
                _logger.LogInformation($"Document analyzed. Found {analysisResult.Pages.Count} pages and {analysisResult.Tables.Count} tables");

                _logger.LogInformation("Starting document chunking...");
                var chunks = ChunkDocument(analysisResult);
                _logger.LogInformation($"Document chunked into {chunks.Count} parts");

                _logger.LogInformation("Beginning indexing process...");
                await IndexChunksAsync(name, chunks, Path.GetExtension(name).ToLowerInvariant());
                _logger.LogInformation($"Successfully processed and indexed document: {name}");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Azure service error processing document {name}. Status: {ex.Status}, Error Code: {ex.ErrorCode}");
                _logger.LogError($"Error details: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing document {name}: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
                throw;
            }
        }

        private async Task CreateIndexIfNotExistsAsync()
        {
            try
            {
                string vectorSearchProfileName = "vectorConfig";
                string vectorSearchHnswConfig = "hnsw";
                int modelDimensions = 1536;

                SearchIndex searchIndex = new(_indexName)
                {
                    Fields =
                    {
                        // Key and core fields
                        new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                        new SearchableField("Content") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SimpleField("DocumentId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
        
                        // Title fields
                        new SearchableField("DocumentTitle") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SearchableField("SectionTitle") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SimpleField("SectionNumber", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SearchableField("SubsectionTitle") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SimpleField("SubsectionNumber", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("SectionType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
        
                        // Metadata fields
                        new SimpleField("ChunkIndex", SearchFieldDataType.Int32) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("PageNumber", SearchFieldDataType.Int32) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("IsTable", SearchFieldDataType.Boolean) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("FileType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("LastModified", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
        
                        // Vector field
                        new SearchField("ContentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                        {
                            IsSearchable = true,
                            VectorSearchDimensions = modelDimensions,
                            VectorSearchProfileName = vectorSearchProfileName
                        },
        
                        // Structural fields
                        new SimpleField("HasStructuredSections", SearchFieldDataType.Boolean) { IsFilterable = true },
                        new SearchableField("ContentType") { IsFilterable = true, IsFacetable = true },
                        new SearchableField("EffectiveTitle") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SimpleField("TitleSource", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SearchableField("ContextualHeader") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
        
                        // New semantic fields
                        new SearchableField("PrecedingContext") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SearchableField("FollowingContext") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                        new SimpleField("Keywords", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("SemanticType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("HeadingLevel", SearchFieldDataType.Int32) { IsFilterable = true, IsFacetable = true },
        
                        // Semantic scores
                        new SimpleField("type_score", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
                        new SimpleField("heading_score", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
                        new SimpleField("keyword_score", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true }
                    },
                    VectorSearch = new VectorSearch
                    {
                        Algorithms =
                        {
                            new HnswAlgorithmConfiguration(vectorSearchHnswConfig)
                            {
                                Parameters = new HnswParameters
                                {
                                    Metric = VectorSearchAlgorithmMetric.Cosine,
                                    M = 4,
                                    EfConstruction = 400,
                                    EfSearch = 500
                                }
                            }
                        },
                        Profiles =
                        {
                            new VectorSearchProfile(vectorSearchProfileName, vectorSearchHnswConfig)
                        }
                    },
                    SemanticSearch = new SemanticSearch
                    {
                        Configurations =
    {
                        new SemanticConfiguration("default", new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField("DocumentTitle"),
                            ContentFields =
                            {
                                new SemanticField("Content"),
                                new SemanticField("EffectiveTitle"),
                                new SemanticField("SectionTitle"),
                                new SemanticField("SubsectionTitle"),
                                new SemanticField("PrecedingContext"),
                                new SemanticField("FollowingContext"),
                                new SemanticField("ContextualHeader")
                            },
                            KeywordsFields =
                            {
                             new SemanticField("ContentType"),
                             new SemanticField("FileType"),
                             new SemanticField("SectionNumber"),
                             new SemanticField("TitleSource"),
                             new SemanticField("Keywords"),
                             new SemanticField("SemanticType")
                            }
                        })
                    }
                    },
                    ScoringProfiles =
                    {
                        new ScoringProfile("hybridScoring")
                        {
                            TextWeights = new TextWeights(new Dictionary<string, double>
                            {
                                { "Content", 1.0 },
                                { "SectionTitle", 1.5 },
                                { "DocumentTitle", 2.0 }
                            }),
                            Functions =
                            {
                                new MagnitudeScoringFunction(
                                    fieldName: "ChunkIndex",
                                    boost: 2.0,
                                    parameters: new MagnitudeScoringParameters(
                                        boostingRangeStart: 0,
                                        boostingRangeEnd: 200))
                                {
                                   Interpolation = ScoringFunctionInterpolation.Logarithmic
                                },

                                new FreshnessScoringFunction(
                                    fieldName: "LastModified",
                                    boost: 1.5,
                                    parameters: new FreshnessScoringParameters(
                                        boostingDuration: TimeSpan.FromDays(365)))
                                {
                                    Interpolation = ScoringFunctionInterpolation.Logarithmic
                                }
                            },
                            FunctionAggregation = ScoringFunctionAggregation.Sum
                        }
                    }
                };
                await _searchIndexClient.CreateOrUpdateIndexAsync(searchIndex);
                _logger.LogInformation($"Created or updated index {_indexName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"An error occurred while creating the index: {ex.Message}");
                throw;
            }
        }

        private async Task<AnalyzeResult> ExtractContentFromDocumentAsync(Uri blobUri, string fileName)
        {
            try
            {
                _logger.LogInformation($"Starting content extraction for file: {fileName}");
                List<DocumentAnalysisFeature> features = [new DocumentAnalysisFeature(DocumentAnalysisFeature.OcrHighResolution.ToString()),
                new DocumentAnalysisFeature(DocumentAnalysisFeature.StyleFont.ToString())];
                var content = new AnalyzeDocumentContent()
                {
                    UrlSource = blobUri
                };
                Operation<AnalyzeResult> operation = await _documentAnalysisClient.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content, null, null, null, features, outputContentFormat: ContentFormat.Markdown);
                AnalyzeResult result = operation.Value;
                _logger.LogInformation($"Content extraction completed. Pages found: {result.Pages.Count}");

                if (result.Pages.Count == 0)
                {
                    _logger.LogWarning("No pages were extracted from the document");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during content extraction: {ex.Message}");
                _logger.LogError($"File name: {fileName}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        private List<DocumentChunk> ChunkDocument(AnalyzeResult analysisResult)
        {
            var chunks = new List<DocumentChunk>();
            var currentChunk = new StringBuilder();
            var currentSection = new DocumentSection();
            var currentSubsection = new DocumentSection();
            int currentPageNumber = 1;
            const int maxTokensPerChunk = 4000;
            const int overlapTokens = 400;
            const int contextWindowSize = 200;
            int currentTokenCount = 0;
            int overlapTokenCount = 0;
            bool isInTable = false;
            int chunkStartPage = 1;
            var pageHeaders = new Dictionary<int, string>();
            var allLines = new List<DocumentLine>(); // Store all lines for context

            try
            {
                foreach (var page in analysisResult.Pages)
                {
                    currentPageNumber = page.PageNumber;
                    _logger.LogInformation($"Processing page {currentPageNumber}");

                    // Store all lines for the page
                    allLines.AddRange(page.Lines);

                    // Process page headers
                    var pageHeaderLines = page.Lines
                        .Where(line =>
                        {
                            if (line.Polygon.Count >= 2)
                            {
                                return line.Polygon[1] < page.Height * 0.15;
                            }
                            return false;
                        })
                        .Select(line => line.Content)
                        .Where(content => !string.IsNullOrWhiteSpace(content))
                        .ToList();

                    if (pageHeaderLines.Any())
                    {
                        pageHeaders[currentPageNumber] = string.Join(" ", pageHeaderLines);
                    }

                    // Process formal tables
                    if (analysisResult.Tables != null && analysisResult.Tables.Any())
                    {
                        foreach (var table in analysisResult.Tables)
                        {
                            if (table.BoundingRegions.Any(r => r.PageNumber == currentPageNumber))
                            {
                                _logger.LogInformation($"Processing formal table on page {currentPageNumber}");
                                AddChunkIfNotEmpty();
                                isInTable = true;
                                chunkStartPage = currentPageNumber;
                                currentChunk.AppendLine("<table>");

                                var rowGroups = table.Cells
                                    .GroupBy(cell => cell.RowIndex)
                                    .OrderBy(group => group.Key);

                                foreach (var row in rowGroups)
                                {
                                    currentChunk.AppendLine("<tr>");
                                    foreach (var cell in row.OrderBy(c => c.ColumnIndex))
                                    {
                                        string cellType = cell.Kind == DocumentTableCellKind.ColumnHeader ? "th" : "td";
                                        currentChunk.AppendLine($"<{cellType}>{cell.Content}</{cellType}>");
                                    }
                                    currentChunk.AppendLine("</tr>");
                                }

                                currentChunk.AppendLine("</table>");
                                AddChunkIfNotEmpty();
                                isInTable = false;
                            }
                        }
                    }

                    // Process text content
                    _logger.LogInformation($"Processing text content on page {currentPageNumber}. Line count: {page.Lines.Count}");

                    for (int lineIndex = 0; lineIndex < page.Lines.Count; lineIndex++)
                    {
                        var line = page.Lines[lineIndex];
                        string lineContent = line.Content;

                        if (ShouldSkipLine(lineContent))
                        {
                            continue;
                        }

                        // Table content check
                        if (!isInTable && IsLikelyTableContent(lineContent))
                        {
                            _logger.LogInformation($"Processing table-like content on page {currentPageNumber}");
                            AddChunkIfNotEmpty(line, lineIndex);
                            isInTable = true;
                            chunkStartPage = currentPageNumber;
                            ProcessTableLikeContent(lineContent, currentChunk);
                            AddChunkIfNotEmpty(line, lineIndex);
                            isInTable = false;
                            continue;
                        }

                        // Enhanced structural elements check
                        var semanticType = DetermineSemanticType(line, pageHeaderLines);
                        var headingLevel = DetermineHeadingLevel(line, page.Height.Value);

                        if (IsSectionHeader(lineContent))
                        {
                            AddChunkIfNotEmpty(line, lineIndex);
                            currentSection = new DocumentSection
                            {
                                Title = CleanHeaderText(lineContent),
                                Number = ExtractSectionNumber(lineContent),
                                Type = SectionType.MainSection,
                                PageNumber = currentPageNumber
                            };
                            chunkStartPage = currentPageNumber;
                        }
                        else if (IsSubsectionHeader(lineContent))
                        {
                            AddChunkIfNotEmpty(line, lineIndex);
                            currentSubsection = new DocumentSection
                            {
                                Title = CleanHeaderText(lineContent),
                                Number = ExtractSectionNumber(lineContent),
                                Type = SectionType.Subsection,
                                PageNumber = currentPageNumber
                            };
                            chunkStartPage = currentPageNumber;
                        }
                        else if (IsHeading(lineContent, line, page.Height.Value))
                        {
                            AddChunkIfNotEmpty(line, lineIndex);
                            currentSection = new DocumentSection
                            {
                                Title = CleanHeaderText(lineContent),
                                Type = SectionType.GenericHeading,
                                PageNumber = currentPageNumber
                            };
                            chunkStartPage = currentPageNumber;
                        }

                        // Handle chunk size limits
                        int lineTokenCount = EstimateTokenCount(lineContent);
                        if (currentTokenCount + lineTokenCount > maxTokensPerChunk)
                        {
                            AddChunkIfNotEmpty(line, lineIndex);
                            var overlapLines = currentChunk.ToString().Split('\n')
                                .Where(l => !ShouldSkipLine(l))
                                .TakeLast(overlapTokenCount / 3)
                                .ToList();
                            currentChunk.Clear().AppendJoin('\n', overlapLines).AppendLine();
                            currentTokenCount = EstimateTokenCount(currentChunk.ToString());
                            chunkStartPage = currentPageNumber;
                        }

                        currentChunk.AppendLine(lineContent);
                        currentTokenCount += lineTokenCount;
                        overlapTokenCount = Math.Min(overlapTokenCount + lineTokenCount, overlapTokens);
                    }
                }

                AddChunkIfNotEmpty(null, -1);
                EnrichChunksWithEmbeddings(chunks);
                return chunks;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during document chunking: {ex.Message}");
                _logger.LogError($"Current page being processed: {currentPageNumber}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }

            void AddChunkIfNotEmpty(DocumentLine currentLine = null, int currentLineIndex = -1)
            {
                if (currentChunk.Length > 0)
                {
                    string cleanedContent = CleanContent(currentChunk.ToString());
                    if (!string.IsNullOrWhiteSpace(cleanedContent))
                    {
                        // Safely get the contextual header
                        pageHeaders.TryGetValue(chunkStartPage, out var contextualHeader);

                        // Get preceding and following context
                        string precedingContext = GetPrecedingContext(allLines, currentLineIndex, contextWindowSize);
                        string followingContext = GetFollowingContext(allLines, currentLineIndex, contextWindowSize);

                        // Safely get header words
                        List<string> headerWords = contextualHeader != null
                            ? contextualHeader.Split(' ').ToList()
                            : new List<string>();

                        // Safely get the page
                        var page = analysisResult.Pages.FirstOrDefault(p => p.PageNumber == chunkStartPage);
                        double pageHeight = page?.Height.Value ?? 0;

                        // Create enhanced chunk
                        var chunk = new DocumentChunk
                        {
                            // Existing properties
                            Content = cleanedContent,
                            Section = currentSection,
                            Subsection = currentSubsection,
                            PageNumber = chunkStartPage,
                            IsTable = isInTable,
                            ContextualHeader = contextualHeader,

                            // New enhanced properties
                            PrecedingContext = precedingContext,
                            FollowingContext = followingContext,
                            Keywords = ExtractKeywords(cleanedContent),
                            SemanticType = DetermineSemanticType(currentLine, headerWords),
                            HeadingLevel = (currentLine != null && pageHeight > 0) ? DetermineHeadingLevel(currentLine, pageHeight) : 0,
                            //SemanticScores = new Dictionary<string, double>()
                        };

                        chunks.Add(chunk);
                    }
                    currentChunk.Clear();
                    currentTokenCount = 0;
                    overlapTokenCount = 0;
                }
            }

        }

        private string GetPrecedingContext(List<DocumentLine> lines, int currentIndex, int contextSize)
        {
            if (currentIndex < 0) return string.Empty;

            var context = new StringBuilder();
            int startIdx = Math.Max(0, currentIndex - 5); // Look back 5 lines

            for (int i = startIdx; i < currentIndex; i++)
            {
                context.AppendLine(lines[i].Content);
            }

            return context.ToString().Trim();
        }

        private string GetFollowingContext(List<DocumentLine> lines, int currentIndex, int contextSize)
        {
            if (currentIndex < 0) return string.Empty;

            var context = new StringBuilder();
            int endIdx = Math.Min(lines.Count, currentIndex + 6); // Look ahead 5 lines

            for (int i = currentIndex + 1; i < endIdx; i++)
            {
                context.AppendLine(lines[i].Content);
            }

            return context.ToString().Trim();
        }

        private string[] ExtractKeywords(string content)
        {
            // Simple keyword extraction based on frequency and position
            return content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLowerInvariant())
                .Where(w => !StopWords.Contains(w))
                .GroupBy(w => w)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key)
                .ToArray();
        }

        private string DetermineSemanticType(DocumentLine line, List<string> headers)
        {
            if (line == null) return "body-text";

            if (headers.Contains(line.Content))
                return "header";

            if (IsLikelyTableContent(line.Content))
                return "table";

            if (Regex.IsMatch(line.Content, @"^\d+\.\s"))
                return "numbered-list";

            if (Regex.IsMatch(line.Content, @"^[A-Z][\w\s]+:"))
                return "definition";

            return "body-text";
        }

        private int DetermineHeadingLevel(DocumentLine line, double pageHeight)
        {
            if (line == null) return 0;

            if (line.Polygon.Count >= 2)
            {
                var yPosition = line.Polygon[1];
                var relativePosition = yPosition / pageHeight;

                if (relativePosition < 0.15) return 1;  // Top of page
                if (relativePosition < 0.3) return 2;   // Upper third
                if (IsSectionHeader(line.Content)) return 1;
                if (IsSubsectionHeader(line.Content)) return 2;
            }

            return 0;
        }
        

        private void ProcessTableLikeContent(string content, StringBuilder currentChunk)
        {
            try
            {
                currentChunk.AppendLine("<table>");
                var lines = content.Split('\n');
                bool isFirstRow = true;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cells = line.Split('|', '\t', ',')
                        .Select(cell => cell.Trim())
                        .Where(cell => !string.IsNullOrEmpty(cell))
                        .ToList();

                    if (cells.Any())
                    {
                        currentChunk.AppendLine("<tr>");
                        foreach (var cell in cells)
                        {
                            // Use th for first row, assuming it might be headers
                            string cellType = isFirstRow ? "th" : "td";
                            currentChunk.AppendLine($"<{cellType}>{cell}</{cellType}>");
                        }
                        currentChunk.AppendLine("</tr>");
                    }
                    isFirstRow = false;
                }
                currentChunk.AppendLine("</table>");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error processing table-like content: {ex.Message}");
                // Fallback to treating it as normal text
                currentChunk.AppendLine(content);
            }
        }
        private static string DetermineTitleSourceType(DocumentChunk chunk)
        {
            if (!string.IsNullOrEmpty(chunk.Section?.Title))
                return "Section";
            if (!string.IsNullOrEmpty(chunk.ContextualHeader))
                return "Header";
            if (!string.IsNullOrEmpty(GetEffectiveTitle(chunk)))
                return "Content";
            return "None";
        }
        private static bool IsHeading(string line, DocumentLine documentLine, double pageHeight)
        {
            if (documentLine?.Polygon == null || documentLine.Polygon.Count < 2)
                return false;

          
            var topY = documentLine.Polygon[1];


            double relativePosition = topY / pageHeight;

            bool isNearTop = relativePosition < 0.3;
            bool hasHeadingFormat = line.Length < 100 &&
                                   !line.EndsWith(".") &&
                                   !line.Contains("  ") &&
                                   char.IsUpper(line[0]);

            return isNearTop && hasHeadingFormat;
        }

        private static string CleanHeaderText(string header)
        {
            return Regex.Replace(header, @"^\d+(\.\d+)*\s*", "").Trim();
        }
        private static bool ShouldSkipLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;

            string trimmedLine = line.Trim();
            return trimmedLine.StartsWith("<!-- PageNumber=") ||
                   trimmedLine.StartsWith("<!-- PageBreak") ||
                   trimmedLine.StartsWith("<!-- PageHeader=");
        }

        private static string CleanContent(string content)
        {
            // Remove page metadata and clean up the content
            var lines = content.Split('\n')
                .Where(line => !ShouldSkipLine(line))
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));

            return string.Join("\n", lines).Trim();
        }

        private async Task IndexChunksAsync(string documentName, List<DocumentChunk> chunks, string fileExtension)
        {
            var indexBatch = new List<SearchDocument>();
            int processedChunks = 0;

            try
            {
                _logger.LogInformation($"Starting indexing for document: {documentName} with {chunks.Count} chunks");

                for (int i = 0; i < chunks.Count; i++)
                {
                    try
                    {
                        var chunk = chunks[i];
                        _logger.LogInformation($"Processing chunk {i + 1}/{chunks.Count}");

                        // Validate required fields
                        if (string.IsNullOrWhiteSpace(chunk.Content))
                        {
                            _logger.LogWarning($"Skipping chunk {i} due to empty content");
                            continue;
                        }

                        // Generate embedding
                        var embeddingResponse = GetEmbeddings(_embeddingClient, chunk.Content);

                        // Create search document with validation
                        var searchDocument = new SearchDocument
                        {
                            // Required fields with validation
                            ["Id"] = $"{Path.GetFileNameWithoutExtension(documentName)}-chunk-{i}",
                            ["Content"] = chunk.Content,
                            ["DocumentId"] = documentName,
                            ["DocumentTitle"] = Path.GetFileNameWithoutExtension(documentName),

                            // Optional fields with safe defaults
                            ["SectionTitle"] = chunk.Section?.Title ?? "",
                            ["SectionNumber"] = chunk.Section?.Number ?? "",
                            ["SectionType"] = chunk.Section?.Type.ToString() ?? SectionType.None.ToString(),
                            ["SectionTitle"] = chunk.Subsection?.Title ?? "",
                            ["SectionNumber"] = chunk.Subsection?.Number ?? "",
                            ["ChunkIndex"] = i,
                            ["PageNumber"] = chunk.PageNumber,
                            ["ContextualHeader"] = chunk.ContextualHeader ?? "",
                            ["IsTable"] = chunk.IsTable,
                            ["FileType"] = fileExtension,
                            ["LastModified"] = DateTimeOffset.UtcNow,
                            ["ContentVector"] = embeddingResponse,
                            ["HasStructuredSections"] = !string.IsNullOrEmpty(chunk.Section?.Number),
                            ["ContentType"] = DetermineContentType(chunk),
                            ["EffectiveTitle"] = GetEffectiveTitle(chunk),
                            ["TitleSource"] = DetermineTitleSourceType(chunk),

                            // Enhanced fields with validation
                            ["PrecedingContext"] = chunk.PrecedingContext ?? "",
                            ["FollowingContext"] = chunk.FollowingContext ?? "",
                            ["Keywords"] = chunk.Keywords ?? Array.Empty<string>(),
                            ["SemanticType"] = chunk.SemanticType ?? "body-text",
                            ["HeadingLevel"] = chunk.HeadingLevel,

                            // Semantic scores with safe defaults
                            ["type_score"] = chunk.SemanticScores.GetValueOrDefault("type_score", 1.0),
                            ["heading_score"] = chunk.SemanticScores.GetValueOrDefault("heading_score", 1.0),
                            ["keyword_score"] = chunk.SemanticScores.GetValueOrDefault("keyword_score", 0.0)
                        };

                        // Validate document before adding to batch
                        if (ValidateSearchDocument(searchDocument))
                        {
                            indexBatch.Add(searchDocument);
                            processedChunks++;

                            if (indexBatch.Count >= 1000 || i == chunks.Count - 1)
                            {
                                _logger.LogInformation($"Indexing batch of {indexBatch.Count} documents");
                                await _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(indexBatch));
                                _logger.LogInformation($"Successfully indexed batch. Progress: {processedChunks}/{chunks.Count} chunks");
                                indexBatch.Clear();
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Skipping invalid document for chunk {i}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error processing chunk {i}: {ex.Message}");
                        throw;
                    }
                }

                _logger.LogInformation($"Completed indexing all {processedChunks} chunks for document: {documentName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during indexing: {ex.Message}");
                _logger.LogError($"Document: {documentName}, Processed chunks: {processedChunks}/{chunks.Count}");
                throw;
            }
        }

        private bool ValidateSearchDocument(SearchDocument doc)
        {
            // Required fields validation
            var requiredFields = new[] { "Id", "Content", "DocumentId", "DocumentTitle" };
            foreach (var field in requiredFields)
            {
                if (!doc.ContainsKey(field) || string.IsNullOrWhiteSpace(doc[field]?.ToString()))
                {
                    _logger.LogError($"Missing required field: {field}");
                    return false;
                }
            }

            // Validate ContentVector
            if (!doc.ContainsKey("ContentVector") || doc["ContentVector"] == null)
            {
                _logger.LogError("Missing ContentVector");
                return false;
            }

            return true;
        }

        private static bool IsSectionHeader(string line)
        {
            return Regex.IsMatch(line.Trim(), @"^\d+(\.\d+)?\s+[A-Z]");
        }

        private static bool IsSubsectionHeader(string line)
        {
            return Regex.IsMatch(line.Trim(), @"^\d+(\.\d+){2,}\s+[A-Z]");
        }

        private static int EstimateTokenCount(string text)
        {
            return (int)Math.Ceiling(text.Length / 3.0);
        }

        private static string ExtractSectionNumber(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return "";
            }

            var match = Regex.Match(section, @"^(\d+(\.\d+)*)");
            return match.Success ? match.Groups[1].Value : "";
        }

        public static ReadOnlyMemory<float> GetEmbeddings(EmbeddingClient embeddingClient, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Input cannot be empty or whitespace.", nameof(input));
            }

            var chunks = SplitLargeChunk(new DocumentChunk { Content = input });
            var embeddings = new List<ReadOnlyMemory<float>>();

            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Content))
                {
                    try
                    {
                        OpenAIEmbedding embedding = embeddingClient.GenerateEmbedding(chunk.Content);
                        embeddings.Add(embedding.ToFloats());
                    }
                    catch (RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("maximum context length"))
                    {
                        // If we still hit the token limit, split the chunk further and try again
                        var subChunks = SplitLargeChunk(chunk);
                        foreach (var subChunk in subChunks)
                        {
                            if (!string.IsNullOrWhiteSpace(subChunk.Content))
                            {
                                embeddings.Add(GetEmbeddings(embeddingClient, subChunk.Content));
                            }
                        }
                    }
                }
            }

            // If there are no embeddings, throw an exception or handle it appropriately
            if (embeddings.Count == 0)
            {
                throw new InvalidOperationException("No valid content to generate embeddings.");
            }

            // If there's only one embedding, return it directly
            if (embeddings.Count == 1)
                return embeddings[0];

            // If there are multiple embeddings, average them
            return AverageEmbeddings(embeddings);
        }

        private static List<DocumentChunk> SplitLargeChunk(DocumentChunk chunk)
        {
            var splitChunks = new List<DocumentChunk>();
            var words = chunk.Content.Split(' ');
            var currentChunk = new StringBuilder();
            var currentTokenCount = 0;
            var subChunkIndex = 0;
            const int MaxTokensPerChunk = 2000;

            foreach (var word in words)
            {
                var wordTokenCount = EstimateTokenCount(word);
                if (currentTokenCount + wordTokenCount > MaxTokensPerChunk)
                {
                    splitChunks.Add(new DocumentChunk
                    {
                        Content = currentChunk.ToString().Trim(),
                        Section = chunk.Section,
                        PageNumber = chunk.PageNumber,
                        SubChunkIndex = subChunkIndex++,
                        IsTable = chunk.IsTable,
                        ContextualHeader = chunk.ContextualHeader
                    });

                    currentChunk.Clear();
                    currentTokenCount = 0;
                }

                currentChunk.Append(word + " ");
                currentTokenCount += wordTokenCount;
            }

            if (currentChunk.Length > 0)
            {
                splitChunks.Add(new DocumentChunk
                {
                    Content = currentChunk.ToString().Trim(),
                    Section = chunk.Section,
                    PageNumber = chunk.PageNumber,
                    SubChunkIndex = subChunkIndex,
                    IsTable = chunk.IsTable,
                    ContextualHeader = chunk.ContextualHeader
                });
            }

            return splitChunks;
        }

        private static ReadOnlyMemory<float> AverageEmbeddings(List<ReadOnlyMemory<float>> embeddings)
        {
            int dimension = embeddings[0].Length;
            float[] averageVector = new float[dimension];

            for (int i = 0; i < dimension; i++)
            {
                float sum = 0;
                for (int j = 0; j < embeddings.Count; j++)
                {
                    sum += embeddings[j].Span[i];
                }
                averageVector[i] = sum / embeddings.Count;
            }

            return new ReadOnlyMemory<float>(averageVector);
        }

        private static string DetermineContentType(DocumentChunk chunk)
        {
            if (chunk.IsTable)
                return "Table";
            if (!string.IsNullOrEmpty(chunk.Section?.Number))
                return $"Section_{chunk.Section.Type}";
            if (!string.IsNullOrEmpty(chunk.ContextualHeader))
                return "HeaderedContent";
            return "UnstructuredContent";
        }

        private static string GetEffectiveTitle(DocumentChunk chunk)
        {
            // Try section title first
            if (!string.IsNullOrEmpty(chunk.Section?.Title))
                return chunk.Section.Title;

            // Fall back to contextual header
            if (!string.IsNullOrEmpty(chunk.ContextualHeader))
                return chunk.ContextualHeader;

            // Last resort: first line if it looks like a title
            var firstLine = chunk.Content.Split('\n').FirstOrDefault();
            if (firstLine != null && firstLine.Length < 100 && char.IsUpper(firstLine[0]))
                return firstLine;

            return "";  // No title found
        }

        private static bool IsLikelyTableContent(string content)
        {
            var lines = content.Split('\n');
            if (lines.Length < 2) return false;

            // Check for consistent delimiters or column-like structure
            var delimiterCounts = lines.Select(l =>
                l.Count(c => c == '|' || c == '\t' || c == ','))
                .Take(5); // Check first 5 lines only

            return delimiterCounts.Distinct().Count() == 1
                && delimiterCounts.First() > 1;
        }

        private void EnrichChunksWithEmbeddings(List<DocumentChunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(chunk.Content))
                    {
                        _logger.LogWarning("Empty chunk content encountered, skipping embedding generation");
                        continue;
                    }

                    // Generate embeddings for the main content
                    _logger.LogInformation($"Generating embeddings for chunk of length {chunk.Content.Length}");
                    var contentEmbedding = GetEmbeddings(_embeddingClient, chunk.Content);
                    chunk.SectionEmbedding = contentEmbedding.ToArray().Select(f => (double)f).ToArray();

                    // Calculate semantic scores
                    _logger.LogInformation("Calculating semantic scores");

                    // Score based on semantic type
                    double typeScore = chunk.SemanticType switch
                    {
                        "header" => 1.5,
                        "definition" => 1.3,
                        "table" => 1.2,
                        "numbered-list" => 1.1,
                        _ => 1.0
                    };
                    chunk.SemanticScores["type_score"] = typeScore;

                    // Score based on heading level
                    chunk.SemanticScores["heading_score"] = chunk.HeadingLevel switch
                    {
                        1 => 1.5,  // Main heading
                        2 => 1.3,  // Subheading
                        _ => 1.0   // Not a heading
                    };
                    chunk.SemanticScores["keyword_score"] = 0.0;
                    // Score based on keyword presence
                    if (chunk.Keywords?.Any() == true)
                    {
                        chunk.SemanticScores["keyword_score"] = chunk.Keywords.Length / 5.0; // Normalize by max keywords
                    }

                    _logger.LogInformation($"Semantic scores calculated: type={typeScore}, heading={chunk.SemanticScores["heading_score"]}, keyword={chunk.SemanticScores["keyword_score"]}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error enriching chunk: {ex.Message}");

                    // Ensure defaults are set even on failure
                    chunk.SectionEmbedding = Array.Empty<double>();
                    chunk.SemanticScores = new Dictionary<string, double>
                    {
                        ["type_score"] = 1.0,
                        ["heading_score"] = 1.0,
                        ["keyword_score"] = 0.0
                    };
                }
            }
        }
        public enum SectionType
        {
            None,
            MainSection,
            Subsection,
            GenericHeading
        }

        public class DocumentSection
        {
            public string Title { get; set; }
            public string Number { get; set; }
            public SectionType Type { get; set; }
            public int PageNumber { get; set; }
        }

        public class DocumentChunk
        {
            // Core properties
            public string Content { get; set; } = string.Empty;
            public DocumentSection Section { get; set; }
            public DocumentSection Subsection { get; set; }
            public int PageNumber { get; set; }
            public int SubChunkIndex { get; set; }
            public bool IsTable { get; set; }
            public string ContextualHeader { get; set; } = string.Empty;

            // Enhanced properties
            public string PrecedingContext { get; set; } = string.Empty;
            public string FollowingContext { get; set; } = string.Empty;
            public string[] Keywords { get; set; } = Array.Empty<string>();
            public double[] SectionEmbedding { get; set; } = Array.Empty<double>();
            public int HeadingLevel { get; set; }
            public string SemanticType { get; set; } = "body-text";  // Default to body-text
            public Dictionary<string, double> SemanticScores { get; set; } = new()
            {
                ["type_score"] = 1.0,     // Default scores
                ["heading_score"] = 1.0,
                ["keyword_score"] = 0.0
            };

        }
    }
}