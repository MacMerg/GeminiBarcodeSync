BarcodeGeminiSync
BarcodeGeminiSync is a .NET 10 worker service that automates product cataloging by watching a local folder for photos, using Gemini 3 Flash to extract data via AI vision, and syncing results directly to Google Sheets. It is designed to bridge the gap between physical inventory and digital organization by monitoring a directory—such as one synced with a smartphone's camera roll—to catalog items in real-time.

Features
Automated Monitoring: Uses FileSystemWatcher to detect new .jpg files in a configured "WatchPath".

AI Vision Processing: Leverages Gemini 3 Flash to identify products and extract barcode strings (8-14 digits) from images.

Google Sheets Integration: Automatically appends scan details, including timestamps, barcodes, product info, and local file links, to a scanned_barcodes worksheet.

Resilient Processing: Includes retry logic and a 5-minute HttpClient timeout to handle high-resolution image uploads.

Structured Logging: Implements comprehensive daily rolling file logs and console output via Serilog.

Tech Stack
Framework: .NET 10

AI: Google Gemini API (Generative AI)

Cloud: Google Sheets API v4

Logging: Serilog

Prerequisites
Google Cloud Project: Enable the Google Sheets API and create a Service Account.

Gemini API Key: Obtain a key from Google AI Studio.

Target Folder: A local directory (like a synced Dropbox or OneDrive folder) where images are saved.

Configuration
Update appsettings.json with your credentials:

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
Note: If SpreadsheetId is empty, the application will attempt to create a new sheet titled "Scanned Barcodes" on the first run.

Important: Permissions
To allow the service to write data, you must share your Google Sheet with the Service Account email address (found in your JSON credentials file) and grant it Editor permissions.

Installation
Clone the repository: git clone <your-repo-url>

Restore dependencies: dotnet restore

Run the application: dotnet run
