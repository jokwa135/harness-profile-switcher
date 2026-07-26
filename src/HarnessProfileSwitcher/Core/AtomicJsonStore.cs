using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace HarnessProfileSwitcher.Core
{
    public sealed class AtomicJsonStore
    {
        public T Read<T>(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Required JSON state file is missing.", path);
            }

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                object value = serializer.ReadObject(stream);
                if (value == null)
                {
                    throw new SerializationException("JSON state was empty: " + path);
                }
                return (T)value;
            }
        }

        public void Write<T>(string path, T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));

            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
    }
}
