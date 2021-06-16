using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LightClient
{
    public class FileUploadResponse : BaseResponse
    {
        public FileUploadResponse() : base()
        {
        }

        public FileUploadResponse(bool isSuccess, bool isForbidden, string message) : base(isSuccess, isForbidden, message)
        {
        }

        public string OriginalName { get; set; }

        public long ModifiedUtc { get; set; }

        public string Guid { get; set; }

        public static string CalculateMd5Hash(string filename)
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

        public static string CalculateMd5Hash(byte[] filename)
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

        public static void AddETags(this MultipartFormDataContent content, List<string> md5S)
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
        internal ChunkUploadState()
        {
            IsFirstChunk = true;
            IsLastChunk = false;
            PartNumber = 0;
        }

        internal string ChunkRequestUri { get; set; }

        internal bool IsFirstChunk { get; set; }

        internal bool IsLastChunk { get; set; }

        internal long PartNumber { get; private set; }

        internal string Guid { get; set; }
    }

}
