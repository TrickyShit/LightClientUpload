﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodeFluent.Runtime.BinaryServices;
using Newtonsoft.Json;

namespace LightClient
{
    public abstract class BaseResponse
    {
        public Boolean IsSuccess { get; set; } = true;
        public String Message { get; set; }
    }

    internal class LoginRequest
    {
        [JsonProperty("login")]
        public String Login { get; set; }

        [JsonProperty("password")]
        public String Password { get; set; }
    }

    public class GroupSubResponse
    {
        [JsonProperty("id")]
        public String Id { get; set; }

        [JsonProperty("name")]
        public String Name { get; set; }

        [JsonProperty("bucket_id")]
        public String BucketId { get; set; }
    }

    public class LoginResponse : BaseResponse
    {
        [JsonProperty("id")]
        public String Id { get; set; }

        [JsonProperty("token")]
        public String Token { get; set; }

        [JsonProperty("tenant_Id")]
        public String TenantId { get; set; }

        [JsonProperty("login")]
        public String Login { get; set; }

        [JsonProperty("staff")] 
        public Boolean IsAdmin { get; set; }

        [JsonProperty("groups")]
        public List<GroupSubResponse> Groups { get; set; }
    }

    public class FileUploadResponse : BaseResponse
    {
        private const String GuidAdsName = "cloud.lightupon.guid";
        private const String LastSeenVersion = "cloud.lightupon.lastseenversion";
        private const String LocalPathAdsName = "cloud.lightupon.path";

        [JsonProperty("guid")]
        public String Guid { get; set; }

        [JsonProperty("orig_name")]
        public String OriginalName { get; set; }

        [JsonProperty("version")]
        public String Version { get; set; }

        [JsonProperty("object_key")]
        public String ObjectKey { get; set; }

        [JsonProperty("upload_id")]
        public String UploadId { get; set; }

        [JsonProperty("end_byte")]
        public String EndByte { get; set; }

        [JsonProperty("md5")]
        public String Md5 { get; set; }

        [JsonProperty("upload_time")]
        public Int64 UploadTime { get; set; }

        [JsonProperty("author_id")]
        public String UserId { get; set; }

        [JsonProperty("author_name")]
        public String UserName { get; set; }

        [JsonProperty("author_tel")]
        public String UserTel { get; set; }

        [JsonProperty("is_locked")]
        public Boolean IsLocked { get; set; }

        [JsonProperty("lock_modified_utc")]
        public String LockModifiedUtc { get; set; }

        [JsonProperty("lock_user_id")]
        public String LockUserId { get; set; }

        [JsonProperty("lock_user_name")]
        public String LockUserName { get; set; }

        [JsonProperty("lock_user_tel")]
        public String LockUserTel { get; set; }

        [JsonProperty("is_deleted")]
        public Boolean IsDeleted { get; set; }

        [JsonProperty("bytes")]
        public Int64 Bytes { get; set; }

        [JsonProperty("width")]
        public String Width { get; set; }

        [JsonProperty("height")]
        public String Height { get; set; }

        public static IEnumerable<Byte[]> IterateFileChunksWithoutFileBlocking(String filePath, Int32 offset)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException();
            }

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fileStream.Length == 0)
            {
                throw new ArgumentException($"Size of the file {filePath} is 0 bytes");
            }

            Int32 chunkSize;
            if (fileStream.Length < LightClient.FileUploadChunkSize)
            {
                chunkSize = (Int32)fileStream.Length;
            }
            else
            {
                chunkSize = LightClient.FileUploadChunkSize;
            }

            //add cycle do while(FileStream.Length - FileStream.Position <)
            var buffer = new Byte[chunkSize];
            for (var countReadBytes = 1; countReadBytes > 0; offset += countReadBytes)
            {
                if (fileStream.Length < offset + chunkSize)
                {
                    chunkSize = (Int32)(fileStream.Length - offset);
                    buffer = new Byte[chunkSize];
                }
                fileStream.Seek(offset, SeekOrigin.Begin);
                countReadBytes = fileStream.Read(buffer, 0, chunkSize);

                fileStream.Dispose();
                yield return buffer;

                fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            }

            fileStream.Dispose();
        }

        public String CalculateMd5Hash(Byte[] filename)
        {
            using (var md5Hash = MD5.Create())
            {
                var sb = new StringBuilder();
                foreach (var data in md5Hash.ComputeHash(filename))
                {
                    sb.Append(data.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public FileUploadResponse ResponseIfChangedWhileUploadFile(String fullPath, DateTime originalModifiedDateTime)
        {
            var currentModifiedDateTime = /*DateTimeExtensions.LastWriteTimeUtcWithCorrectOffset*/File.GetLastWriteTimeUtc(fullPath);

            if (originalModifiedDateTime == currentModifiedDateTime)
                return new FileUploadResponse {IsSuccess = true};

            var message = $"Upload is stopped. File {fullPath} was changed just during uploading process.";
            Console.WriteLine(message);

            return new FileUploadResponse
            {
                IsSuccess = false,
                Message = message
            };
        }

        public static void TryWriteGuidAndLocalPathMarkersIfNotTheSame(FileInfo fi, String guid)
        {
            if (fi == null)
            {
                return;
            }

            var path = fi.FullName;

            var bytes = Encoding.UTF8.GetBytes(path);
            var hexStringWithDashes = BitConverter.ToString(bytes);
            var hexPath = hexStringWithDashes.Replace("-", "");

            var guidAdsPath = $"{path}:{GuidAdsName}";
            var localPathAdsPath = $"{path}:{LocalPathAdsName}";

            var current = "";
            if (NtfsAlternateStream.Exists($"{path}:{GuidAdsName}"))
                 current = NtfsAlternateStream.ReadAllText($"{path}:{GuidAdsName}");

            FileStream stream;

            if (current == guid)
            {
                // Update only local path marker if it is needed.
                var currentLocalPathMarker = "";
                if (NtfsAlternateStream.Exists($"{path}:{LocalPathAdsName}"))
                     currentLocalPathMarker = NtfsAlternateStream.ReadAllText($"{path}:{LocalPathAdsName}");

                if (currentLocalPathMarker != path)
                {
                    //currentLastWriteUtc = File.GetLastWriteTimeUtc(path);//try to remove it

                    stream = NtfsAlternateStream.Open(localPathAdsPath, FileAccess.Write, FileMode.OpenOrCreate,
                        FileShare.None);

                    stream.Close();

                    NtfsAlternateStream.WriteAllText(localPathAdsPath, hexPath);

                    //File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//try to remove it
                }

                return;
            }

            try 
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

            stream = NtfsAlternateStream.Open(guidAdsPath, FileAccess.Write, FileMode.OpenOrCreate, FileShare.None);
            stream.Close();
            NtfsAlternateStream.WriteAllText(guidAdsPath, guid);

            // If guid is created - remember original path of the file.
            stream = NtfsAlternateStream.Open(localPathAdsPath, FileAccess.Write, FileMode.OpenOrCreate,
                FileShare.None);

            stream.Close();

            NtfsAlternateStream.WriteAllText(localPathAdsPath, hexPath);

            //File.SetLastWriteTimeUtc(path, currentLastWriteUtc);//maybe this row should be deleted
        }

        public static void TryWriteLastSeenVersion(FileInfo fi, String version)
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
        private FileUploadResponse _lastResponse;

        internal String ChunkRequestUri { get; set; }

        internal String Guid { get; set; }

        internal Boolean IsFirstChunk { get; set; }

        internal Boolean IsLastChunk { get; set; }

        internal FileUploadResponse LastResponse
        {
            get => _lastResponse;
            set
            {
                _lastResponse = value;

                if (_lastResponse != null)
                {
                    Guid = _lastResponse.Guid;
                }
            }
        }

        internal Int64 PartNumber { get; private set; }

        internal void IncreasePartNumber() => PartNumber++;
    }
}