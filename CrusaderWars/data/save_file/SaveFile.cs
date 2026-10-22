using System;
using System.Linq;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using System.Collections.Generic;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using CrusaderWars.client;

namespace CrusaderWars.data.save_file
{
    internal static class SaveFile
    {

        internal static void Uncompress(string? selectedSavePath = null)
        {
            string lastSavePath = selectedSavePath ?? Properties.Settings.Default.VAR_dir_save.Replace(@"save games", "last_save.ck3");
            string myLastSavePath = @".\data\save_file_data\last_save.zip";
            string gamestatePath = @".\data\save_file_data\gamestate_file\";

            File.Copy(lastSavePath, myLastSavePath, true);

            ReadMetaData(lastSavePath);
            ExtractGamestate(lastSavePath, gamestatePath);
        }

        internal static void Compress()
        {

            string editedGamestatePath = @".\data\save_file_data\gamestate";
            string newSaveFilePath = @".\data\save_file_data\battle_results.zip";
            string metadataPath = @".\data\save_file_data\metadata.txt";
            DEFAULTCompressFileWithMetadata(editedGamestatePath, newSaveFilePath, metadataPath);
        }

        internal static void Finish()
        {
            string newSaveFilePath = @".\data\save_file_data\battle_results.zip";
            if (DevelopmentRuntime.Enabled)
            {
                // A development UI run exercises the full importer, then publishes
                // a separate test save. Never replace the user's last save or
                // continue-game target, and retain intermediates for verification.
                string results = Path.Combine(Directory.GetCurrentDirectory(), "results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
                Directory.CreateDirectory(results);
                string resultSave = Path.Combine(results, "battle_results.ck3");
                File.Copy(newSaveFilePath, resultSave);
                string input = Path.Combine(Directory.GetCurrentDirectory(), "data", "save_file_data", "last_save.zip");
                string? archive(string source, string name)
                {
                    if (!File.Exists(source)) return null;
                    string target = Path.Combine(results, name);
                    File.Copy(source, target);
                    return target;
                }
                string? archivedInput = archive(input, "native-input.ck3");
                string? archivedHandoff = archive(@".\data\attila_battle\battle_log_snippet.txt", "native-handoff.txt");
                archive(@".\data\stat_fidelity_fates.json", "character-fates.json");
                string? archivedStatistics = StatFidelity.StatFidelitySession.HasBoundExport
                    ? archive(@".\data\stat_fidelity_result_statistics.json", "native-result-statistics.json") : null;
                string gameDirectory = Path.GetDirectoryName(Properties.Settings.Default.VAR_attila_path) ?? "";
                string nativeAar = Properties.Settings.Default.VAR_log_attila;
                if (!File.Exists(nativeAar) && !string.IsNullOrEmpty(gameDirectory)) nativeAar = Path.Combine(gameDirectory, "data", "BattleResults_log.txt");
                string? archivedAar = archive(nativeAar, "native-attila-result.txt");
                string digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
                File.WriteAllText(Path.Combine(results, "result-manifest.json"), JsonSerializer.Serialize(new {
                    createdUtc = DateTime.UtcNow, developmentRuntime = true, resultSave,
                    snapshot = StatFidelity.StatFidelitySession.Export.BattleId,
                    combat = data.battle_results.BattleResult.CombatID, combatResult = data.battle_results.BattleResult.ResultID,
                    archivedInput, archivedHandoff, archivedAar, archivedStatistics,
                    inputSaveSha256 = archivedInput != null ? digest(archivedInput) : null,
                    handoffSha256 = archivedHandoff != null ? digest(archivedHandoff) : null,
                    attilaResultSha256 = archivedAar != null ? digest(archivedAar) : null,
                    resultStatisticsSha256 = archivedStatistics != null ? digest(archivedStatistics) : null,
                    outputSaveSha256 = digest(resultSave), personalCampaignWritten = false,
                    validationLimit = "Produced by the application importer; CK3 reload and battle-evidence verification are separate checks."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Program.Logger.Debug("Development battle result saved separately: " + resultSave);
                return;
            }
            string lastSavePath = Properties.Settings.Default.VAR_dir_save.Replace("save games", "last_save.ck3");
            string normalSavePath = Properties.Settings.Default.VAR_dir_save + @"\battle_results.ck3";

            File.Copy(newSaveFilePath, lastSavePath, true);
            File.Copy(newSaveFilePath, normalSavePath, true);
            EditContinueGameJson();
            DeleteOlderFiles();
        }

        static void DEFAULTCompressFileWithMetadata(string fileToZip, string zipFilePath, string metadata)
        {
            byte[] metadataBytes = File.ReadAllBytes(metadata);
            ValidateMetadataPrefix(metadataBytes);
            // Temporary file for creating the zip content
            string tempZipPath = Path.GetTempFileName();

            // Step 1: Create the ZIP file normally
            using (FileStream zipToCreate = new FileStream(tempZipPath, FileMode.Create))
            {
                using (ZipArchive archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create))
                {
                    // Add the actual file to the ZIP archive
                    string fileName = Path.GetFileName(fileToZip);
                    ZipArchiveEntry fileEntry = archive.CreateEntry(fileName);

                    using (Stream fileStream = new FileStream(fileToZip, FileMode.Open, FileAccess.Read))
                    using (Stream entryStream = fileEntry.Open())
                    {
                        fileStream.CopyTo(entryStream);
                    }
                }
            }

            // Step 2: Write metadata and then append the zip content
            using (FileStream finalZipStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write))
            {
                // Write metadata
                finalZipStream.Write(metadataBytes, 0, metadataBytes.Length);

                // Append the ZIP data
                using (FileStream tempZipStream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read))
                {
                    tempZipStream.CopyTo(finalZipStream);
                }
            }

            // Clean up the temporary file
            File.Delete(tempZipPath);
        }

        static void EditContinueGameJson()
        {
            string jsonPath = Properties.Settings.Default.VAR_dir_save.Replace("save games", "continue_game.json");
            string keyToUpdate = "title";  // The key you want to update
            string newValue = "battle_results.ck3";    // The new value for the key

            UpdateJsonKey(jsonPath, keyToUpdate, newValue);
        }

        static void UpdateJsonKey(string jsonFilePath, string keyToUpdate, string newValue)
        {
            // Read the JSON file
            string jsonString = File.ReadAllText(jsonFilePath);

            // Parse JSON into a dictionary
            var jsonDocument = JsonDocument.Parse(jsonString);
            var root = jsonDocument.RootElement;

            // Convert root to a mutable dictionary
            var jsonDictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root.GetRawText());

            // Check if the key exists
            if (jsonDictionary != null && jsonDictionary.ContainsKey(keyToUpdate)) // Added null check for jsonDictionary
            {
                // Update the key's value
                jsonDictionary[keyToUpdate] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(newValue));

                // Serialize the dictionary back to JSON
                string updatedJsonString = JsonSerializer.Serialize(jsonDictionary, new JsonSerializerOptions { WriteIndented = true });

                // Write the updated JSON back to the file
                File.WriteAllText(jsonFilePath, updatedJsonString);
            }
            else
            {
                Console.WriteLine($"Key '{keyToUpdate}' not found in JSON file.");
            }
        }

        static void DeleteOlderFiles()
        {
            string myLastSavePath = @".\data\save_file_data\last_save.zip";
            string editedGamestatePath = @".\data\save_file_data\gamestate";
            string gamestatePath = @".\data\save_file_data\gamestate_file\gamestate";
            string newSaveFilePath = @".\data\save_file_data\battle_results.zip";

            string[] files_to_delete = new string[] { myLastSavePath, editedGamestatePath, gamestatePath, newSaveFilePath };
            foreach (string file in files_to_delete)
            {
                if (File.Exists(file))
                {
                    // If this is the original gamestate, create a backup before deleting
                    if (file == gamestatePath)
                    {
                        string backupPath = file + ".bak";
                        Program.Logger.Debug($"Backing up original gamestate to {backupPath}");
                        File.Copy(file, backupPath, true);
                    }
                    File.Delete(file);
                }
            }

        }


        public static void ExtractGamestate(string zipFilePath, string extractPath)
        {
            Directory.CreateDirectory(extractPath);
            string destination = Path.Combine(extractPath, "gamestate");
            if (File.Exists(@".\data\save_file_data\gamestate"))
                File.Delete(@".\data\save_file_data\gamestate");


            using (FileStream fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read))
            {
                long zipDataStart = FindStartOfZipData(fs);

                if (zipDataStart >= 0)
                {
                    using (FileStream zipStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read))
                    {
                        zipStream.Seek(zipDataStart, SeekOrigin.Begin);

                        using (ICSharpCode.SharpZipLib.Zip.ZipFile zipFile = new ICSharpCode.SharpZipLib.Zip.ZipFile(zipStream))
                        {
                            ZipEntry entry = zipFile.GetEntry("gamestate") ?? throw new InvalidDataException("CK3 save archive has no gamestate member.");
                            using Stream zipStreamEntry = zipFile.GetInputStream(entry);
                            using FileStream fileStream = File.Create(destination);
                            zipStreamEntry.CopyTo(fileStream);
                        }
                    }
                }
                else
                {
                    // Native debug saves can be plaintext. Ordinary 'PK' text is
                    // not a ZIP header and must not select a false embedded archive.
                    File.Copy(zipFilePath, destination, true);
                }
            }

        }


        static long FindStartOfZipData(FileStream fs)
        {
            uint signature = 0;
            for (long offset = 0; offset < Math.Min(fs.Length, 16 * 1024 * 1024); offset++)
            {
                signature = (signature << 8) | (byte)fs.ReadByte();
                if (signature == 0x504b0304) return offset - 3;
            }

            return -1;  // Indicate that the signature was not found
        }

        static void ReadMetaData(string myLastSavePath)
        {
            using (var input = File.OpenRead(myLastSavePath))
            {
                long zipStart = FindStartOfZipData(input);
                if (zipStart >= 0)
                {
                    input.Position = 0;
                    byte[] prefix = new byte[checked((int)zipStart)];
                    input.ReadExactly(prefix);
                    ValidateMetadataPrefix(prefix);
                    File.WriteAllBytes(@".\data\save_file_data\metadata.txt", prefix);
                    return;
                }
            }
            using (StreamReader streamReader = new StreamReader(myLastSavePath))
            using (StreamWriter streamWriter = new StreamWriter(@".\data\save_file_data\metadata.txt", false, new UTF8Encoding(false)))
            {
                streamWriter.NewLine = "\n";
                string? line; // Changed to nullable string
                while ((line = streamReader.ReadLine()) != null)
                {
                    if (line == "}")
                    {
                        streamWriter.WriteLine(line);
                        break;
                    }


                    streamWriter.WriteLine(line);

                }
            }
        }

        public static void ValidateMetadataPrefix(byte[] prefix)
        {
            // Native CK3 SAV0102 has a 24-byte ASCII header and an embedded
            // eight-hex-digit METADATA BYTE length. CRLF conversion or counting
            // Unicode characters instead of bytes moves ZIP to an invalid offset.
            if (prefix.Length < 7 || Encoding.ASCII.GetString(prefix, 0, 7) != "SAV0102") return;
            if (prefix.Length < 24 || prefix[23] != (byte)'\n' ||
                !int.TryParse(Encoding.ASCII.GetString(prefix, 15, 8), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int declared) || declared != prefix.Length - 24)
                throw new InvalidDataException("CK3 metadata byte length/header is inconsistent. Preserve the exact native prefix; no malformed result container was published.");
        }


    }
}
