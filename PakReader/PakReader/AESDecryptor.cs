using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace PakReader
{
    static class AESDecryptor
    {
        public const int BLOCK_SIZE = 16 * 8; // 128
        //Aes rather than the obsolete derived type (SYSLIB0022). BLOCK_SIZE here is
        //128 bits, and at a 128-bit block the two are the same cipher - they only
        //diverge at 160/192/256, which .NET's Aes never supported. Drop-in, identical.
        static readonly Aes Cipher;
        static readonly Dictionary<byte[], ICryptoTransform> CachedTransforms = new Dictionary<byte[], ICryptoTransform>();

        static AESDecryptor()
        {
            Cipher = Aes.Create();
            Cipher.Mode = CipherMode.ECB;
            Cipher.Padding = PaddingMode.Zeros;
            Cipher.BlockSize = BLOCK_SIZE;
        }

        static ICryptoTransform GetDecryptor(byte[] key)
        {
            if (!CachedTransforms.TryGetValue(key, out var ret))
            {
                CachedTransforms[key] = ret = Cipher.CreateDecryptor(key, null);
            }
            return ret;
        }

        public static int FindKey(byte[] data, IList<byte[]> keys)
        {
            byte[] block = new byte[BLOCK_SIZE];
            for (int i = 0; i < keys.Count; i++)
            {
                using (var crypto = GetDecryptor(keys[i]))
                    crypto.TransformBlock(data, 0, BLOCK_SIZE, block, 0);
                int stringLen = BitConverter.ToInt32(block, 0);
                if (stringLen > 512 || stringLen < -512)
                    continue;
                if (stringLen < 0)
                {
                    if (BitConverter.ToUInt16(block, (stringLen - 1) * 2 + 4) != 0)
                        continue;
                }
                else
                {
                    if (block[stringLen - 1 + 4] != 0)
                        continue;
                }
                return i;
            }
            return -1;
        }

        public static byte[] DecryptAES(byte[] data, byte[] key) =>
            GetDecryptor(key).TransformFinalBlock(data, 0, data.Length);
    }
}
