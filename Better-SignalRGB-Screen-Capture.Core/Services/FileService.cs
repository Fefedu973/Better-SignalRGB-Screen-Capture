using System.Text;

using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;

using Newtonsoft.Json;

namespace Better_SignalRGB_Screen_Capture.Core.Services;

public class FileService : IFileService
{
    private static readonly object FileAccessGate = new();
    public T Read<T>(string folderPath, string fileName)
    {
        lock (FileAccessGate)
        {
            var path = Path.Combine(folderPath, fileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<T>(json);
            }

            return default;

        }
    }

    public void Save<T>(string folderPath, string fileName, T content)
    {
        lock (FileAccessGate)
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            var fileContent = JsonConvert.SerializeObject(content);
            var destination = Path.Combine(folderPath, fileName);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Write beside the destination so replacement is atomic on the same volume.
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(fileContent);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

        }
    }

    public void Delete(string folderPath, string fileName)
    {
        lock (FileAccessGate)
        {
            if (fileName != null && File.Exists(Path.Combine(folderPath, fileName)))
            {
                File.Delete(Path.Combine(folderPath, fileName));
            }

        }
    }
}
