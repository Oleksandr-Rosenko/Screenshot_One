using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace ScreenShot_One_Avalonia
{
    public static class GoogleDriveManager
    {
        private static readonly string[] Scopes = { DriveService.Scope.DriveFile };
        private static readonly string ApplicationName = "Screenshot_One";
        private static DriveService? _service;

        private static async Task AuthenticateAsync()
        {
            if (_service != null) return;
            UserCredential credential;

            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "ScreenShot_One_Avalonia.credentials.json";

            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new FileNotFoundException($"Ресурс '{resourceName}' не знайдено всередині EXE.");

                string credPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "token.json");
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(stream).Secrets,
                    Scopes, "user", CancellationToken.None, new FileDataStore(credPath, true));
            }

            _service = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
        }

        private static async Task<string> GetOrCreateFolderAsync(string folderName)
        {
            if (_service == null) throw new InvalidOperationException("Сервіс Google Drive не ініціалізовано.");
            var request = _service.Files.List();
            request.Q = $"mimeType='application/vnd.google-apps.folder' and name='{folderName}' and trashed=false";
            request.Spaces = "drive";
            request.Fields = "files(id, name)";
            var result = await request.ExecuteAsync();

            if (result.Files != null && result.Files.Count > 0) return result.Files[0].Id;

            var folder = await _service.Files.Create(new Google.Apis.Drive.v3.Data.File() { Name = folderName, MimeType = "application/vnd.google-apps.folder" }).ExecuteAsync();
            return folder.Id;
        }

        public static async Task<string> UploadImageAsync(string filePath)
        {
            await AuthenticateAsync();
            if (_service == null) throw new InvalidOperationException("Помилка сервісу Google Drive.");
            string folderId = await GetOrCreateFolderAsync("Screenshot_One");

            var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = Path.GetFileName(filePath), Parents = new List<string> { folderId } };

            FilesResource.CreateMediaUpload request;

            // ФІКС: FileShare.ReadWrite дозволяє читати файл, навіть якщо він відкритий деінде
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                request = _service.Files.Create(fileMetadata, stream, "image/png");
                request.Fields = "id, webViewLink";
                await request.UploadAsync();
            }

            var file = request.ResponseBody;
            await _service.Permissions.Create(new Permission { Type = "anyone", Role = "reader" }, file.Id).ExecuteAsync();
            return file.WebViewLink;
        }

        public static async Task<string> UploadVideoAsync(string filePath)
        {
            await AuthenticateAsync();
            if (_service == null) throw new InvalidOperationException("Помилка сервісу Google Drive.");
            string folderId = await GetOrCreateFolderAsync("Screenshot_One");

            var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = Path.GetFileName(filePath), Parents = new List<string> { folderId } };

            FilesResource.CreateMediaUpload request;

            // ФІКС: FileShare.ReadWrite та формат video/mp4
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                request = _service.Files.Create(fileMetadata, stream, "video/mp4");
                request.Fields = "id, webViewLink";

                var response = await request.UploadAsync();

                if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                {
                    throw response.Exception ?? new Exception("Google Drive відхилив завантаження файлу.");
                }
            }

            var file = request.ResponseBody;
            if (file == null) throw new Exception("Google Drive не повернув дані файлу.");

            await _service.Permissions.Create(new Permission { Type = "anyone", Role = "reader" }, file.Id).ExecuteAsync();
            return file.WebViewLink;
        }
    }
}
