using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using LUC.DVVSet;

namespace LightClientUpload
{
    public class PreparingToUpload
    {
        const Int32 FILE_UPLOAD_CHUNK_SIZE = 2000000;

        public string Host { get; set; }
        public string Bucket_Id { get; set; }
        public string FullPath { get; set; }
        public string Version { get; set; }

        public async Task<FileUploadResponse> Preparing(string urlHost, string bucket_id, string fullPath, string version = "")
        {
            Dictionary<String, String> uploadParams;
            var requestUri = PostUploadUri(urlHost, bucket_id);
            var startTime = DateTime.UtcNow;
            var originalModifiedDateTime = /*DateTimeExtensions.LastWriteTimeUtcWithCorrectOffset*/File.GetLastWriteTimeUtc(fullPath);
            var timeStamp = FromDateTimeToUnixTimeStamp(originalModifiedDateTime).ToString();

            try
            {
                GetUploadParams(fullPath, timeStamp, out uploadParams);
            }
            catch (ArgumentException ex)
            {
                return new FileUploadResponse
                {
                    IsSuccess = false,
                    Message = ex.Message
                };
            }

            return await ResponseOfIterativelyUploadFile(fullPath, uploadState, uploadParams, filePrefix, operationTime);
        }

        private void GetInitialUploadState(string fullPath, string urlHost, string bucket_id)
        {
            //get filePrefix(realize method GetFilePrefix)

            //create upload state
            var requestUri = PostUploadUri(urlHost, bucket_id);
            chunkUploadState = new ChunkUploadState
            {
                ChunkRequestUri = requestUri,
                Guid = AdsExtensions.ReadGuidMarker(fileFullPath),
                IsLastChunk = fileInfo.Length < FILE_UPLOAD_CHUNK_SIZE
            };
        }

        public static string PostUploadUri(string host, string bucketName)
        {
            var result = Combine(host, "riak", "upload", bucketName);
            return result;
        }

        public static string Combine(params string[] uri)
        {
            uri[0] = uri[0].TrimEnd('/');
            string result = "";
            result += uri[0] + "/";
            for (var i = 1; i < uri.Length; i++)
            {
                uri[i] = uri[i].TrimStart('/');
                uri[i] = uri[i].TrimEnd('/');
                result += uri[i] + "/";
            }
            return result;
        }

        private static void GetUploadParams(string fullPath, string timeStamp, out Dictionary<String, String> uploadParams)
        {
            var userId = CurrentUserProvider.LoggedUser.Id;

            var vectorClock = new Clock();
            var vectorClockVersion = vectorClock.IncrementVersion(userId, timeStamp);
            filePrefix = ObjectNameProvider.ServerPrefix(fileInfo.FullName).Result;

            uploadParams = new Dictionary<String, String>
            {
                {"user_id", userId },
                {"version", vectorClockVersion},
                {"hex_prefix", filePrefix },
            };

            var guid = AdsExtensions.ReadGuidMarker(fileInfo.FullName);
            if (!String.IsNullOrEmpty(guid))
            {
                uploadParams.Add("guid", guid);
            }
        }

        public long FromDateTimeToUnixTimeStamp(this DateTime dateTime)
        {
            var result = (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            if (result < 0)
            {
                // TODO Delete
            }

            return (long)result;
        }

    }
}
