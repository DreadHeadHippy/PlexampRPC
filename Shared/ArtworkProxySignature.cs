using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PlexampRPC.Shared {
    public static partial class ArtworkProxySignature {
        private static readonly UTF8Encoding utf8 = new(false, true);

        [GeneratedRegex(@"\A/library/metadata/[0-9]+/thumb(?:/[0-9]+)?\z", RegexOptions.CultureInvariant)]
        private static partial Regex ThumbnailPathPattern();

        public static byte[] ReadKey(string key) {
            try {
                byte[] bytes = Convert.FromBase64String(key);
                if (bytes.Length == 32)
                    return bytes;
            }
            catch (FormatException) { }
            throw new ArgumentException("Artwork proxy key must be a base64-encoded 32-byte value.");
        }

        public static bool IsValidPath(string path) => path.Length <= 200 && ThumbnailPathPattern().IsMatch(path);

        public static string Encode(string value) => EncodeBytes(Encoding.UTF8.GetBytes(value));

        public static bool TryDecodePath(string encoded, out string path) {
            path = string.Empty;
            if (encoded.Length > 267 || !TryDecodeBytes(encoded, out byte[] bytes))
                return false;
            try {
                path = utf8.GetString(bytes);
                return IsValidPath(path) && Encode(path) == encoded;
            }
            catch (DecoderFallbackException) {
                return false;
            }
        }

        public static string Sign(byte[] key, int resolution, string encodedPath) =>
            EncodeBytes(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{resolution}:{encodedPath}")));

        public static bool Verify(byte[] key, int resolution, string encodedPath, string signature) {
            if (signature.Length != 43 || !TryDecodeBytes(signature, out byte[] actual))
                return false;
            byte[] expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{resolution}:{encodedPath}"));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        private static string EncodeBytes(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool TryDecodeBytes(string encoded, out byte[] bytes) {
            bytes = [];
            if (encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') || encoded.Length % 4 == 1)
                return false;
            try {
                string value = encoded.Replace('-', '+').Replace('_', '/');
                bytes = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
                return true;
            }
            catch (FormatException) {
                return false;
            }
        }
    }
}
