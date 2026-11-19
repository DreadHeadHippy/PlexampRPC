using System.Net;
using System.Net.Http.Headers;
using PlexampRPC.Shared;
using PlexampRPC.Utils;

byte[] keyBytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
string key = Convert.ToBase64String(keyBytes);
string path = "/library/metadata/123/thumb/456";
string encoded = ArtworkProxySignature.Encode(path);
string signature = ArtworkProxySignature.Sign(keyBytes, 128, encoded);
Assert(ArtworkProxySignature.TryDecodePath(encoded, out string decoded) && decoded == path, "Valid path must round-trip.");
Assert(ArtworkProxySignature.Verify(keyBytes, 128, encoded, signature), "Valid signature must verify.");
Assert(!ArtworkProxySignature.Verify(keyBytes, 129, encoded, signature), "Modified resolution must fail verification.");
Assert(!ArtworkProxySignature.Verify(keyBytes, 128, ArtworkProxySignature.Encode("/library/metadata/124/thumb/456"), signature),
    "Modified path must fail verification.");
Assert(!ArtworkProxySignature.TryDecodePath(ArtworkProxySignature.Encode("/library/metadata/123/thumb/../123"), out _),
    "Non-thumbnail paths must be rejected.");

int calls = 0;
using HttpClient client = new(new Handler(request => {
    calls++;
    Assert(request.Method == HttpMethod.Get, "Proxy must be checked using GET.");
    Assert(request.RequestUri!.AbsoluteUri == $"https://art.example.test/art/128/{encoded}/{signature}", "Unexpected proxy URL.");
    Assert(!request.RequestUri.AbsoluteUri.Contains(key), "Key must never appear in the URL.");
    HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
    return response;
}));
ArtworkService service = new(client);
string link = await service.GetProxyThumbnailAsync("https://art.example.test", key, path, 128);
Assert(link.StartsWith("https://art.example.test/art/"), "Signed URL must be returned.");
Assert(await service.GetProxyThumbnailAsync("https://art.example.test", key, path, 128) == link && calls == 1,
    "Recently checked artwork must be reused.");
await AssertFailure(() => service.GetProxyThumbnailAsync("http://art.example.test", key, path, 128), "HTTP proxy must be rejected.");
await AssertFailure(() => service.GetProxyThumbnailAsync("https://art.example.test", "invalid", path, 128), "Invalid key must be rejected.");
await AssertFailure(() => service.GetProxyThumbnailAsync("https://art.example.test", key, "/status/sessions", 128), "Non-thumbnail path must be rejected.");
Console.WriteLine("Artwork proxy client checks passed.");

static void Assert(bool condition, string message) {
    if (!condition)
        throw new Exception(message);
}

static async Task AssertFailure(Func<Task<string>> action, string message) {
    try {
        await action();
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) {
        return;
    }
    throw new Exception(message);
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
