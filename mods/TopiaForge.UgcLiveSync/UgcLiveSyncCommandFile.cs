using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TopiaForge.UgcLiveSync
{
    /// <summary>
    /// Launcher/CLI → game command file, written next to the UGC live-sync config. The mod polls it from the
    /// Unity main thread and deletes it after parsing so commands are one-shot.
    /// </summary>
    [DataContract]
    public sealed class UgcLiveSyncCommandFile
    {
        public const int CurrentSchemaVersion = 2;
        public const string StopCommand = "stop";
        public const int MaxFileBytes = 16 * 1024;
        public static readonly TimeSpan MaxCommandAge = TimeSpan.FromMinutes(10);

        public UgcLiveSyncCommandFile()
        {
            SeedDefaults();
        }

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [DataMember(Name = "command")]
        public string Command { get; set; } = string.Empty;

        [DataMember(Name = "cleanup")]
        public bool Cleanup { get; set; }

        [DataMember(Name = "createdUtc")]
        public string CreatedUtc { get; set; } = string.Empty;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            SchemaVersion = CurrentSchemaVersion;
            Command = string.Empty;
            Cleanup = false;
            CreatedUtc = string.Empty;
        }

        public string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(UgcLiveSyncCommandFile));
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, this);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static UgcLiveSyncCommandFile FromJson(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(UgcLiveSyncCommandFile));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty));
            var command = serializer.ReadObject(stream) as UgcLiveSyncCommandFile
                ?? throw new InvalidDataException("UGC live-sync command JSON produced no command object.");
            if (command.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "UGC live-sync command schemaVersion must be " + CurrentSchemaVersion + ".");
            }

            return command;
        }

        /// <summary>Reads a command with a strict size bound so the main thread cannot be forced into a huge allocation.</summary>
        public static UgcLiveSyncCommandFile ReadFrom(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes)
            {
                throw new InvalidDataException("UGC live-sync command exceeds " + MaxFileBytes + " bytes.");
            }

            var bytes = new byte[Math.Max(1, (int)stream.Length)];
            var total = 0;
            while (true)
            {
                if (total == bytes.Length)
                {
                    if (total >= MaxFileBytes)
                    {
                        if (stream.ReadByte() >= 0)
                        {
                            throw new InvalidDataException("UGC live-sync command grew beyond " + MaxFileBytes + " bytes.");
                        }

                        break;
                    }

                    Array.Resize(ref bytes, Math.Min(MaxFileBytes, Math.Max(256, bytes.Length * 2)));
                }

                var read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var json = new UTF8Encoding(false, true).GetString(bytes, 0, total);
            return FromJson(json);
        }

        public static string PathForConfig(string configFilePath)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
            {
                return string.Empty;
            }

            var directory = Path.GetDirectoryName(configFilePath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(configFilePath);
            return Path.Combine(directory, baseName + ".command.json");
        }

        public bool IsStop =>
            string.Equals(Command, StopCommand, StringComparison.OrdinalIgnoreCase);

        /// <summary>Rejects replayed or implausibly future-dated one-shot commands.</summary>
        public bool IsFresh(DateTime utcNow)
        {
            if (!DateTime.TryParse(
                    CreatedUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var created))
            {
                return false;
            }

            var age = utcNow.ToUniversalTime() - created;
            return age >= TimeSpan.FromMinutes(-2) && age <= MaxCommandAge;
        }
    }
}
