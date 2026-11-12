namespace Linguacode.Audio;
internal static class FileOperations { internal static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(); }
