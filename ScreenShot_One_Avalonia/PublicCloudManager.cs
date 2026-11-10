using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    public static class PublicCloudManager
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task<string> UploadImageAsync(string filePath)
        {
            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent("fileupload"), "reqtype");

                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
                form.Add(fileContent, "fileToUpload", Path.GetFileName(filePath));

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://catbox.moe/user/api.php")
                {
                    Content = form
                };
                request.Headers.Add("User-Agent", "Screenshot_One_App/1.0 (Windows)");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadAsStringAsync()).Trim();
            }
        }
    }
}
