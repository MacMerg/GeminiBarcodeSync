using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System.Net.Http;
using Google.GenAI;
using Serilog;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using File = System.IO.File;
using System.IO;
using System.Linq;
using Google.Apis.Http;
using IHttpClientFactory = System.Net.Http.IHttpClientFactory; // Fixes 'Role', 'Content', and 'Part' errors

namespace MacMerg.BarcodeGeminiSync;

public class BarcodeWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BarcodeWorker> _logger;
    private readonly IConfiguration _config;

    private record BarcodeResult(string Barcode, string? Description, string? ProductInfo);

    public BarcodeWorker(IHttpClientFactory httpClientFactory, ILogger<BarcodeWorker> logger, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Get the path from your config (double check your appsettings.json!)
        string watchPath = _config["Settings:WatchPath"] ?? @"C:\Scans";

        if (!Directory.Exists(watchPath)) Directory.CreateDirectory(watchPath);

        using var watcher = new FileSystemWatcher(watchPath);
        watcher.Filter = "*.jpg"; // Only look for images
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;

        // 2. THE HOOK: When a file is created, call the AI
        watcher.Created += async (s, e) =>
        {
            _logger.LogInformation("New photo detected: {file}", e.Name);

            // Wait a split second for the sync to actually finish writing the file
            await Task.Delay(1000);

            // THE MISSING CALL: This is where the magic happens
            var result = await GetBarcodeFromGemini(e.FullPath, stoppingToken);
            _logger.LogInformation("Gemini Result for {file}: {barcode} / {desc}", e.Name, result.Barcode, result.Description);

            // Append the result and image to the Google Sheet
            try
            {
                await AppendToSheet(result, e.FullPath, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to append to Google Sheet");
            }
        };

        watcher.EnableRaisingEvents = true;

        // 3. Keep the service alive
        _logger.LogInformation("Watching {path}...", watchPath);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }

    }

    private async Task AppendToSheet(BarcodeResult result, string imagePath, CancellationToken cancellationToken)
    {
        // Config
        var spreadsheetId = _config["Google:SpreadsheetId"];
        var serviceAccountFile = _config["Google:ServiceAccountFile"];

        // If no spreadsheetId is provided we will create a new spreadsheet. Credentials are required and
        // are validated later; if missing the method will return early.

        // Authenticate with service account.
        // Support multiple modes:
        // 1) A path to a service account JSON file via Google:ServiceAccountFile
        // 2) The raw service account JSON stored in configuration via Google:ServiceAccountJson
        // 3) A nested JSON object under Google:ServiceAccount in configuration (user secrets)
        GoogleCredential credential;
        // Prefer path-based service account file. If a relative path is provided, resolve it against the app base dir.
        if (!string.IsNullOrWhiteSpace(serviceAccountFile))
        {
            var filePath = Path.IsPathRooted(serviceAccountFile) ? serviceAccountFile : Path.Combine(AppContext.BaseDirectory, serviceAccountFile);
            _logger.LogDebug("Looking for service account file at: {path}", filePath);
            if (!File.Exists(filePath))
            {
                // Fallback: check the per-user user-secrets location for the project's secrets.json
                try
                {
                    var userSecretsId = "dotnet-BarcodeGeminiSync-6ff29cf1-f29b-406e-b4ad-1364d59bd2cf"; // project UserSecretsId
                    var userSecretsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
                    _logger.LogDebug("Service account file not found at configured path; checking user secrets at: {path}", userSecretsPath);
                    if (File.Exists(userSecretsPath))
                    {
                        filePath = userSecretsPath;
                        _logger.LogInformation("Using service account file from user secrets: {path}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error while checking user secrets path");
                }
            }

            if (File.Exists(filePath))
            {
                credential = GoogleCredential.FromFile(filePath).CreateScoped(SheetsService.Scope.Spreadsheets);
                _logger.LogInformation("Using service account file: {path}", filePath);
            }
            else
            {
                _logger.LogWarning("Service account file not found at {path}", filePath);
                credential = null;
            }
        }
        else
        {
            credential = null;
        }

        // If file-based credential not available, fall back to JSON in configuration (user secrets)
        if (credential == null)
        {
            if (!string.IsNullOrWhiteSpace(_config["Google:ServiceAccountJson"]))
            {
                credential = GoogleCredential.FromJson(_config["Google:ServiceAccountJson"]).CreateScoped(SheetsService.Scope.Spreadsheets);
                _logger.LogInformation("Using service account JSON from configuration (Google:ServiceAccountJson)");
            }
            else
            {
                var saSection = _config.GetSection("Google:ServiceAccount");
                if (saSection.Exists())
                {
                    // Build a dictionary from the configuration section (support one level of nesting)
                    var dict = new Dictionary<string, object?>();
                    foreach (var child in saSection.GetChildren())
                    {
                        if (child.GetChildren().Any())
                        {
                            dict[child.Key] = child.GetChildren().ToDictionary(c => c.Key, c => (object?)c.Value);
                        }
                        else
                        {
                            dict[child.Key] = child.Value;
                        }
                    }
                    var json = JsonSerializer.Serialize(dict);
                    credential = GoogleCredential.FromJson(json).CreateScoped(SheetsService.Scope.Spreadsheets);
                    _logger.LogInformation("Using service account from configuration section Google:ServiceAccount");
                }
            }

            if (credential == null)
            {
                _logger.LogWarning("No usable service account credentials found; skipping sheet append");
                return;
            }
        }

        using var sheets = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "BarcodeGeminiSync"
        });

        // Ensure the spreadsheet exists. If no spreadsheetId provided, create a new spreadsheet with a
        // 'scanned_barcodes' sheet and use its id.
        if (string.IsNullOrWhiteSpace(spreadsheetId))
        {
            var newSpreadsheet = new Spreadsheet
            {
                Properties = new SpreadsheetProperties { Title = "Scanned Barcodes" },
                Sheets = new List<Sheet>
                {
                    new Sheet { Properties = new SheetProperties { Title = "scanned_barcodes" } }
                }
            };

            var created = await sheets.Spreadsheets.Create(newSpreadsheet).ExecuteAsync(cancellationToken);
            spreadsheetId = created.SpreadsheetId;
            _logger.LogInformation("Created new spreadsheet {id} with sheet 'scanned_barcodes'", spreadsheetId);

            // Add header row for the new sheet
            var headerRow = new List<object> { "timestamp", "filename", "barcode", "description", "product_info", "file" };
            var headerRange = new ValueRange { Values = new List<IList<object>> { headerRow } };
            var headerReq = sheets.Spreadsheets.Values.Append(headerRange, spreadsheetId, "scanned_barcodes!A1:F1");
            headerReq.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            headerReq.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.OVERWRITE;
            await headerReq.ExecuteAsync(cancellationToken);
        }

        // Load the spreadsheet to ensure the tab exists
        Google.Apis.Sheets.v4.Data.Spreadsheet spreadsheet;
        try
        {
            _logger.LogDebug("Fetching spreadsheet with id {id}", spreadsheetId);
            spreadsheet = await sheets.Spreadsheets.Get(spreadsheetId).ExecuteAsync(cancellationToken);
            var available = (spreadsheet.Sheets ?? new List<Sheet>()).Select(s => s.Properties?.Title ?? "<unnamed>").ToArray();
            _logger.LogInformation("Loaded spreadsheet {id} titled '{title}' with sheets: {sheets}", spreadsheetId, spreadsheet.Properties?.Title, string.Join(", ", available));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load spreadsheet with id {id}. Ensure SpreadsheetId is correct and the service account has access.", spreadsheetId);
            return;
        }

        var sheet = spreadsheet.Sheets?.FirstOrDefault(s => s.Properties?.Title == "scanned_barcodes");
        if (sheet == null)
        {
            var addSheetReq = new Google.Apis.Sheets.v4.Data.Request
            {
                AddSheet = new AddSheetRequest { Properties = new SheetProperties { Title = "scanned_barcodes" } }
            };
            var batch = new BatchUpdateSpreadsheetRequest { Requests = new[] { addSheetReq } };
            await sheets.Spreadsheets.BatchUpdate(batch, spreadsheetId).ExecuteAsync(cancellationToken);

            // add header row to the newly created sheet
            var headerRow = new List<object> { "timestamp", "filename", "barcode", "description", "product_info", "file" };
            var headerRange = new ValueRange { Values = new List<IList<object>> { headerRow } };
            var headerReq = sheets.Spreadsheets.Values.Append(headerRange, spreadsheetId, "scanned_barcodes!A1:F1");
            headerReq.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            headerReq.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.OVERWRITE;
            await headerReq.ExecuteAsync(cancellationToken);
        }

        // Use a relative file path link instead of embedding the image
        var watchPath = _config["Settings:WatchPath"] ?? AppContext.BaseDirectory;
        string fileCell;
        try
        {
            var relative = Path.GetRelativePath(watchPath, imagePath);
            // If relative path goes up (starts with ..), use absolute path instead
            fileCell = relative.StartsWith("..") ? imagePath : relative.Replace('\\', '/');
        }
        catch
        {
            fileCell = imagePath;
        }

        // Build the row values: Timestamp, Filename, Barcode, Description, ProductInfo, File
        // Google Sheets cell limit is roughly 50,000 characters; truncate long text to avoid errors.
        const int googleSheetsCellMax = 50000;
        string descriptionCell = result.Description ?? string.Empty;
        if (descriptionCell.Length > googleSheetsCellMax)
        {
            _logger.LogWarning("Description too long ({len} chars); truncating to {max} chars for Sheets.", descriptionCell.Length, googleSheetsCellMax);
            descriptionCell = descriptionCell.Substring(0, googleSheetsCellMax);
        }

        string productInfoCell = result.ProductInfo ?? string.Empty;
        if (productInfoCell.Length > googleSheetsCellMax)
        {
            _logger.LogWarning("ProductInfo too long ({len} chars); truncating to {max} chars for Sheets.", productInfoCell.Length, googleSheetsCellMax);
            productInfoCell = productInfoCell.Substring(0, googleSheetsCellMax);
        }

        var row = new List<object>
        {
            DateTime.UtcNow.ToString("o"),
            Path.GetFileName(imagePath),
            result.Barcode ?? string.Empty,
            descriptionCell,
            productInfoCell,
            fileCell
        };

        var valueRange = new ValueRange { Values = new List<IList<object>> { row } };
        var appendReq = sheets.Spreadsheets.Values.Append(valueRange, spreadsheetId, "scanned_barcodes!A:F");
        appendReq.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        appendReq.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await appendReq.ExecuteAsync(cancellationToken);
        _logger.LogInformation("Appended scan to sheet 'scanned_barcodes'");
    }

    private async Task<BarcodeResult> GetBarcodeFromGemini(string imagePath, CancellationToken stoppingToken)
    {
        try
        {
            // 1. Prepare image
            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
            string base64Image = Convert.ToBase64String(imageBytes);

            // 2. Setup the "2026 Verified" Endpoint
            string apiKey = _config["Gemini:ApiKey"];
            // Using 'gemini-3-flash' - the current stable version
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}";


            //var listUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
  
            // 3. Build the Request Body and send with retries to avoid truncated outputs
            using var client = _httpClientFactory.CreateClient("gemini");

            string resultJson = null;
            string fullText = null;
            int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                _logger.LogDebug("Gemini request attempt {attempt}/{maxAttempts}", attempt, maxAttempts);

                // Use a prompt that requests strict JSON
                string instruction = attempt == 1
                    ? "Return a JSON object only, no commentary. Format: {\"barcode\":\"<digits or alnum>\", \"description\":\"<brief image description>\", \"product_info\":\"<name, brand, price, any attributes or url if found>\"}. If unreadable return {\"barcode\":\"\",\"description\":\"\",\"product_info\":\"\"}."
                    : "Retry: return the same JSON shape. Re-check the image and OCR carefully.";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { text = instruction },
                                new { inline_data = new { mime_type = "image/jpeg", data = base64Image } }
                            }
                        }
                    },
                    // Increase maxOutputTokens on the first/primary attempt so the model has room to return the full barcode
                    generationConfig = new { temperature = 0.0, maxOutputTokens = 1000 }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // optional: combine service stop token with per-request timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromMinutes(5));

                var response = await client.PostAsync(url, content, cts.Token);
                resultJson = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Gemini raw response (attempt {attempt}): {json}", attempt, resultJson);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("API responded with {status} on attempt {attempt}: {msg}", response.StatusCode, attempt, resultJson);
                    if (attempt == maxAttempts)
                    {
                        _logger.LogError("API Error: {status} - {msg}", response.StatusCode, resultJson);
                        return new BarcodeResult("Error", null, null);
                    }
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                // 4. Parse the 2026 Response Object
                using var doc = JsonDocument.Parse(resultJson);

                var candidate = doc.RootElement.GetProperty("candidates")[0];
                var content2 = candidate.GetProperty("content");

                // The model may split its output across multiple "parts"; join them all.
                var parts = content2.GetProperty("parts");
                var sb = new StringBuilder();
                for (int i = 0; i < parts.GetArrayLength(); i++)
                {
                    if (parts[i].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(t.GetString());
                    }
                }

                fullText = sb.ToString().Trim();
                _logger.LogDebug("Gemini fullText (attempt {attempt}): {text}", attempt, fullText);

                // Try to parse JSON result
                try
                {
                    using var parsedDoc = JsonDocument.Parse(fullText);
                    var root = parsedDoc.RootElement;
                    string parsedBarcode = root.GetProperty("barcode").GetString() ?? string.Empty;
                    string? description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
                    string? productInfo = root.TryGetProperty("product_info", out var p) ? p.GetString() : null;
                    return new BarcodeResult(parsedBarcode, description, productInfo);
                }
                catch (JsonException)
                {
                    // fallback to previous extraction when model didn't obey JSON output
                    var token = Regex.Matches(fullText, "[A-Za-z0-9_-]+").Select(m => m.Value)
                                     .FirstOrDefault(m => Regex.IsMatch(m, "^\\d{8,14}$"))
                                ?? Regex.Matches(fullText, "[A-Za-z0-9_-]+").Select(m => m.Value).OrderByDescending(s => s.Length).FirstOrDefault();
                    return new BarcodeResult(token ?? fullText, null, null);
                }

                // If model explicitly returned 'empty' or nothing, try again or give up
                if (string.Equals(fullText, "empty", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fullText))
                {
                    fullText = null;
                    if (attempt == maxAttempts) return new BarcodeResult(string.Empty, null, null);
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                // If we find a plausible all-digit barcode (8-14 digits) accept it immediately
                var digitMatch = Regex.Match(fullText, "\\d{8,14}");
                if (digitMatch.Success)
                {
                    fullText = digitMatch.Value;
                    break;
                }

                // If the returned text is suspiciously short (like a single digit) try again
                if (fullText.Length < 6 && attempt < maxAttempts)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                // Otherwise accept what we have
                break;
            }

            _logger.LogDebug("Regex matches: {matches}", string.Join(", ", Regex.Matches(fullText ?? string.Empty, "[A-Za-z0-9_-]+").Select(m => m.Value)));

            // Final selection: choose a plausible barcode from the aggregated fullText
            if (string.IsNullOrEmpty(fullText))
            {
                return new BarcodeResult(string.Empty, null, null);
            }

            var matches = Regex.Matches(fullText, "[A-Za-z0-9_-]+").Select(m => m.Value).ToList();
            string barcode = matches.FirstOrDefault(m => Regex.IsMatch(m, "^\\d{8,14}$"));
            if (barcode == null)
            {
                barcode = matches.OrderByDescending(m => m.Length).FirstOrDefault(m => m.Length >= 4);
            }

            _logger.LogDebug("Chosen barcode token: {barcode}", barcode ?? "<none>");

            return new BarcodeResult(string.IsNullOrEmpty(barcode) ? fullText : barcode, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process image.");
            return new BarcodeResult("Error", null, null);
        }
    }
}