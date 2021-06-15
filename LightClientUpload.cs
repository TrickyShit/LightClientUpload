using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LUC.DVVSet;

namespace LightClientUpload
{
    public class LightClientUpload
    {
        const Int32 FILE_UPLOAD_CHUNK_SIZE = 2000000;

        public string Host { get; set; }
        public string Bucket_Id { get; set; }
        public string FullPath { get; set; }

        public LightClientUpload() { }

        public LightClientUpload(string host, string bucket_id, string fullPath)
        {
            Host = host;
            Bucket_Id = bucket_id;
            FullPath = fullPath;

            async Task<FileUploadResponse> Preparing()
            {
                Dictionary<String, String> uploadParams;
                var requestUri = PostUploadUri();
                var startTime = DateTime.UtcNow;
                var originalModifiedDateTime = /*DateTimeExtensions.LastWriteTimeUtcWithCorrectOffset*/File.GetLastWriteTimeUtc(fullPath);
                var timeStamp = FromDateTimeToUnixTimeStamp(originalModifiedDateTime).ToString();

                try
                {
                    GetUploadParams(timeStamp, out uploadParams);
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

            void GetInitialUploadState()
            {
                //get filePrefix(realize method GetFilePrefix)

                //create upload state
                var requestUri = PostUploadUri();
                chunkUploadState = new ChunkUploadState
                {
                    ChunkRequestUri = requestUri,
                    Guid = AdsExtensions.ReadGuidMarker(fileFullPath),
                    IsLastChunk = fileInfo.Length < FILE_UPLOAD_CHUNK_SIZE
                };
            }

            string PostUploadUri()
            {
                var result = Combine(host, "riak", "upload", bucket_id);
                return result;
            }

            string Combine(params string[] uri)
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

            void GetUploadParams(string timeStamp, out Dictionary<String, String> uploadParams)
            {
                var userId = CurrentUserProvider.LoggedUser.Id;

                var vectorClock = new Clock();
                var vectorClockVersion = vectorClock.IncrementVersion(userId, timeStamp);
                var filePrefix = ObjectNameProvider.ServerPrefix(fullPath).Result;

                uploadParams = new Dictionary<String, String>
            {
                {"user_id", userId },
                {"version", vectorClockVersion},
                {"hex_prefix", filePrefix },
            };

                var guid = AdsExtensions.ReadGuidMarker(fullPath);
                if (!string.IsNullOrEmpty(guid))
                {
                    uploadParams.Add("guid", guid);
                }
            }

            long FromDateTimeToUnixTimeStamp(DateTime dateTime)
            {
                var result = (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                if (result < 0)
                {
                    // TODO Delete
                }

                return (long)result;
            }

            string IncrementVersion(string userId, string timeStamp, string version = "")
            {
                var dvvset = new Dvvdotnet();
                var dot = new Clock();
                if (version == "" || version == null) dot = dvvset.Update(new Clock(timeStamp), userId);
                else
                {
                    var incomeClock = Clock.StringToClock(version);
                    dot = dvvset.Update(incomeClock, userId);
                }
                var d = dot.ClockToString(dot);
                version = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(d));

                return version;
            }


        }
    }
}
