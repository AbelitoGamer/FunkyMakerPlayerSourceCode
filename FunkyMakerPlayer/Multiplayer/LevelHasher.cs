using System;
using System.IO;
using System.Security.Cryptography;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Utility class for calculating SHA256 hashes of level files.
    /// Used to verify that both players have the same level file.
    /// </summary>
    public static class LevelHasher
    {
        /// <summary>
        /// Calculate SHA256 hash of a file.
        /// </summary>
        public static string CalculateHash(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"[LevelHasher] File not found: {filePath}");
                    return "";
                }

                using (var sha256 = SHA256.Create())
                {
                    using (var stream = File.OpenRead(filePath))
                    {
                        byte[] hashBytes = sha256.ComputeHash(stream);
                        return Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelHasher] Error calculating hash: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Calculate SHA256 hash of an in-memory level payload without touching disk.
        /// </summary>
        public static string CalculateHash(byte[] data)
        {
            if (data == null || data.Length == 0)
                return "";

            try
            {
                byte[] hashBytes = SHA256.HashData(data);
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelHasher] Error calculating byte hash: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Verify that two files have the same hash.
        /// </summary>
        public static bool VerifyMatch(string filePath1, string filePath2)
        {
            string hash1 = CalculateHash(filePath1);
            string hash2 = CalculateHash(filePath2);
            
            if (string.IsNullOrEmpty(hash1) || string.IsNullOrEmpty(hash2))
                return false;
            
            return hash1.Equals(hash2, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compare hash with expected value.
        /// </summary>
        public static bool VerifyHash(string filePath, string expectedHash)
        {
            string actualHash = CalculateHash(filePath);
            
            if (string.IsNullOrEmpty(actualHash) || string.IsNullOrEmpty(expectedHash))
                return false;
            
            return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
