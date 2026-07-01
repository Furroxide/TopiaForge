using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Robotopia.ModManager.Core
{
    public static class JsonUtil
    {
        public static T LoadFile<T>(string path, T fallback)
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            using (var stream = File.OpenRead(path))
            {
                return Deserialize<T>(stream);
            }
        }

        public static void SaveFile<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = File.Create(path))
            {
                Serialize(stream, value);
            }
        }

        public static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return Deserialize<T>(stream);
            }
        }

        public static T Deserialize<T>(Stream stream)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            var value = serializer.ReadObject(stream);
            if (value == null)
            {
                throw new InvalidDataException("JSON document produced a null value.");
            }

            return (T)value;
        }

        public static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                Serialize(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static void Serialize<T>(Stream stream, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            serializer.WriteObject(stream, value);
        }

        public static T Clone<T>(T value)
        {
            return Deserialize<T>(Serialize(value));
        }
    }
}
