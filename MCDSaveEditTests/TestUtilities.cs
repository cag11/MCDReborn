using DungeonTools.Save.File;
using MCDSaveEdit.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEditTests
{
    public static class TestUtilities
    {
        public static DirectoryInfo? tryGetProjectDirectoryInfo(string? currentPath = null)
        {
            var directory = new DirectoryInfo(currentPath ?? Directory.GetCurrentDirectory());
            while (directory != null && !directory.GetFiles("*.csproj").Any())
            {
                directory = directory.Parent;
            }
            return directory;
        }

        /// <summary>
        /// TestData next to the .csproj. Prefers the copy beside the test binary so it
        /// works from any runner, and falls back to the project directory.
        /// </summary>
        public static string testDataDirectory()
        {
            var besideBinary = Path.Combine(AppContext.BaseDirectory, "TestData");
            if (Directory.Exists(besideBinary)) { return besideBinary; }

            var projectDirectory = tryGetProjectDirectoryInfo()?.FullName;
            Assert.IsNotNull(projectDirectory, "Could not locate the test project directory");
            return Path.Combine(projectDirectory!, "TestData");
        }

        public static async Task<Stream?> decryptFileIntoStream(string filePath)
        {
            var file = new FileInfo(filePath);
            using FileStream inputStream = file.OpenRead();
            bool encrypted = SaveFileHandler.IsFileEncrypted(inputStream);
            if (!encrypted)
            {
                Assert.Fail($"The file \"{file.Name}\" was in an unexpected format.");
                return null;
            }
            Stream? stream = await FileProcessHelper.Decrypt(inputStream);
            if (stream == null)
            {
                Assert.Fail($"Content of file \"{file.Name}\" could not be converted to a supported format.");
                return null;
            }
            return stream;
        }

        public static Stream generateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Top-level keys whose value changed or vanished between the save that was read
        /// and the one written back. A key that was null and is now absent is not a loss,
        /// and neither is the reverse - both mean "no value".
        /// </summary>
        public static IEnumerable<string> findLostValues(string inputJson, string outputJson)
        {
            using var inputDocument = JsonDocument.Parse(inputJson);
            using var outputDocument = JsonDocument.Parse(outputJson);

            foreach (var property in inputDocument.RootElement.EnumerateObject())
            {
                bool wasNull = property.Value.ValueKind == JsonValueKind.Null;
                if (!outputDocument.RootElement.TryGetProperty(property.Name, out var written))
                {
                    if (!wasNull) { yield return $"{property.Name} (dropped)"; }
                    continue;
                }

                if (wasNull || written.ValueKind == JsonValueKind.Null) { continue; }

                //GetRawText keeps nested objects and arrays comparable without walking them.
                if (!string.Equals(property.Value.GetRawText(), written.GetRawText(), StringComparison.Ordinal))
                {
                    yield return $"{property.Name} (changed)";
                }
            }
        }

        public static int countUnequalLines(string[] inputLines, string[] outputLines)
        {
            int inputLineIndex = 0;
            int outputLineIndex = 0;
            int totalUnequal = 0;
            for (; inputLineIndex < inputLines.Length && outputLineIndex < outputLines.Length; inputLineIndex++, outputLineIndex++)
            {
                if (inputLines[inputLineIndex].EndsWith("null,")) { inputLineIndex++; }
                if (outputLines[outputLineIndex].EndsWith("null,")) { outputLineIndex++; }

                if (!inputLines[inputLineIndex].Equals(outputLines[outputLineIndex]))
                {
                    if (inputLines[inputLineIndex].StartsWith("\"power\"") && outputLines[outputLineIndex].StartsWith("\"power\"")) { continue; }
                    if (inputLines[inputLineIndex].StartsWith("\"priceMultiplier\"") && outputLines[outputLineIndex].StartsWith("\"priceMultiplier\"")) { continue; }
                    if (inputLines[inputLineIndex].StartsWith("\"rebateFraction\"") && outputLines[outputLineIndex].StartsWith("\"rebateFraction\"")) { continue; }
                    totalUnequal++;
                    Console.WriteLine(">>> {0}:{1}", inputLineIndex, inputLines[inputLineIndex]);
                    Console.WriteLine("<<< {0}:{1}", outputLineIndex, outputLines[outputLineIndex]);
                }
            }
            return totalUnequal;
        }

    }
}
