using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using LUC.DVVSet;

namespace LightClient
{
    public class LightClient
    {
        public const Int32 FILE_UPLOAD_CHUNK_SIZE = 2000000;

        public LightClient() { }

        public async Task<FileUploadResponse> Upload(string host, string user_id, string bucket_id, string fullPath, string filePrefix, string guid = "")
        {
            var requestUri = PostUploadUri();
            //var startTime = DateTime.UtcNow;
            var timeStamp = FromDateTimeToUnixTimeStamp(File.GetLastWriteTimeUtc(fullPath)).ToString();
            var version = IncrementVersion(user_id, timeStamp);
            var fileInfo = new FileInfo(fullPath);

            var chunkUploadState = new ChunkUploadState
            {
                ChunkRequestUri = requestUri,
                Guid = guid,
                IsLastChunk = fileInfo.Length < FILE_UPLOAD_CHUNK_SIZE
            };


            var uploadParams = new Dictionary<string, string>
                {
                    {"user_id", user_id},
                    {"version", version},
                    {"hex_prefix", filePrefix},
                };

            if (!string.IsNullOrEmpty(guid))
            {
                uploadParams.Add("guid", guid);
            }

            return await ResponseOfIterativelyUploadFile(fileInfo, chunkUploadState, uploadParams, filePrefix);

            string PostUploadUri()
            {
                var result = Combine(host, "riak", "upload", bucket_id);
                return result;
            }


            long FromDateTimeToUnixTimeStamp(DateTime dateTime)
            {
                var result = (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                return (long)result;
            }
        }

        private string Combine(params string[] uri)
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


        private string IncrementVersion(string userId, string timestamp, string oldversion = "")
        {
            var dvvset = new Dvvdotnet();
            var dot = new Clock();
            if (oldversion == "" || oldversion == null) dot = dvvset.Update(new Clock(timestamp), userId);
            else
            {
                var incomeClock = Clock.StringToClock(oldversion);
                dot = dvvset.Update(incomeClock, userId);
            }
            var d = Clock.ClockToString(dot);
            var version = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(d));

            return version;
        }

        private async Task<FileUploadResponse> ResponseOfIterativelyUploadFile(FileInfo fileInfo, ChunkUploadState uploadState,
    Dictionary<string, string> uploadParams, string filePrefix)
        {
            string fullPath = fileInfo.FullName;
            var calculatedMd5S = new List<string>();
            var md5OfFullFile = FileUploadResponse.CalculateMd5Hash(fullPath);
            string baseRequestUrl = uploadState.ChunkRequestUri;

            foreach (var currentPlainBytes in FileUploadResponse.IterateFileChunksWithoutFileBlocking(fullPath, offset: 0))
            {
                var currentLocalMd5 = FileUploadResponse.CalculateMd5Hash(currentPlainBytes);
                calculatedMd5S.Add(currentLocalMd5);

                Console.WriteLine();

                var percents = uploadState.PartNumber * FILE_UPLOAD_CHUNK_SIZE / (double)fileInfo.Length;
                Console.WriteLine($"Upload part[{uploadState.PartNumber}] for file {fileInfo.Name}. Uploaded {percents:P2}");

                MultipartFormDataContent multipartFormData;
                if (uploadState.IsFirstChunk && !uploadParams.ContainsKey("guid"))
                {
                    multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, currentPlainBytes, uploadState, uploadParams, fileInfo);

                    var responseGetGuid = await ServerUploadResponse(multipartFormData, uploadState, currentLocalMd5,
                        currentPlainBytes, fileInfo, whetherGetGuid: true);
                    if (!responseGetGuid.IsSuccess)
                    {
                        return responseGetGuid;
                    }

                    string guidValue;
                    if (!uploadParams.ContainsKey("guid"))
                    {
                        guidValue = uploadState.LastResponse.Guid;
                        uploadParams.Add(FileUploadRequest.PropGuid, guidValue);
                    }
                    else
                    {
                        guidValue = uploadParams[FileUploadRequest.PropGuid];
                    }

                    AdsExtensions.TryWriteGuidAndLocalPathMarkersIfNotTheSame(fullPath, guidValue);

                    if (fileInfo.Length <= UploadConstants.SingleChunkMaxSize)
                    {
                        return responseGetGuid;
                    }
                }

                if (uploadState.LastResponse?.UploadId != null)
                {
                    uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";
                }

                //uploadState.IncrementVersion(uploadParams, CurrentUserProvider.LoggedUser.Id, operationTime.OriginalModifiedDateTime);

                //add method ServerRequestToPostChunk
                multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, currentPlainBytes, uploadState, uploadParams, fileInfo);
                var responseChunkUpload = await ServerUploadResponse(multipartFormData, uploadState, currentLocalMd5,
                    currentPlainBytes, fileInfo, operationTime, whetherGetGuid: false);

                if (uploadState.IsLastChunk)
                {
                    return responseChunkUpload;
                }

                var responseIfChanged = ResponseIfChangedWhileUploadFile(fullPath, operationTime.OriginalModifiedDateTime);
                if (!responseIfChanged.IsSuccess)
                {
                    return responseIfChanged;
                }

                //uploadState.LastResponse.Version = IncrementedVersion(uploadParams, uploadState.LastResponse.Version, CurrentUserProvider.LoggedUser.Id, operationTime.TimeStamp);
                //}
                //add method handle ServerRequestException
            }

            return new FileUploadResponse
            {
                IsSuccess = false,
                Message = $"Technical error. For big file method '{nameof(ByteArrayExtensions.IterateFileChunks)}' was missed somehow..."
            };
        }

        private MultipartFormDataContent MultipartFormData(List<String> calculatedMd5S, String md5OfChunk, Byte[] bytes,
            ChunkUploadState uploadState, Dictionary<String, String> uploadParams, FileInfo fileInfo)
        {
            var boundary = "-----" + DateTime.Now.Ticks.ToString("x");
            var multiPartContent = new MultipartFormDataContent(boundary);

            //In a normal HTTP response, the Content-Disposition header is an indication that the expected response content will be displayed in the browser as a web page or part of a web page, or as an attachment that can then be downloaded and saved locally.
            multiPartContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "files[]",
                FileName = fileInfo.Name,
                Size = bytes.Length
            };

            foreach (var param in uploadParams)
            {
                multiPartContent.Add(new StringContent(param.Value), param.Key);
            }

            var bytemd5 = new ByteArrayContent(bytes);
            bytemd5.Headers.ContentMD5 = Encoding.ASCII.GetBytes(md5OfChunk);
            //multiPartContent.Add(bytemd5, FileUploadRequest.PropFiles, Path.GetFileName(fileInfo.FullName));
            multiPartContent.Add(bytemd5, "files[]", fileInfo.Name);

            multiPartContent.Add(new StringContent(md5OfChunk), "md5");

            var md5OfFullFile = FileUploadResponse.CalculateMd5Hash(fileInfo.FullName);
            multiPartContent.Headers.ContentMD5 = Encoding.ASCII.GetBytes(md5OfFullFile);

            var endByte = multiPartContent.AddContentRange(uploadState, fileInfo);    //add ContentRange

            if (uploadState.IsLastChunk)
            {
                multiPartContent.AddETags(calculatedMd5S);

                var i = 0;
                Console.WriteLine("Last lap!");
                foreach (var calculatedItemMd5 in calculatedMd5S)
                {
                    Console.WriteLine($"Chunk: {i} - md5 {calculatedItemMd5}");
                    i++;
                }
            }

            return multiPartContent;
        }

    }
}
