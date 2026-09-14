using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DungeonTools.Save.File {
    public static class AesEncryptionProvider {
        //Aes.Create() rather than new AesManaged(): the derived crypto types are obsolete
        //from .NET 6 (SYSLIB0021). Same AES implementation, so ECB + Zeros padding with
        //this key and IV produces byte-identical output on net48 and net10.
        private static readonly SymmetricAlgorithm Algorithm = CreateAlgorithm();

        private static SymmetricAlgorithm CreateAlgorithm() {
            SymmetricAlgorithm algorithm = Aes.Create();
            algorithm.Mode = CipherMode.ECB;
            algorithm.Padding = PaddingMode.Zeros;
            algorithm.IV = new byte[16];
            algorithm.Key = new byte[] {
                0x5C, 0xEB, 0x9D, 0x0A, 0xEB, 0xB9, 0x5A, 0xC0, 0x27, 0x0B, 0x0A, 0xF6, 0x75, 0x3D, 0xFC, 0x0E,
                0xE3, 0xE6, 0x8B, 0xB6, 0x94, 0x79, 0x02, 0x0F, 0x24, 0x30, 0xE2, 0xEA, 0x00, 0x2B, 0xD4, 0xC9,
            };
            return algorithm;
        }

        public static ValueTask<Stream> DecryptAsync(Stream encrypted) {
            return TransformAsync(encrypted, Algorithm.CreateDecryptor());
        }

        public static ValueTask<Stream> EncryptAsync(Stream decrypted) {
            return TransformAsync(decrypted, Algorithm.CreateEncryptor());
        }

        private static async ValueTask<Stream> TransformAsync(Stream input, ICryptoTransform transform) {
            MemoryStream output = new MemoryStream();

            using (CryptoStream crypto = new CryptoStream(input, transform, CryptoStreamMode.Read)) {
                await crypto.CopyToAsync(output);
            }

            output.Seek(0, SeekOrigin.Begin);
            return output;
        }
    }
}
