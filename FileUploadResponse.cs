using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodeFluent.Runtime.BinaryServices;
using Newtonsoft.Json;

namespace LightClient
{
    internal abstract class BaseResponse
    {
        public BaseResponse()
        {
        }

        public bool IsSuccess { get; set; } = true;
        public string Message { get; set; }
    }

    internal class LoginRequest
    {
        [JsonProperty("login")]
        public string Login { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }
    }


    internal class FileUploadResponse : BaseResponse
    {
        private const string GuidAdsName = "cloud.lightupon.guid";
        private const string LastSeenVersion = "cloud.lightupon.lastseenversion";
        private const string LocalPathAdsName = "cloud.lightupon.path";
        private const string LockAdsName = "cloud.lightupon.lock";

        public FileUploadResponse() : base()
        {
        }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("orig_name")]
        public string OriginalName { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("object_key")]
        public string ObjectKey { get; set; }

        [JsonProperty("upload_id")]
        public string UploadId { get; set; }

        [JsonProperty("end_byte")]
        public string EndByte { get; set; }

        [JsonProperty("md5")]
        public string Md5 { get; set; }

        [JsonProperty("upload_time")]
        public long UploadTime { get; set; }

        [JsonProperty("author_id")]
        public string UserId { get; set; }

        [JsonProperty("author_name")]
        public string UserName { get; set; }

        [JsonProperty("author_tel")]
        public string UserTel { get; set; }

        [JsonProperty("is_locked")]
        public bool IsLocked { get; set; }

        [JsonProperty("lock_modified_utc")]
        public string LockModifiedUtc { get; set; }

        [JsonProperty("lock_user_id")]
        public string LockUserId { get; set; }

        [JsonProperty("lock_user_name")]
        public string LockUserName { get; set; }

        [JsonProperty("lock_user_tel")]
        public string LockUserTel { get; set; }

        [JsonProperty("is_deleted")]
        public bool IsDeleted { get; set; }

        [JsonProperty("bytes")]
        public long Bytes { get; set; }

        [JsonProperty("width")]
        public string Width { get; set; }

        [JsonProperty("height")]
        public string Height { get; set; }

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

                //add cycle do while(FileStream.Length - FileStream.Position <)
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

        public FileUploadResponse ResponseIfChangedWhileUploadFile(string fullPath, DateTime originalModifiedDateTime)
        {
            var currentModifiedDateTime = /*DateTimeExtensions.LastWriteTimeUtcWithCorrectOffset*/File.GetLastWriteTimeUtc(fullPath);

            if (originalModifiedDateTime != currentModifiedDateTime)
            {
                string message = $"Upload is stopped. File {fullPath} was changed just during uploading process.";
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

        public static void TryWriteGuidAndLocalPathMarkersIfNotTheSame(FileInfo fi, string guid)
        {
            if (fi == null)
            {
                return;
            }

            var path = fi.FullName;

            var bytes = Encoding.UTF8.GetBytes(path);
            var hexStringWithDashes = BitConverter.ToString(bytes);
            var hex_path = hexStringWithDashes.Replace("-", "");

            var guidAdsPath = $"{path}:{GuidAdsName}";
            var localPathAdsPath = $"{path}:{LocalPathAdsName}";

            string current = "";
            if (NtfsAlternateStream.Exists($"{path}:{GuidAdsName}"))
                 current = NtfsAlternateStream.ReadAllText($"{path}:{GuidAdsName}");

            DateTime currentLastWriteUtc;
            FileStream stream;

            if (current == guid)
            {
                // Update only local path marker if it is needed.
                string currentLocalPathMarker = "";
                if (NtfsAlternateStream.Exists($"{path}:{LocalPathAdsName}"))
                     currentLocalPathMarker = NtfsAlternateStream.ReadAllText($"{path}:{LocalPathAdsName}");

                if (currentLocalPathMarker != path)
                {
                    //currentLastWriteUtc = File.GetLastWriteTimeUtc(path);//try to remove it

                    stream = NtfsAlternateStream.Open(localPathAdsPath, FileAccess.Write, FileMode.OpenOrCreate,
                        FileShare.None);

                    stream.Close();


                    NtfsAlternateStream.WriteAllText(localPathAdsPath, hex_path);

                    //File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//try to remove it
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

            NtfsAlternateStream.WriteAllText(localPathAdsPath, hex_path);

            //File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//maybe this row should be deleted
        }

        public static void TryWriteLastSeenVersion(FileInfo fi, string version)
        {
            if (fi == null)
            {
                return;
            }

            try
            {
                var stream = NtfsAlternateStream.Open($"{fi.FullName}:{version}", FileAccess.Write,
                    FileMode.OpenOrCreate, FileShare.None);

                stream.Close();

                NtfsAlternateStream.WriteAllText($"{fi.FullName}:{LastSeenVersion}", version);

                Console.WriteLine($"{fi.FullName} has last seen version {version}");
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal class ChunkUploadState // TODO Release 2.0 Range for download Range: 65545-
    {
        internal FileUploadResponse lastResponse;

        internal string ChunkRequestUri { get; set; }

        internal string Guid { get; set; }

        internal bool IsFirstChunk { get; set; }

        internal bool IsLastChunk { get; set; }

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

        internal long PartNumber { get; private set; }

        internal void IncreasePartNumber()
        {
            PartNumber++;
        }
    }
}