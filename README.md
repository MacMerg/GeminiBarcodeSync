BarcodeGeminiSync
BarcodeGeminiSync is a .NET 10 worker service designed to automate the process of cataloging physical items. It watches a local directory for new product photos (e.g., taken from a smartphone), uses the Gemini 2.0 Flash API to perform OCR and product identification, and syncs the results—including timestamps, barcodes, and descriptions—directly to a Google Sheet.

Features
Automated File Watching: Monitors a designated folder for new .jpg files.

AI-Powered Vision: Leverages Gemini 3 Flash to extract barcodes, product names, brands, and brief descriptions from images.

Google Sheets Integration: Automatically appends scan data to a spreadsheet, creating a new one or a specific scanned_barcodes tab if they don't exist.

Resilient Processing: Includes built-in retry logic and configurable timeouts to handle API fluctuations.

Structured Logging: Uses Serilog for detailed console and rolling file logs.

Tech Stack
.NET 10

Google Gemini API (Generative AI)

Google Sheets API v4

Serilog (Logging)

Prerequisites
Google Cloud Project:

Enable the Google Sheets API.

Create a Service Account and download the JSON credentials file.

Gemini API Key: Obtain an API key from Google AI Studio.

Target Folder: A local directory (like a synced Dropbox or OneDrive folder) where your phone saves images.

Configuration
Update appsettings.json with your specific details:

JSON
{
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY"
  },
  "Google": {
    "ServiceAccountFile": "path/to/your/service-account.json",
    "SpreadsheetId": "YOUR_SPREADSHEET_ID_HERE"
  },
  "Settings": {
    "WatchPath": "C:\\Users\\Name\\Pictures\\Scans"
  }
}
Note: If SpreadsheetId is left blank, the application will create a new sheet titled "Scanned Barcodes" on the first run.

Important: Google Sheets Permissions
For the application to write data to an existing spreadsheet, you must share the Google Sheet with your service account email address (found in your service account JSON file) and grant it Editor permissions.

Setup and Installation
Clone the repository:

Bash
git clone https://github.com/yourusername/BarcodeGeminiSync.git
cd BarcodeGeminiSync
Restore dependencies:

Bash
dotnet restore
User Secrets (Optional): Instead of appsettings.json, you can use .NET User Secrets to store your sensitive API keys:

Bash
dotnet user-secrets set "Gemini:ApiKey" "your_key"
Run the application:

Bash
dotnet run
How It Works
Detection: The service detects a new .jpg in the WatchPath.

Analysis: The image is sent to Gemini with a system prompt to extract structured JSON containing the barcode and product details.

Verification: The service uses regex to validate barcode formats (8-14 digits) to ensure data accuracy.

Sync: The data is appended as a new row in the specified Google Sheet with a relative path link to the original image.
