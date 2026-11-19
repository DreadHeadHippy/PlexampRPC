using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PlexampRPC.Data;
using PlexampRPC.Shared;

namespace PlexampRPC.Utils {
    internal sealed class ArtworkService(HttpClient client) {
        private static readonly JsonSerializerOptions serializerOptions = new() { WriteIndented = true };
        private readonly Dictionary<string, (string Url, DateTime CheckedAt)> checkedLinks = [];
        private DateTimeOffset uploadRetryAt = DateTimeOffset.MinValue;

        public async Task<string> GetProxyThumbnailAsync(string proxyUrl, string proxyKey, string path, int resolution) {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out Uri? address) ||
                address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo) ||
                !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
                throw new InvalidOperationException("Artwork proxy URL must be an HTTPS URL without credentials, query, or fragment.");
            if (!ArtworkProxySignature.IsValidPath(path))
                throw new InvalidDataException("Plex provided an unsupported thumbnail path.");
            if (resolution is < 1 or > 1024)
                throw new InvalidDataException("Artwork proxy resolution must be between 1 and 1024 pixels.");

            byte[] key = ArtworkProxySignature.ReadKey(proxyKey);
            string encodedPath = ArtworkProxySignature.Encode(path);
            string signature = ArtworkProxySignature.Sign(key, resolution, encodedPath);
            Uri baseUri = new(address.AbsoluteUri.TrimEnd('/') + "/");
            string link = new Uri(baseUri, $"art/{resolution}/{encodedPath}/{signature}").AbsoluteUri;
            if (link.Length > 256)
                throw new InvalidDataException("Artwork proxy URL is too long for Discord.");

            if (checkedLinks.TryGetValue(path, out var previous) && previous.Url == link &&
                DateTime.UtcNow - previous.CheckedAt < TimeSpan.FromHours(1))
                return link;

            using HttpResponseMessage response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead);
            EnsureSuccess(response, "Artwork proxy request");
            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg")
                throw new InvalidDataException("Artwork proxy returned a non-JPEG response.");
            checkedLinks[path] = (link, DateTime.UtcNow);
            return link;
        }

        public async Task<string> GetThumbnailAsync(Uri server, string? token, string path, string? album, int resolution, string cacheFile) {
            Dictionary<string, ThumbnailData> thumbnails = ReadCache(cacheFile);
            if (thumbnails.TryGetValue(path, out ThumbnailData? cached) && !string.IsNullOrWhiteSpace(cached.Art)) {
                if (checkedLinks.TryGetValue(path, out var previous) && previous.Url == cached.Art &&
                    DateTime.UtcNow - previous.CheckedAt < TimeSpan.FromHours(1))
                    return cached.Art;

                using HttpResponseMessage check = await client.GetAsync(cached.Art, HttpCompletionOption.ResponseHeadersRead);
                if (check.StatusCode != HttpStatusCode.NotFound && check.StatusCode != HttpStatusCode.Gone) {
                    EnsureSuccess(check, "Cached artwork check");
                    checkedLinks[path] = (cached.Art, DateTime.UtcNow);
                    return cached.Art;
                }
                Console.WriteLine("INFO: Cached artwork is no longer available; uploading it again");
            }

            if (DateTimeOffset.UtcNow < uploadRetryAt)
                throw new HttpRequestException($"Artwork upload is rate-limited until {uploadRetryAt:O}", null, HttpStatusCode.TooManyRequests);

            string query = $"photo/:/transcode?width={resolution}&height={resolution}&minSize=1&upscale=1&format=jpg" +
                $"&url={Uri.EscapeDataString(path)}&X-Plex-Token={Uri.EscapeDataString(token ?? string.Empty)}";
            using HttpResponseMessage image = await client.GetAsync(new Uri(server, query));
            EnsureSuccess(image, "Plex artwork request");
            byte[] data = await image.Content.ReadAsByteArrayAsync();

            using MultipartFormDataContent content = new();
            using ByteArrayContent file = new(data);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
                Name = "\"file\"",
                FileName = "\"artwork.jpg\""
            };
            content.Add(file);
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.pixelvault.dev/v1/images") { Content = content };
            request.Headers.UserAgent.ParseAdd("PlexampRPC");
            using HttpResponseMessage upload = await client.SendAsync(request);
            if (!upload.IsSuccessStatusCode) {
                if (upload.StatusCode == HttpStatusCode.TooManyRequests) {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    uploadRetryAt = upload.Headers.RetryAfter?.Date ?? now.Add(upload.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                    if (uploadRetryAt <= now)
                        uploadRetryAt = now.AddMinutes(1);
                }
                string details = await GetUploadErrorAsync(upload);
                throw new HttpRequestException($"Artwork upload failed with HTTP {(int)upload.StatusCode} ({upload.ReasonPhrase}){details}",
                    null, upload.StatusCode);
            }
            using JsonDocument response = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            string? link = response.RootElement.GetProperty("data").GetProperty("url").GetString();
            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new InvalidDataException("Image host returned an invalid artwork URL");

            thumbnails[path] = new() { Name = album ?? "Unknown", Art = link };
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(thumbnails, serializerOptions));
            checkedLinks[path] = (link, DateTime.UtcNow);
            return link;
        }

        private static async Task<string> GetUploadErrorAsync(HttpResponseMessage response) {
            try {
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (body.RootElement.ValueKind == JsonValueKind.Object &&
                    body.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String) {
                    string? text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) {
                        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        return $": {text[..Math.Min(text.Length, 200)]}";
                    }
                }
            }
            catch (JsonException) {
                // Some hosts return non-JSON error pages.
            }
            return string.Empty;
        }

        private static void EnsureSuccess(HttpResponseMessage response, string operation) {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{operation} failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
                    null, response.StatusCode);
        }

        private static Dictionary<string, ThumbnailData> ReadCache(string cacheFile) {
            if (!File.Exists(cacheFile))
                return [];
            try {
                return JsonSerializer.Deserialize<Dictionary<string, ThumbnailData>>(File.ReadAllText(cacheFile))
                    ?? throw new JsonException("Artwork cache is empty");
            }
            catch (JsonException e) {
                Console.WriteLine($"WARN: Unable to read artwork cache; rebuilding it: {e.Message}");
                return [];
            }
        }
    }
}
