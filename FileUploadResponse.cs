using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CodeFluent.Runtime.BinaryServices;
using Newtonsoft.Json;

namespace LightClient
{
    public class FileUploadResponse : BaseResponse
    {
        private const string LockAdsName = "com.dubstack.lock";
        private const string GuidAdsName = "com.dubstack.guid";
        private const string LocalPathAdsName = "com.dubstack.path";
        private const string LastSeenModifiedUtc = "com.dubstack.servermodifiedutc";


        public FileUploadResponse(bool isSuccess, bool isForbidden, string message) : base(isSuccess, isForbidden, message)
        {
        }

        public FileUploadResponse() : base()
        {
        }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("end_byte")]
        public string EndByte { get; set; }

        [JsonProperty("upload_id")]
        public string UploadId { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("md5")]
        public string Md5 { get; set; }

        [JsonProperty("upload_time")]
        public int UploadTime { get; set; }


        public string OriginalName { get; set; }

        public long ModifiedUtc { get; set; }


        public string CalculateMd5Hash(string filename)
        {
            using (MD5 md5Hash = MD5.Create())
            {
                var sb = new StringBuilder();
                foreach (var data in md5Hash.ComputeHash(File.ReadAllBytes(filename)))
                {
                    sb.Append(data.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public string CalculateMd5Hash(byte[] filename)
        {
            using (MD5 md5Hash = MD5.Create())
            {
                var sb = new StringBuilder();
                foreach (var data in md5Hash.ComputeHash(filename))
                {
                    sb.Append(data.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public void AddETags(MultipartFormDataContent content, List<string> md5S)
        {
            var etags = "";

            if (md5S?.Count != 0)
            {
                var part = 1;
                foreach (var md5 in md5S)
                {
                    etags += $"{part},{md5},";
                    part++;
                }

                etags = etags.Remove(etags.Length - 1, 1);
            }

            content.Add(new StringContent(etags), "etags[]");
        }


        public static IEnumerable<byte[]> IterateFileChunksWithoutFileBlocking(string filePath, Int32 offset)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException();
            }
            else
            {
                FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                if (fileStream.Length == 0)
                {
                    throw new ArgumentException($"Size of the file {filePath} is 0 bytes");
                }

                Int32 chunkSize;
                if (fileStream.Length < LightClient.FILE_UPLOAD_CHUNK_SIZE)
                {
                    chunkSize = (Int32)fileStream.Length;
                }
                else
                {
                    chunkSize = LightClient.FILE_UPLOAD_CHUNK_SIZE;
                }

                //add cycle do while(FileStrea.Length - FileStream.Position <) 
                var buffer = new Byte[chunkSize];
                for (Int32 countReadBytes = 1; countReadBytes > 0; offset += countReadBytes)
                {
                    if (fileStream.Length < offset + chunkSize)
                    {
                        chunkSize = (Int32)(fileStream.Length - offset);
                        buffer = new Byte[chunkSize];
                    }
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    countReadBytes = fileStream.Read(buffer, offset: 0, chunkSize);

                    fileStream.Dispose();
                    yield return buffer;

                    fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                }

                fileStream.Dispose();
            }
        }

        public static void TryWriteLastSeenModifiedUtc(string path, long utc)
        {
            if (utc == 0) { }

            var fi = new FileInfo(path);

            try
            {
                var text = utc.ToString();

                var currentLastWriteUtc = File.GetLastWriteTimeUtc(path);

                var stream = NtfsAlternateStream.Open($"{path}:{LastSeenModifiedUtc}", FileAccess.Write,
                    FileMode.OpenOrCreate, FileShare.None);

                stream.Close();

                NtfsAlternateStream.WriteAllText($"{path}:{LastSeenModifiedUtc}", text);

                File.SetLastWriteTimeUtc(path, currentLastWriteUtc);
                Console.WriteLine($"{path} has last seen modified utc {text}");
            }
            catch (DirectoryNotFoundException) { }
        }

        public void TryWriteGuidAndLocalPathMarkersIfNotTheSame(string path, string guid)
        {
            var fi = new FileInfo(path);//add refactoring to this function

            var guidAdsPath = $"{path}:{GuidAdsName}";
            var localPathAdsPath = $"{path}:{LocalPathAdsName}";

            string current = "";
            if (NtfsAlternateStream.Exists($"{path}:{GuidAdsName}"))
            {
                current = NtfsAlternateStream.ReadAllText($"{path}:{GuidAdsName}");
            }

            DateTime currentLastWriteUtc;
            FileStream stream;

            if (current == guid)
            {
                // Update only local path marker if it is needed.
                string currentLocalPathMarker = "";
                if (NtfsAlternateStream.Exists($"{path}:{LocalPathAdsName}"))
                {
                    currentLocalPathMarker = NtfsAlternateStream.ReadAllText($"{path}:{LocalPathAdsName}");
                }

                if (currentLocalPathMarker != path)
                {
                    currentLastWriteUtc = File.GetLastWriteTimeUtc(path);//try to remove it

                    stream = NtfsAlternateStream.Open(localPathAdsPath, FileAccess.Write, FileMode.OpenOrCreate,
                        FileShare.None);

                    stream.Close();
                    NtfsAlternateStream.WriteAllText(localPathAdsPath, ToHexString(path));

                    File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//try to remove it
                }

                return;
            }

            try // TODO RR find proper solution here.
            {
                if (fi.IsReadOnly)
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            currentLastWriteUtc = File.GetLastWriteTimeUtc(path);

            stream = NtfsAlternateStream.Open(guidAdsPath, FileAccess.Write, FileMode.OpenOrCreate, FileShare.None);
            stream.Close();
            NtfsAlternateStream.WriteAllText(guidAdsPath, guid);

            // If guid is created - remember original path of the file.
            stream = NtfsAlternateStream.Open(localPathAdsPath, FileAccess.Write, FileMode.OpenOrCreate,
                FileShare.None);

            stream.Close();

            var bytes = Encoding.UTF8.GetBytes(path);
            var hexStringWithDashes = BitConverter.ToString(bytes);
            var result = hexStringWithDashes.Replace("-", "");

            NtfsAlternateStream.WriteAllText(localPathAdsPath, result);

            File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//maybe this row should be deleted
        }

        string ToHexString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);

            var hexStringWithDashes = BitConverter.ToString(bytes);
            var result = hexStringWithDashes.Replace("-", "");

            return result;
        }

        public FileUploadResponse ResponseIfChangedWhileUploadFile(String fullPath, DateTime originalModifiedDateTime)
        {
            var currentModifiedDateTime = /*DateTimeExtensions.LastWriteTimeUtcWithCorrectOffset*/File.GetLastWriteTimeUtc(fullPath);

            if (originalModifiedDateTime != currentModifiedDateTime)
            {
                String message = $"Upload is stopped. File {fullPath} was changed just during uploading process.";
                Console.WriteLine(message);

                return new FileUploadResponse
                {
                    IsSuccess = false,
                    Message = $"Upload is stopped. File {fullPath} was changed just during uploading process."
                };
            }
            else
            {
                return new FileUploadResponse
                {
                    IsSuccess = true
                };
            }
        }

        public static IEnumerable<byte[]> IterateFileChunks(string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                var buffer = new byte[LightClient.FILE_UPLOAD_CHUNK_SIZE];
                fileStream.Seek(0, SeekOrigin.Begin);
                var bytesRead = fileStream.Read(buffer, 0, LightClient.FILE_UPLOAD_CHUNK_SIZE);

                while (bytesRead > 0)
                {
                    if (bytesRead < LightClient.FILE_UPLOAD_CHUNK_SIZE)
                    {
                        buffer = buffer.Take(bytesRead).ToArray();

                        yield return buffer;
                        break;
                    }

                    yield return buffer;

                    bytesRead = fileStream.Read(buffer, 0, LightClient.FILE_UPLOAD_CHUNK_SIZE);
                }
            }
        }

        public override string ToString()
        {
            var responseStr = $"{nameof(FileUploadResponse)}:\n" +
                $"{nameof(OriginalName)} = {OriginalName};\n" +
                $"{nameof(ModifiedUtc)} = {ModifiedUtc};\n" +
                $"{nameof(Guid)} = {Guid}";

            return responseStr;
        }
    }

    public abstract class BaseResponse : INotificationResult
    {
        public BaseResponse()
        {
        }

        public BaseResponse(bool isSuccess, bool isForbidden, string message)
        {
            IsSuccess = isSuccess;
            IsForbidden = isForbidden;
            Message = message;
        }

        public bool IsSuccess { get; set; } = true;

        public string Message { get; set; }

        public bool IsForbidden { get; set; } = false;
    }

    public interface INotificationResult
    {
        bool IsSuccess { get; set; }

        string Message { get; set; }
    }

    internal class ChunkUploadState // TODO Release 2.0 Range for download Range: 65545-
    {
        internal FileUploadResponse lastResponse;
        internal FileUploadResponse LastResponse
        {
            get { return lastResponse; }
            set
            {
                lastResponse = value;

                if (lastResponse != null)
                {
                    Guid = lastResponse.Guid;
                }
            }
        }

        internal string ChunkRequestUri { get; set; }

        internal bool IsFirstChunk { get; set; }

        internal bool IsLastChunk { get; set; }

        internal long PartNumber { get; private set; }

        internal string Guid { get; set; }

        internal void IncreasePartNumber()
        {
            PartNumber++;
        }

    }
}
