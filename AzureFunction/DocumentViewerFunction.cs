using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Web;

namespace RAGCitations
{
    public class DocumentViewerFunction
    {
        private readonly ILogger<DocumentViewerFunction> _logger;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _containerName;
        private readonly string _allowedOrigins;
        public DocumentViewerFunction(
            ILogger<DocumentViewerFunction> logger,
            BlobServiceClient blobServiceClient)
        {
            _logger = logger;
            _blobServiceClient = blobServiceClient;
            _containerName = Environment.GetEnvironmentVariable("BlobContainerName") ?? "documentcont";
            _allowedOrigins = Environment.GetEnvironmentVariable("AllowedOrigins") ?? "*";
        }
        [Function("ViewDocument")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options")] HttpRequest req)
        {
            try
            {
                if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleOptionsRequest();
                }

                _logger.LogInformation("Processing document view request");

                if (!TryExtractParameters(req, out var parameters))
                {
                    return new BadRequestObjectResult(new { error = "Invalid or missing parameters" });
                }

                var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
                var blobClient = containerClient.GetBlobClient(parameters.DocumentName);

                if (!await blobClient.ExistsAsync())
                {
                    _logger.LogWarning($"Document not found: {parameters.DocumentName}");
                    return new NotFoundObjectResult(new { error = "Document not found" });
                }

                var sasUri = await GenerateSasToken(blobClient);
                var html = GenerateViewerHtml(parameters, sasUri);

                var response = CreateResponse(html);
                var headers = ((CustomContentResult)response).CustomHeaders;
                AddSecurityHeaders(headers);
                AddCorsHeaders(headers);

                foreach (var header in headers)
                {
                    req.HttpContext.Response.Headers[header.Key] = header.Value;
                }

                return response;
            }
            catch (Exception ex)
            {
                return await HandleError(ex, "document view request");
            }
        }

        private async Task<IActionResult> HandleError(Exception ex, string operation)
        {
            _logger.LogError(ex, $"Error during {operation}");

            if (ex is Azure.RequestFailedException azureEx)
            {
                _logger.LogError($"Azure error: Status {azureEx.Status}, Error code: {azureEx.ErrorCode}");
                return new ObjectResult(new
                {
                    error = "Azure service error",
                    details = azureEx.Message
                })
                { StatusCode = azureEx.Status };
            }

            return new ObjectResult(new
            {
                error = "Internal server error",
                details = ex.Message,
                operation = operation
            })
            { StatusCode = 500 };
        }

        private static IActionResult HandleOptionsRequest()
        {
            var result = new CustomContentResult
            {
                StatusCode = 204,
                Content = string.Empty,
                ContentType = "text/plain",
                CustomHeaders = new Dictionary<string, string>
                {
                    { "Access-Control-Allow-Origin", "*" },
                    { "Access-Control-Allow-Methods", "GET, HEAD, OPTIONS" },
                    { "Access-Control-Allow-Headers", "*" },
                    { "Access-Control-Expose-Headers", "*" },
                    { "Access-Control-Max-Age", "3600" }
                }
            };

            return result;
        }

        private void AddSecurityHeaders(IDictionary<string, string> headers)
        {
            var blobDomain = (_blobServiceClient.Uri).GetLeftPart(UriPartial.Authority);

            headers.Add("Content-Security-Policy",
                "default-src 'self' blob: data: https://cdnjs.cloudflare.com; " +
                "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
                 "worker-src 'self' blob:; " + 
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' blob: data:; " +
                "object-src 'self' blob:; " + 
                "frame-src 'self' blob:; " + 
                $"connect-src 'self' blob: {blobDomain};");
            headers.Add("X-Content-Type-Options", "nosniff");
            headers.Add("X-Frame-Options", "SAMEORIGIN");
            headers.Add("Referrer-Policy", "same-origin");
            headers.Add("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        }

        private void AddCorsHeaders(IDictionary<string, string> headers)
        {
            headers.Add("Access-Control-Allow-Origin", _allowedOrigins);
            headers.Add("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            headers.Add("Access-Control-Allow-Headers", "*");
            headers.Add("Access-Control-Expose-Headers", "*");
            headers.Add("Access-Control-Max-Age", "3600");
        }

        private async Task<string> GenerateSasToken(BlobClient blobClient)
        {
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _containerName,
                BlobName = blobClient.Name,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(30)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var storageAccountName = Environment.GetEnvironmentVariable("StorageAccountName");
            var storageAccountKey = Environment.GetEnvironmentVariable("StorageAccountKey");

            if (string.IsNullOrEmpty(storageAccountName) || string.IsNullOrEmpty(storageAccountKey))
            {
                throw new InvalidOperationException("Storage account credentials not configured");
            }

            var sasToken = sasBuilder.ToSasQueryParameters(
                new StorageSharedKeyCredential(storageAccountName, storageAccountKey)).ToString();

            return blobClient.Uri + "?" + sasToken;
        }

        private bool TryExtractParameters(HttpRequest req, out ViewerParameters parameters)
        {
            parameters = new ViewerParameters();

            try
            {
                // Single document name extraction with validation
                if (!req.Query.TryGetValue("doc", out var docValue) || string.IsNullOrEmpty(docValue))
                {
                    _logger.LogWarning("Document name is missing");
                    return false;
                }

                var documentName = HttpUtility.UrlDecode(docValue).Trim();

                // Validate document name
                if (string.IsNullOrEmpty(documentName) ||
                    documentName.Contains("..") ||
                    documentName.Contains("/") ||
                    documentName.Contains("\\"))
                {
                    _logger.LogWarning("Invalid document name detected");
                    return false;
                }


                parameters.DocumentName = !documentName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                 ? documentName + ".pdf"
                 : documentName;

                parameters.PageNumber = req.Query.TryGetValue("page", out var pageValue) &&
                                      int.TryParse(pageValue, out var page)
                    ? page
                    : 1;

                // Extract existing parameters
                parameters.TextToHighlight = HttpUtility.UrlDecode(req.Query["highlight"].ToString());
                parameters.HighlightSource = HttpUtility.UrlDecode(req.Query["highlightSource"].ToString());
                parameters.Section = HttpUtility.UrlDecode(req.Query["section"].ToString());
                parameters.ContextualHeader = HttpUtility.UrlDecode(req.Query["header"].ToString());
                parameters.ContentType = req.Query["contentType"].ToString();
                parameters.EffectiveTitle = HttpUtility.UrlDecode(req.Query["effectiveTitle"].ToString());
                parameters.TitleSource = req.Query["titleSource"].ToString();

                // Extract new parameters
                parameters.PrecedingContext = HttpUtility.UrlDecode(req.Query["precedingContext"].ToString());
                parameters.FollowingContext = HttpUtility.UrlDecode(req.Query["followingContext"].ToString());
                parameters.SemanticType = req.Query["semanticType"].ToString();
                parameters.HeadingLevel = req.Query.TryGetValue("headingLevel", out var headingLevel) &&
                                        int.TryParse(headingLevel, out var level)
                    ? level
                    : 0;

                // Extract keywords array
                if (req.Query.TryGetValue("keywords", out var keywordsValue))
                {
                    parameters.Keywords = HttpUtility.UrlDecode(keywordsValue.ToString())
                        .Split(',', StringSplitOptions.RemoveEmptyEntries);
                }

                parameters.HasStructuredSections = req.Query.TryGetValue("hasStructured", out var hasStructured) &&
                                                bool.TryParse(hasStructured, out var structured) &&
                                                structured;

                _logger.LogInformation($"Parameters extracted: Page={parameters.PageNumber}, " +
                                     $"Section={parameters.Section}, Highlight={parameters.TextToHighlight}, " +
                                     $"SemanticType={parameters.SemanticType}, HeadingLevel={parameters.HeadingLevel}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting parameters");
                return false;
            }
        }

        private class ViewerParameters
        {
            public string DocumentName { get; set; }
            public int PageNumber { get; set; } = 1;
            public string Section { get; set; }
            public string ContentType { get; set; }
            public bool HasStructuredSections { get; set; }
            public string ContextualHeader { get; set; }
            public string EffectiveTitle { get; set; }
            public string TextToHighlight { get; set; }
            public string HighlightSource { get; set; }
            public string TitleSource { get; set; }
            // New parameters
            public string PrecedingContext { get; set; }
            public string FollowingContext { get; set; }
            public string SemanticType { get; set; }
            public int HeadingLevel { get; set; }
            public string[] Keywords { get; set; }
        }

        private IActionResult CreateResponse(string html)
        {
            return new CustomContentResult
            {
                Content = html,
                ContentType = "text/html",
                StatusCode = 200,
                CustomHeaders = new Dictionary<string, string>()
            };
        }

        public class CustomContentResult : ContentResult
        {
            public Dictionary<string, string> CustomHeaders { get; set; }

            public override async Task ExecuteResultAsync(ActionContext context)
            {
                if (CustomHeaders != null)
                {
                    foreach (var header in CustomHeaders)
                    {
                        context.HttpContext.Response.Headers[header.Key] = header.Value;
                    }
                }

                await base.ExecuteResultAsync(context);
            }
        }

        private static string GenerateViewerHtml(ViewerParameters parameters, string documentUrl)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<!DOCTYPE html>");
            builder.AppendLine("<html>");
            builder.AppendLine("<head>");
            builder.AppendLine($"<title>Document Viewer - {HttpUtility.HtmlEncode(parameters.DocumentName)}</title>");
            builder.AppendLine(@"<link rel=""icon"" type=""image/x-icon"" href=""data:image/x-icon;,"">");
            builder.AppendLine(@"<script src=""https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js""></script>");

            // Add styles
            builder.AppendLine(@"
<style>
    /* Base styles */
    body { 
        margin: 0; 
        font-family: Arial, sans-serif; 
        background: #f0f0f0; 
    }

    #container { 
        display: flex; 
        height: 100vh;
        background: white;
    }

    #sidebar { 
        width: 300px; 
        background: #f8f9fa; 
        padding: 20px;
        border-right: 1px solid #dee2e6;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
    }

    #mainContent { 
        flex-grow: 1; 
        display: flex; 
        flex-direction: column;
        position: relative;
    }

    /* Progress bar */
    #progressBar {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 3px;
        background: #f0f0f0;
        z-index: 1000;
    }

    #progressValue {
        width: 0%;
        height: 100%;
        background: #007bff;
        transition: width 0.3s ease;
    }

    /* Toolbar */
    #toolbar { 
        padding: 15px;
        background: #f8f9fa;
        border-bottom: 1px solid #dee2e6;
        display: flex;
        align-items: center;
        gap: 15px;
        z-index: 10;
    }

    .toolbar-group {
        display: flex;
        align-items: center;
        gap: 10px;
    }

    #viewerContainer { 
        flex-grow: 1;
        overflow: auto;
        position: relative;
        background: #e9ecef;
        display: flex;
        justify-content: center;
        align-items: flex-start;
        padding: 20px;
    }

    #pdfViewer { 
        background: white;
        box-shadow: 0 2px 10px rgba(0,0,0,0.1);
        margin: auto;
        transition: transform 0.3s ease;
    }

#loadingIndicator {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    background: white;
    padding: 20px 40px;
    border-radius: 8px;
    box-shadow: 0 2px 15px rgba(0,0,0,0.1);
    z-index: 1000;
    display: none;  /* Hide by default */
    flex-direction: column;
    align-items: center;
    gap: 15px;
}

.loading-text {
    font-size: 14px;
    color: #666;
}

    #loadingSpinner {
        width: 20px;
        height: 20px;
        border: 2px solid #f3f3f3;
        border-top: 2px solid #3498db;
        border-radius: 50%;
        animation: spin 1s linear infinite;
    }

    @keyframes spin {
        0% { transform: rotate(0deg); }
        100% { transform: rotate(360deg); }
    }

    #error {
        display: none;
        color: #dc3545;
        padding: 10px;
        margin: 0 10px;
        border-radius: 4px;
        background: #f8d7da;
        border: 1px solid #f5c6cb;
    }

    button {
        padding: 8px 16px;
        border: 1px solid #dee2e6;
        background: white;
        border-radius: 4px;
        cursor: pointer;
        transition: all 0.2s;
        display: flex;
        align-items: center;
        gap: 5px;
    }

    button:hover {
        background: #f8f9fa;
        border-color: #ced4da;
    }

    button:disabled {
        opacity: 0.6;
        cursor: not-allowed;
    }

    /* Document outline styles */
    .outline-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        margin-bottom: 15px;
    }

    .outline-toggle {
        padding: 4px 8px;
        font-size: 12px;
    }

    .outline-root {
        font-size: 16px;
        color: #2c3e50;
        padding: 8px 0;
        border-bottom: 2px solid #dee2e6;
        margin-bottom: 12px;
        font-weight: 600;
    }

    .outline-list {
        margin-left: 20px;
        transition: all 0.3s ease;
    }

    .outline-item-container {
        margin: 2px 0;
    }

    .outline-item {
        display: flex;
        align-items: center;
        padding: 8px;
        cursor: pointer;
        border-radius: 4px;
        color: #495057;
        font-size: 14px;
        transition: all 0.2s ease;
    }

    .outline-item:hover {
        background: #e9ecef;
        color: #212529;
    }

    .outline-expand {
        display: inline-block;
        width: 20px;
        height: 20px;
        line-height: 20px;
        text-align: center;
        margin-right: 4px;
        cursor: pointer;
        color: #6c757d;
        user-select: none;
        font-size: 12px;
        transition: transform 0.2s ease;
    }

    .outline-expand:hover {
        color: #212529;
    }

    .outline-expand.collapsed {
        transform: rotate(-90deg);
    }

    .outline-item.active {
        background: #e9ecef;
        font-weight: 500;
    }

    /* Search functionality */
    #searchBox {
        margin-bottom: 15px;
        width: 100%;
        padding: 8px;
        border: 1px solid #dee2e6;
        border-radius: 4px;
        font-size: 14px;
    }

    /* Zoom controls */
    .zoom-controls {
        display: flex;
        align-items: center;
        gap: 10px;
    }

    #zoomLevel {
        min-width: 60px;
        text-align: center;
    }

    /* Keyboard shortcuts info */
    #shortcutsPanel {
        position: fixed;
        bottom: 20px;
        right: 20px;
        background: white;
        padding: 15px;
        border-radius: 8px;
        box-shadow: 0 2px 10px rgba(0,0,0,0.1);
        display: none;
        z-index: 1000;
    }

    .shortcuts-list {
        margin: 0;
        padding: 0;
        list-style: none;
    }

    .shortcuts-list li {
        margin: 5px 0;
        font-size: 14px;
    }

    .keyboard-key {
        display: inline-block;
        padding: 2px 6px;
        background: #f8f9fa;
        border: 1px solid #dee2e6;
        border-radius: 3px;
        font-size: 12px;
        margin: 0 2px;
    }
#errorOverlay {
    display: none;
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0,0,0,0.5);
    z-index: 2000;
    justify-content: center;
    align-items: center;
}

#errorOverlay > div {
    background: white;
    padding: 20px 40px;
    border-radius: 8px;
    text-align: center;
    max-width: 80%;
}

#errorOverlay h3 {
    color: #dc3545;
    margin-top: 0;
}

#errorOverlay button {
    margin-top: 15px;
    background: #007bff;
    color: white;
    border: none;
    padding: 8px 20px;
    border-radius: 4px;
    cursor: pointer;
}

#errorOverlay button:hover {
    background: #0056b3;
}
</style>");
            builder.AppendLine(@"
<script>
// At the start of the script, add:
if (pdfjsLib) {
    try {
        pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';
    } catch (error) {
        console.error('Error initializing PDF.js worker:', error);
        showError('Error initializing PDF viewer');
    }
}

const viewerConfig = {
         documentUrl: '" + documentUrl + @"',
        initialPage: " + parameters.PageNumber + @",
        section: '" + HttpUtility.JavaScriptStringEncode(parameters.Section ?? "") + @"',
        highlight: '" + HttpUtility.JavaScriptStringEncode(parameters.TextToHighlight ?? "") + @"',
        highlightSource: '" + HttpUtility.JavaScriptStringEncode(parameters.HighlightSource ?? "") + @"',
        documentName: '" + HttpUtility.JavaScriptStringEncode(parameters.DocumentName) + @"',
        contentType: '" + HttpUtility.JavaScriptStringEncode(parameters.ContentType ?? "") + @"',
        hasStructuredSections: " + parameters.HasStructuredSections.ToString().ToLower() + @",
        contextualHeader: '" + HttpUtility.JavaScriptStringEncode(parameters.ContextualHeader ?? "") + @"',
        effectiveTitle: '" + HttpUtility.JavaScriptStringEncode(parameters.EffectiveTitle ?? "") + @"',
        precedingContext: '" + HttpUtility.JavaScriptStringEncode(parameters.PrecedingContext ?? "") + @"',
        followingContext: '" + HttpUtility.JavaScriptStringEncode(parameters.FollowingContext ?? "") + @"',
        semanticType: '" + HttpUtility.JavaScriptStringEncode(parameters.SemanticType ?? "") + @"',
        headingLevel: " + parameters.HeadingLevel + @",
        keywords: " + JsonSerializer.Serialize(parameters.Keywords ?? Array.Empty<string>()) + @"
    };
          const highlightConfig = {
    colors: {
        default: 'rgba(255, 255, 0, 0.35)',
        header: 'rgba(0, 255, 255, 0.35)',
        definition: 'rgba(255, 200, 0, 0.35)',
        keyword: 'rgba(200, 200, 255, 0.25)'
    },
    headingStyles: {
        lineWidth: 2,
        color: 'rgba(0, 0, 255, 0.4)'
    }
};
            const DEBUG = true;  // Set to false in production
const isMobile = /iPhone|iPad|iPod|Android/i.test(navigator.userAgent);
window.objectUrls = new Set();

function log(...args) {
    if (DEBUG) {
        console.log(...args);
    }
}
// Add this to your LoadingManager object
const LoadingManager = {
    operations: new Set(),
    timeouts: new Map(),
    
    start: function(message, timeout = 30000) {  // 30 second default timeout
        const loadingId = Date.now().toString();
        this.operations.add(loadingId);
        
        // Set timeout to auto-clear loading state
        const timeoutId = setTimeout(() => {
            console.warn(`Loading operation ${loadingId} timed out`);
            this.end(loadingId);
        }, timeout);
        
        this.timeouts.set(loadingId, timeoutId);
        
        const loadingDiv = document.getElementById('loadingIndicator');
        const messageSpan = loadingDiv?.querySelector('.loading-text');
        if (messageSpan) {
            messageSpan.textContent = message || 'Loading...';
        }
        if (loadingDiv) {
            loadingDiv.style.display = 'flex';
        }
        
        updateProgress(0);
        
        return loadingId;
    },
    
    end: function(loadingId) {
        if (loadingId) {
            this.operations.delete(loadingId);
            // Clear timeout
            const timeoutId = this.timeouts.get(loadingId);
            if (timeoutId) {
                clearTimeout(timeoutId);
                this.timeouts.delete(loadingId);
            }
        }
        
        if (this.operations.size === 0) {
            const loadingDiv = document.getElementById('loadingIndicator');
            if (loadingDiv) {
                loadingDiv.style.display = 'none';
            }
            updateProgress(100);
        }
    },
    
    clear: function() {
        this.operations.clear();
        // Clear all timeouts
        this.timeouts.forEach(timeoutId => clearTimeout(timeoutId));
        this.timeouts.clear();
        
        const loadingDiv = document.getElementById('loadingIndicator');
        if (loadingDiv) {
            loadingDiv.style.display = 'none';
        }
        updateProgress(100);
    }
};
function setButtonsLoading(loading) {
    const buttons = document.querySelectorAll('button');
    buttons.forEach(button => {
        button.disabled = loading;
        if (loading) {
            button.dataset.originalText = button.textContent;
            button.textContent = 'Loading...';
        } else if (button.dataset.originalText) {
            button.textContent = button.dataset.originalText;
        }
    });
}

async function checkNetwork() {
    if (!navigator.onLine) {
        showError('No internet connection. Please check your network.');
        return false;
    }
    return true;
}
// Viewer state
let pdfDoc = null;
let pageNum = viewerConfig.initialPage || 1;
let pageRendering = false;
let pageNumPending = null;
let scale = 1.5;
const pageCache = new Map();

// UI state
let sidebarVisible = true;
let shortcutsPanelVisible = false;

function updateProgress(percent) {
    const progressValue = document.getElementById('progressValue');
    progressValue.style.width = `${percent}%`;
    if (percent >= 100) {
        setTimeout(() => {
            progressValue.style.width = '0%';
        }, 500);
    }
}

// Add this with your other state variables (near the top where you have other let declarations)
let loadingOperations = new Set();

async function handleAsyncOperation(operation, message, timeout = 30000) {
    const loadingId = LoadingManager.start(message, timeout);
    try {
        return await withTimeout(operation(), timeout, `${message} timed out`);
    } catch (error) {
        console.error(`Error during ${message}:`, error);
        showError(`Failed to ${message.toLowerCase()}`);
        throw error;
    } finally {
        LoadingManager.end(loadingId);
    }
}

function showError(message, isOverlay = true) {
    const errorDiv = document.getElementById('error');
    const errorOverlay = document.getElementById('errorOverlay');
    const errorMessage = document.getElementById('errorMessage');
    
    if (errorDiv) {
        errorDiv.textContent = message;
        errorDiv.style.display = 'block';
    }
    
    if (isOverlay && errorOverlay && errorMessage) {
        errorMessage.textContent = message;
        errorOverlay.style.display = 'flex';
        // Add close button
        const closeButton = errorOverlay.querySelector('.close-error');
        if (closeButton) {
            closeButton.onclick = closeError;
        }
    }
    // Auto-cleanup loading states on error
    LoadingManager.clear();
}
function closeError() {
    const errorDiv = document.getElementById('error');
    const errorOverlay = document.getElementById('errorOverlay');
    
    if (errorDiv) {
        errorDiv.style.display = 'none';
    }
    
    if (errorOverlay) {
        errorOverlay.style.display = 'none';
    }
}

async function handleError(error, operation) {
    closeError();  // Close any existing errors first
    
    if (error.name === 'TimeoutError') {
        showError(`Operation timed out: ${operation}`);
        return;
    }
    
    console.error(`Error during ${operation}:`, error);
    showError(`Failed to ${operation.toLowerCase()}: ${error.message}`);
}
async function withTimeout(promise, timeoutMs = 30000, message = 'Operation timed out') {
    let timeoutHandle;
    
    const timeoutPromise = new Promise((_, reject) => {
        timeoutHandle = setTimeout(() => {
            reject(new Error(message));
        }, timeoutMs);
    });
    
    try {
        return await Promise.race([promise, timeoutPromise]);
    } finally {
        clearTimeout(timeoutHandle);
    }
}
async function init() {
    const loadingId = LoadingManager.start('Loading document...', 60000); 
    try {
        if (!await checkNetwork()) return;
        
        setButtonsLoading(true);
        
        const loadingTask = pdfjsLib.getDocument({
            url: viewerConfig.documentUrl,
            cMapUrl: 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/cmaps/',
            cMapPacked: true
        });

        loadingTask.onProgress = function(progress) {
            if (progress.total > 0) {
                const percent = Math.round((progress.loaded / progress.total) * 100);
                updateProgress(percent);
            }
        };

        pdfDoc = await loadingTask.promise;
        document.getElementById('pageCount').textContent = pdfDoc.numPages;
        updateButtonStates();
        
        await loadOutline();
        
        if (viewerConfig.initialPage > 0) {
            pageNum = viewerConfig.initialPage;
            await renderPage(pageNum);
            
            if (viewerConfig.section && viewerConfig.initialPage === 1) {
                await navigateToSection(viewerConfig.section);
            }
        } else if (viewerConfig.section) {
            await navigateToSection(viewerConfig.section);
        }
        
    } catch (error) {
        console.error('Error initializing viewer:', error);
        showError(`Error loading document: ${error.message}`);
    } finally {
       LoadingManager.end(loadingId);
        setButtonsLoading(false);
    }
}

async function highlightText(textContent, context, viewport) {
    try {
        if (!viewerConfig.highlight) {
            console.log('No highlight text configured');
            return false;
        }

        const searchText = decodeURIComponent(viewerConfig.highlight).toLowerCase();
        if (!searchText) {
            console.log('No search text to highlight');
            return false;
        }

        context.save();
        let foundMatch = false;

        textContent.items.forEach(item => {
            try {
                const itemText = item.str.toLowerCase();
                const [x, y] = viewport.convertToViewportPoint(item.transform[4], item.transform[5]);

                // Main content highlight
                if (itemText.includes(searchText) || searchText.includes(itemText)) {
                    foundMatch = true;
                    const color = highlightConfig.colors[viewerConfig.semanticType] || highlightConfig.colors.default;
                    context.fillStyle = color;
                    context.fillRect(x, y - item.height, item.width * scale, item.height * 1.2);

                    // Add heading emphasis
                    if (viewerConfig.headingLevel > 0) {
                        context.strokeStyle = highlightConfig.headingStyles.color;
                        context.lineWidth = highlightConfig.headingStyles.lineWidth * viewerConfig.headingLevel;
                        context.strokeRect(x, y - item.height, item.width * scale, item.height * 1.2);
                    }
                }

                // Highlight keywords
                if (viewerConfig.keywords?.some(keyword => 
                    itemText.includes(keyword.toLowerCase()))) {
                    context.fillStyle = highlightConfig.colors.keyword;
                    context.fillRect(x, y - item.height, item.width * scale, item.height * 1.2);
                }

            } catch (itemError) {
                console.error('Error highlighting item:', itemError);
            }
        });

        context.restore();
        return foundMatch;

    } catch (error) {
        console.error('Error in highlightText:', error);
        return false;
    }
}
// Add navigation helper
async function handleNoHighlightFound() {
    console.log('No matching text found on current page');
    if (viewerConfig.precedingContext || viewerConfig.followingContext) {
        // Try to find match in surrounding context
        const contextPages = await findInContext();
        if (contextPages.length > 0) {
            pageNum = contextPages[0];
            await renderPage(pageNum);
        }
    }
}
async function findInContext() {
    const searchText = decodeURIComponent(viewerConfig.highlight).toLowerCase();
    const contextPages = new Set();
    
    if (viewerConfig.precedingContext) {
        const precedingMatch = viewerConfig.precedingContext.toLowerCase().includes(searchText);
        if (precedingMatch) {
            contextPages.add(pageNum - 1);
        }
    }
    
    if (viewerConfig.followingContext) {
        const followingMatch = viewerConfig.followingContext.toLowerCase().includes(searchText);
        if (followingMatch) {
            contextPages.add(pageNum + 1);
        }
    }
    
    return Array.from(contextPages).filter(p => p > 0 && p <= pdfDoc.numPages);
}
async function renderPage(num, retries = 3) {
    const loadingId = LoadingManager.start('Rendering page...');
    
    try {
        if (!await checkNetwork()) return;
        
        pageRendering = true;
        document.getElementById('pageNum').textContent = num;
        updatePageNavigationButtons();

        const canvas = document.getElementById('pdfViewer');
        const context = canvas.getContext('2d');
        const cacheKey = `${num}-${scale}`;
        let page, viewport;
        
        try {
            if (pageCache.has(cacheKey)) {
                ({ page, viewport } = pageCache.get(cacheKey));
            } else {
                page = await pdfDoc.getPage(num);
                viewport = page.getViewport({ scale });
                
                if (pageCache.size >= 5) {
                    const oldestKey = pageCache.keys().next().value;
                    pageCache.delete(oldestKey);
                }
                
                pageCache.set(cacheKey, { page, viewport });
            }
            
            canvas.height = viewport.height;
            canvas.width = viewport.width;
            
            context.clearRect(0, 0, viewport.width, viewport.height);
            
            await page.render({
                canvasContext: context,
                viewport: viewport
            }).promise;
            
            if (viewerConfig.highlight) {
            const textContent = await page.getTextContent();
            const foundMatch = await highlightText(textContent, context, viewport);
            
            if (!foundMatch) {
                await handleNoHighlightFound();
            }
        }
            
            const newUrl = new URL(window.location.href);
            newUrl.searchParams.set('page', num);
            window.history.replaceState({}, '', newUrl);
            
        } catch (renderError) {
            console.error('Render error:', renderError);
            if (retries > 0) {
                await new Promise(resolve => setTimeout(resolve, 1000));
                return renderPage(num, retries - 1);
            }
            throw renderError;
        }
        
        pageRendering = false;
        if (pageNumPending !== null) {
            renderPage(pageNumPending);
            pageNumPending = null;
        }
        
    } catch (error) {
        console.error('Page rendering error:', error);
        showError('Failed to render page');
        pageRendering = false;
    } finally {
        LoadingManager.end(loadingId);
    }
}
// Add error boundary handling
window.onerror = function(msg, url, line, col, error) {
    console.error('Global error:', { msg, url, line, col, error });
    showError('An error occurred in the viewer');
    return false;
};

// Add network state monitoring
window.addEventListener('online', function() {
    console.log('Network connection restored');
    init(); // Reinitialize viewer if needed
});

window.addEventListener('offline', function() {
    console.log('Network connection lost');
    showError('Network connection lost. Please check your connection.');
});



function queueRenderPage(num) {
    if (pageRendering) {
        pageNumPending = num;
    } else {
        renderPage(num);
    }
}

function updatePageNavigationButtons() {
    const prevButton = document.getElementById('prevButton');
    const nextButton = document.getElementById('nextButton');
    
    prevButton.disabled = pageNum <= 1;
    nextButton.disabled = pageNum >= pdfDoc.numPages;
}

function onPrevPage() {
    if (pageNum <= 1) return;
    pageNum--;
    queueRenderPage(pageNum);
    updateButtonStates();  // Add this line
}

function onNextPage() {
    if (pageNum >= pdfDoc.numPages) return;
    pageNum++;
    queueRenderPage(pageNum);
    updateButtonStates();  // Add this line
}

function onZoomIn() {
    if (scale < 3.0) {
        scale += 0.25;
        //updateZoomLevel();
        queueRenderPage(pageNum);
        updateButtonStates();  // Add this line
    }
}

function onZoomOut() {
    if (scale > 0.5) {
        scale -= 0.25;
        //updateZoomLevel();
        queueRenderPage(pageNum);
        updateButtonStates(); 
    }
}

function updateButtonStates() {
    document.getElementById('prevButton').disabled = pageNum <= 1;
    document.getElementById('nextButton').disabled = pageNum >= pdfDoc.numPages;
    document.getElementById('zoomOut').disabled = scale <= 0.5;
    document.getElementById('zoomIn').disabled = scale >= 3.0;
}


async function downloadPdf() {
    await downloadPdfWithRetry();
}

async function downloadPdfWithRetry(retries = 3) {
    try {
        if (!await checkNetwork()) {
            return;
        }

        log('Starting download');
        setButtonsLoading(true);

        const response = await fetch(viewerConfig.documentUrl);
        const blob = await response.blob();
        const url = window.URL.createObjectURL(blob);
        window.objectUrls.add(url);

        const a = document.createElement('a');
        a.href = url;
        a.download = viewerConfig.documentName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);

        log('Download complete');
    } catch (error) {
        console.error('Download error:', error);
        if (retries > 0) {
            log(`Retrying download. Attempts remaining: ${retries}`);
            await new Promise(resolve => setTimeout(resolve, 1000));
            await downloadPdfWithRetry(retries - 1);
        } else {
            showError('Failed to download document after multiple attempts');
        }
    } finally {
        setButtonsLoading(false);
    }
}
// Keyboard navigation
document.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT') return;
// Continue with keyboard event handler
    switch(e.key) {
        case 'ArrowLeft':
            onPrevPage();
            break;
        case 'ArrowRight':
            onNextPage();
            break;
        case '+':
        case '=':
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                onZoomIn();
            }
            break;
        case '-':
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                onZoomOut();
            }
            break;
        case '0':
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                scale = 1.5;
                //updateZoomLevel();
                queueRenderPage(pageNum);
            }
            break;
        case 'f':
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                document.getElementById('searchBox').focus();
            }
            break;
        case 'b':
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                toggleSidebar();
            }
            break;
        case '?':
            toggleShortcutsPanel();
            break;
    }
});

let touchStartX = null;

try {
    const viewerContainer = document.getElementById('viewerContainer');
    if (viewerContainer) {
        viewerContainer.addEventListener('touchstart', (e) => {
            touchStartX = e.touches[0].clientX;
        });

        viewerContainer.addEventListener('touchend', (e) => {
            if (!touchStartX) return;
            
            const touchEndX = e.changedTouches[0].clientX;
            const diff = touchStartX - touchEndX;
            
            if (Math.abs(diff) > 50) {
                if (diff > 0) {
                    onNextPage();
                } else {
                    onPrevPage();
                }
            }
            
            touchStartX = null;
        });
    }
} catch (error) {
    console.error('Error setting up touch handlers:', error);
}
async function loadOutline() {
    try {
        const outline = await pdfDoc.getOutline();
        const container = document.getElementById('outline');
        container.innerHTML = '';

        // Add search box
        const searchBox = document.createElement('input');
        searchBox.type = 'text';
        searchBox.id = 'searchBox';
        searchBox.placeholder = 'Search in document...';
        searchBox.addEventListener('input', handleSearch);
        container.appendChild(searchBox);

        // Add document title
        const documentTitle = document.createElement('div');
        documentTitle.className = 'outline-root';
        documentTitle.textContent = viewerConfig.documentName.replace('.pdf', '');
        container.appendChild(documentTitle);

        if (outline && outline.length > 0) {
            const outlineTree = createOutlineTree(outline);
            container.appendChild(outlineTree);
        } else {
            const pageList = createPageList();
            container.appendChild(pageList);
        }
    } catch (error) {
        console.error('Error loading outline:', error);
        showError('Error loading document outline');
    }
}



function createPageList() {
    const pageList = document.createElement('div');
    pageList.className = 'page-list';
    
    for (let i = 1; i <= pdfDoc.numPages; i++) {
        const pageItem = document.createElement('div');
        pageItem.className = 'outline-item';
        pageItem.textContent = `Page ${i}`;
        pageItem.onclick = () => {
            pageNum = i;
            renderPage(i);
            highlightCurrentOutlineItem(pageItem);
        };
        pageList.appendChild(pageItem);
    }
    
    return pageList;
}

function toggleOutlineItem(itemDiv, expandButton) {
    const childList = itemDiv.querySelector('.outline-list');
    const isExpanded = !expandButton.classList.contains('collapsed');
    
    if (childList) {
        childList.style.display = isExpanded ? 'none' : 'block';
        expandButton.classList.toggle('collapsed');
        expandButton.textContent = isExpanded ? '?' : '?';
    }
}

function highlightCurrentOutlineItem(item) {
    document.querySelectorAll('.outline-item').forEach(i => {
        i.classList.remove('active');
    });
    item.classList.add('active');
}
async function navigateToSection(section) {
    if (!section) return;
    
    const loadingId = LoadingManager.start('Navigating to section...');
    try {
        console.log('Navigating to section:', section);
        
        if (viewerConfig.initialPage > 0 && pageNum === viewerConfig.initialPage) {
            console.log('Skipping section navigation as we are already on the correct page:', pageNum);
            return;
        }
        
        let found = false;
        
        if (viewerConfig.hasStructuredSections && section.match(/^\d+(\.\d+)*$/)) {
            found = await findInStructuredDocument(section);
        }
        
        if (!found && viewerConfig.contextualHeader) {
            found = await findInUnstructuredDocument(section);
        }
        
        if (!found) {
            found = await findInContent(section);
        }
        
        if (!found) {
            console.warn(`Section ""${section}"" not found in document`);
        }
    } catch (error) {
        console.error('Navigation error:', error);
        showError('Failed to navigate to the specified section');
    } finally {
        LoadingManager.end(loadingId);
    }
}

async function findInStructuredDocument(section) {
    const outline = await pdfDoc.getOutline();
    if (outline) {
        let found = false;
        for (const item of outline) {
            if (item.title.includes(section)) {
                found = true;
                // Only navigate if we don't have a specific page or we're not on it
                if (!viewerConfig.initialPage || pageNum !== viewerConfig.initialPage) {
                    await navigateToDestination(item.dest);
                }
                break;
            }
        }
        if (!found) {
            await findInContent(section);
        }
        return found;
    }
    return false;
}

async function findInUnstructuredDocument(section) {
    if (viewerConfig.contextualHeader) {
        await findInContent(viewerConfig.contextualHeader);
    } else {
        await findInContent(section);
    }
}

async function findInOutline(section) {
    const outline = await pdfDoc.getOutline();
    if (!outline) {
        await findInContent(section);
        return;
    }

    let found = false;
    const searchSection = async (items) => {
        for (const item of items) {
            if (item.title.includes(section)) {
                found = true;
                await navigateToDestination(item.dest);
                break;
            }
            if (item.items) {
                await searchSection(item.items);
                if (found) break;
            }
        }
    };

    await searchSection(outline);
    if (!found) {
        await findInContent(section);
    }
}

async function findInContent(searchText) {
    if (!searchText) return false;
    
    const loadingId = LoadingManager.start('Searching content...');
    try {
        let found = false;
        for (let i = 1; i <= pdfDoc.numPages; i++) {
            try {
                const page = await pdfDoc.getPage(i);
                const textContent = await page.getTextContent();
                const pageText = textContent.items.map(item => item.str).join(' ');
                
                if (pageText.toLowerCase().includes(searchText.toLowerCase())) {
                    pageNum = i;
                    found = true;
                    await renderPage(i);
                    break;
                }
            } catch (error) {
                console.error(`Error searching page ${i}:`, error);
            }
        }
        return found;
    } finally {
        LoadingManager.end(loadingId);
    }
}
async function navigateToDestination(dest) {
    const loadingId = LoadingManager.start('Navigating to location...');
    try {
        if (!dest) return;
        
        // Handle section numbers
        if (typeof dest === 'string' && dest.match(/^\d+(\.\d+)*$/)) {
            await navigateToSectionNumber(dest);
            return;
        }
        
        // Handle contextual headers
        if (typeof dest === 'string') {
            await navigateToContextualHeader(dest);
            return;
        }

        // Handle PDF destinations
        if (Array.isArray(dest)) {
            const pageIndex = await pdfDoc.getPageIndex(dest[0]);
            pageNum = pageIndex + 1;
            await renderPage(pageNum);
        }
    } catch (error) {
        console.error('Navigation error:', error);
        showError('Failed to navigate to the specified location');
    } finally {
        LoadingManager.end(loadingId);
    }
}


async function navigateToSectionNumber(sectionNumber) {
    const loadingId = LoadingManager.start('Navigating to section...');
    try {
        const outline = await pdfDoc.getOutline();
        if (outline) {
            const item = outline.find(i => i.title.includes(sectionNumber));
            if (item && item.dest) {
                const pageIndex = await pdfDoc.getPageIndex(item.dest[0]);
                pageNum = pageIndex + 1;
                await renderPage(pageNum);
                return;
            }
        }
        // Fallback to searching in content
        await searchForContent(sectionNumber);
    } catch (error) {
        console.error('Section navigation error:', error);
        showError('Failed to navigate to section');
    } finally {
        LoadingManager.end(loadingId);
    }
}

async function navigateToContextualHeader(header) {
    const loadingId = LoadingManager.start('Navigating to header...');
    try {
        await searchForContent(header);
    } catch (error) {
        console.error('Header navigation error:', error);
        showError('Failed to navigate to header');
    } finally {
        LoadingManager.end(loadingId);
    }
}

async function searchForContent(searchText) {
    if (!searchText) return;
    
    const loadingId = LoadingManager.start('Searching content...');
    try {
        for (let i = 1; i <= pdfDoc.numPages; i++) {
            const page = await pdfDoc.getPage(i);
            const textContent = await page.getTextContent();
            const text = textContent.items.map(item => item.str).join(' ');
            
            if (text.includes(searchText)) {
                pageNum = i;
                await renderPage(pageNum);
                return;
            }
        }
    } catch (error) {
        console.error('Search error:', error);
        showError('Failed to search content');
    } finally {
        LoadingManager.end(loadingId);
    }
}

function createOutlineTree(items, level = 0) {
    const list = document.createElement('div');
    list.className = `outline-list outline-level-${level}`;

    items.forEach((item, index) => {
        const itemDiv = document.createElement('div');
        itemDiv.className = 'outline-item-container';

        const itemContent = document.createElement('div');
        itemContent.className = 'outline-item';
        itemContent.dataset.searchContent = item.title.toLowerCase();
        
        if (item.items && item.items.length > 0) {
            const expandButton = document.createElement('span');
            expandButton.className = 'outline-expand';
            expandButton.textContent = '?';
            expandButton.onclick = (e) => {
                e.stopPropagation();
                toggleOutlineItem(itemDiv, expandButton);
            };
            itemContent.appendChild(expandButton);
        }

        const titleSpan = document.createElement('span');
        titleSpan.textContent = item.title;
        itemContent.appendChild(titleSpan);

        // Updated click handler with better error handling
        itemContent.onclick = async () => {
            try {
                if (item.dest) {
                    await navigateToDestination(item.dest);
                } else if (item.pageNumber) {
                    // If we have a direct page number
                    pageNum = item.pageNumber;
                    await renderPage(pageNum);
                }
                highlightCurrentOutlineItem(itemContent);
            } catch (error) {
                console.error('Error navigating to outline item:', error);
                // Try to extract page number from title if available
                const pageMatch = item.title.match(/Page (\d+)/i);
                if (pageMatch) {
                    const pageNumber = parseInt(pageMatch[1]);
                    if (!isNaN(pageNumber) && pageNumber > 0 && pageNumber <= pdfDoc.numPages) {
                        pageNum = pageNumber;
                        await renderPage(pageNum);
                        highlightCurrentOutlineItem(itemContent);
                    }
                }
            }
        };

        itemDiv.appendChild(itemContent);

        if (item.items && item.items.length > 0) {
            const childList = createOutlineTree(item.items, level + 1);
            itemDiv.appendChild(childList);
        }

        list.appendChild(itemDiv);
    });

    return list;
}

// Add this helper function for safer page navigation
async function navigateToPage(pageNumber) {
    try {
        if (pageNumber > 0 && pageNumber <= pdfDoc.numPages) {
            pageNum = pageNumber;
            await renderPage(pageNum);
            return true;
        }
        return false;
    } catch (error) {
        console.error('Error navigating to page:', error);
        return false;
    }
}
function handleSearch(event) {
    const searchTerm = event.target.value.toLowerCase();
    const outlineItems = document.querySelectorAll('.outline-item');
    
    outlineItems.forEach(item => {
        const content = item.dataset.searchContent;
        if (!searchTerm || content.includes(searchTerm)) {
            item.style.display = 'flex';
            // Show parent containers
            let parent = item.parentElement;
            while (parent && parent.classList.contains('outline-item-container')) {
                parent.style.display = 'block';
                parent = parent.parentElement;
            }
        } else {
            item.style.display = 'none';
        }
    });
}

function toggleSidebar() {
    const sidebar = document.getElementById('sidebar');
    const toggleButton = document.getElementById('toggleSidebar');
    sidebarVisible = !sidebarVisible;
    
    sidebar.style.width = sidebarVisible ? '300px' : '0';
    sidebar.style.padding = sidebarVisible ? '20px' : '0';
    toggleButton.textContent = sidebarVisible ? '?' : '?';
}

function toggleShortcutsPanel() {
    const panel = document.getElementById('shortcutsPanel');
    shortcutsPanelVisible = !shortcutsPanelVisible;
    panel.style.display = shortcutsPanelVisible ? 'block' : 'none';
}
function cleanupLoadingStates() {
    LoadingManager.clear();
    const errorDiv = document.getElementById('error');
    if (errorDiv) {
        errorDiv.style.display = 'none';
    }
}
// Initialize viewer when DOM is loaded
document.addEventListener('DOMContentLoaded', init);

// Add cleanup handlers
window.addEventListener('unload', () => {
    try {
        LoadingManager.clear();
        log('Cleaning up resources');
        pageCache.clear();
        if (pdfDoc) {
            pdfDoc.destroy();
        }
        // Clean up any open windows
        if (window.printWindow) {
            window.printWindow.close();
        }
        // Revoke any object URLs
        if (window.objectUrls) {
            window.objectUrls.forEach(url => URL.revokeObjectURL(url));
        }
    } catch (error) {
        console.error('Cleanup error:', error);
    }
});

// Add PDF.js fallback loader
if (typeof pdfjsLib === 'undefined') {
    try {
        const script = document.createElement('script');
        script.src = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js';
        script.onerror = () => {
            showError('Failed to load PDF viewer. Please try again later.');
        };
        script.onload = () => {
            pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';
            init();
        };
        document.head.appendChild(script);
    } catch (error) {
        console.error('Error loading PDF.js:', error);
        showError('Failed to initialize PDF viewer');
    }
} else {
    pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';
}
window.addEventListener('error', function(e) {
    cleanupLoadingStates();
    showError('An error occurred while loading the document.');
    console.error('Loading error:', e.error);
});

window.addEventListener('unhandledrejection', function(e) {
    cleanupLoadingStates();
    showError('Failed to load the document.');
    console.error('Promise rejection:', e.reason);
});
</script>");

            // Add HTML structure
            builder.AppendLine(@"
<body>
    <div id=""container"">
        <div id=""sidebar"">
            <div class=""outline-header"">
                <h3>Document Outline</h3>
                <button class=""outline-toggle"" onclick=""toggleSidebar()"" id=""toggleSidebar"">?</button>
            </div>
            <div id=""outline""></div>
        </div>
        <div id=""mainContent"">
            <div id=""progressBar"">
                <div id=""progressValue""></div>
            </div>
            <div id=""toolbar"">
    <div class=""toolbar-group"">
        <button onclick=""onPrevPage()"" id=""prevButton"">Previous</button>
        <span class=""page-info"">Page: <span id=""pageNum""></span> / <span id=""pageCount""></span></span>
        <button onclick=""onNextPage()"" id=""nextButton"">Next</button>
    </div>
   <div class=""toolbar-group zoom-controls"">
    <button onclick=""onZoomOut()"" id=""zoomOut"">-</button>
    <span id=""zoomLevel"">150%</span>
    <button onclick=""onZoomIn()"" id=""zoomIn"">+</button>
</div>
    <!-- Add these new buttons -->
    <div class=""toolbar-group"">
        <button onclick=""downloadPdf()"">Download</button>
    </div>
    <div id=""error""></div>
</div>
            <div id=""viewerContainer"">
                <canvas id=""pdfViewer""></canvas>
                <div id=""loadingIndicator"" style=""display: none;"">
    <div id=""loadingSpinner""></div>
    <span class=""loading-text"">Loading document...</span>
</div>
            </div>
        </div>
<div id=""errorOverlay"" style=""display: none; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.5); z-index: 2000;"">
    <div style=""position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); background: white; padding: 20px; border-radius: 8px; text-align: center;"">
        <h3>Error</h3>
        <p id=""errorMessage""></p>
        <div style=""display: flex; gap: 10px; justify-content: center;"">
            <button onclick=""location.reload()"">Retry</button>
            <button class=""close-error"" onclick=""closeError()"">Close</button>
        </div>
    </div>
</div>
    </div>
    <div id=""shortcutsPanel"">
        <h4>Keyboard Shortcuts</h4>
        <ul class=""shortcuts-list"">
            <li><span class=""keyboard-key"">?</span> Previous page</li>
            <li><span class=""keyboard-key"">?</span> Next page</li>
            <li><span class=""keyboard-key"">Ctrl</span> + <span class=""keyboard-key"">+</span> Zoom in</li>
            <li><span class=""keyboard-key"">Ctrl</span> + <span class=""keyboard-key"">-</span> Zoom out</li>
            <li><span class=""keyboard-key"">Ctrl</span> + <span class=""keyboard-key"">0</span> Reset zoom</li>
            <li><span class=""keyboard-key"">Ctrl</span> + <span class=""keyboard-key"">F</span> Search</li>
            <li><span class=""keyboard-key"">Ctrl</span> + <span class=""keyboard-key"">B</span> Toggle sidebar</li>
            <li><span class=""keyboard-key"">?</span> Show/hide shortcuts</li>
        </ul>
    </div>
</body>");

            builder.AppendLine("</html>");
            return builder.ToString();
        }
    }


}
