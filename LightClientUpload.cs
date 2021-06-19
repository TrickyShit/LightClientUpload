using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using LUC.DVVSet;
using LUC.Services.Implementation;

namespace LightClient
{
    public class LightClient
    {
        public const Int32 FILE_UPLOAD_CHUNK_SIZE = 2000000;

        public LightClient() { }

        public async Task<FileUploadResponse> Upload(string host, string token, string user_id, string bucket_id, string fullPath, string filePrefix, string guid = "")
        {
            var requestUri = Combine(host, "riak", "upload", bucket_id);
            //var startTime = DateTime.UtcNow;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            var timeStamp = (lastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds.ToString();
            var version = IncrementVersion(user_id, timeStamp);
            var fileInfo = new FileInfo(fullPath);

            var chunkUploadState = new ChunkUploadState
            {
                ChunkRequestUri = requestUri,
                Guid = guid,
                IsLastChunk = fileInfo.Length < FILE_UPLOAD_CHUNK_SIZE,
                IsFirstChunk = true
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

            return await ResponseOfIterativelyUploadFile(fileInfo, token, chunkUploadState, uploadParams, filePrefix);
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

        private async Task<FileUploadResponse> ResponseOfIterativelyUploadFile(FileInfo fileInfo, string token, ChunkUploadState uploadState,
    Dictionary<string, string> uploadParams, string filePrefix)
        {
            var fileUploadResponse = new FileUploadResponse();
            string fullPath = fileInfo.FullName;
            var calculatedMd5S = new List<string>();
            var md5OfFullFile = fileUploadResponse.CalculateMd5Hash(fullPath);
            string baseRequestUrl = uploadState.ChunkRequestUri;

            foreach (var currentPlainBytes in FileUploadResponse.IterateFileChunksWithoutFileBlocking(fullPath, offset: 0))
            {
                var currentLocalMd5 = fileUploadResponse.CalculateMd5Hash(currentPlainBytes);
                calculatedMd5S.Add(currentLocalMd5);

                var percents = uploadState.PartNumber * FILE_UPLOAD_CHUNK_SIZE / (double)fileInfo.Length;
                Console.WriteLine($"Upload part[{uploadState.PartNumber}] for file {fileInfo.Name}. Uploaded {percents:P2}");

                MultipartFormDataContent multipartFormData;
                if (uploadState.IsFirstChunk && !uploadParams.ContainsKey("guid"))
                {
                    multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, currentPlainBytes, uploadState, uploadParams, fileInfo);
                    bool whetherGetGuid = true;

                    var responseGetGuid = await ServerUploadResponse(multipartFormData, token, uploadState, currentLocalMd5,
                        currentPlainBytes, fileInfo, whetherGetGuid);
                    if (!responseGetGuid.IsSuccess)
                    {
                        return responseGetGuid;
                    }

                    string guidValue;
                    if (!uploadParams.ContainsKey("guid"))
                    {
                        guidValue = uploadState.LastResponse.Guid;
                        uploadParams.Add("guid", guidValue);
                    }
                    else
                    {
                        guidValue = uploadParams["guid"];
                    }

                    fileUploadResponse.TryWriteGuidAndLocalPathMarkersIfNotTheSame(fullPath, guidValue);

                    if (fileInfo.Length <= FILE_UPLOAD_CHUNK_SIZE)
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

                var responseChunkUpload = await ServerUploadResponse(multipartFormData, token, uploadState, currentLocalMd5,
                    currentPlainBytes, fileInfo, whetherGetGuid: false);

                if (uploadState.IsLastChunk)
                {
                    return responseChunkUpload;
                }

                var responseIfChanged = fileUploadResponse.ResponseIfChangedWhileUploadFile(fullPath, File.GetLastWriteTimeUtc(fullPath));
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
                Message = $"Technical error. For big file method '{nameof(fileUploadResponse.IterateFileChunks)}' was missed somehow..."
            };
        }

        private MultipartFormDataContent MultipartFormData(List<String> calculatedMd5S, String md5OfChunk, Byte[] bytes,
            ChunkUploadState uploadState, Dictionary<String, String> uploadParams, FileInfo fileInfo)
        {
            var fileUploadResponse = new FileUploadResponse();
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

            var md5OfFullFile = fileUploadResponse.CalculateMd5Hash(fileInfo.FullName);
            multiPartContent.Headers.ContentMD5 = Encoding.ASCII.GetBytes(md5OfFullFile);

            var endByte = AddContentRange(multiPartContent, uploadState, fileInfo);    //add ContentRange

            if (uploadState.IsLastChunk)
            {
                AddETags(multiPartContent, calculatedMd5S);

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

        private async Task<FileUploadResponse> ServerUploadResponse(MultipartFormDataContent multipartContent, string token, ChunkUploadState uploadState,
    string currentLocalMd5, Byte[] currentBytes, FileInfo fileInfo, Boolean whetherGetGuid)
        {
            HttpResponseMessage httpResponse;

            using (var httpClient = new RepeatableHttpClient(token))
            {
                httpClient.BaseAddress = new Uri(uploadState.ChunkRequestUri);

                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                try
                {
                    var repeatableHttpClient = new RepeatableHttpClient();
                    httpResponse = httpClient.PostAsync(uploadState.ChunkRequestUri, multipartContent).Result;

                    HttpStatusCode statusCode = httpResponse.StatusCode;
                    HttpContent responseContent = httpResponse.Content;

                    _ = await repeatableHttpClient.ServerMessage(responseContent);

                    //add method HandleGoodUploadRequest
                    if (httpResponse.IsSuccessStatusCode)
                    {
                        try
                        {
                            var lastResponse = await responseContent.ReadAsStringAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString(), ex.Message);

                            return new FileUploadResponse
                            {
                                IsSuccess = false,
                                Message = "Can't read content from the response."
                            };
                            //throw new ArgumentException($"Can't read content from the response: {ex.Message}");
                        }

                        if (currentLocalMd5 != uploadState.LastResponse.Md5 && false) // TODO Release 2.0 Server Temp do not use the logic.
                        {
                            var message = "Calculated Md5 and Md5 from server are different";
                            Console.WriteLine(message);

                            return new FileUploadResponse
                            {
                                IsSuccess = false,
                                Message = message
                            };
                            //var message = "Calculated Md5 and Md5 from server are different";
                            //throw new ArgumentException(message);
                        }

                        if (!whetherGetGuid || (uploadState.IsFirstChunk && uploadState.IsLastChunk))
                        {
                            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fileInfo.FullName);
                            var timeStamp = (lastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                            FileUploadResponse.TryWriteLastSeenModifiedUtc(fileInfo.FullName, (long)timeStamp);

                            //var finishUploadTime = DateTime.UtcNow;

                            var message = $"File {fileInfo.FullName} was uploaded";// by {(finishUploadTime - startTime).TotalMinutes} minutes.";
                            //Console.WriteLine(message);
                            //Console.WriteLine();

                            var lastResponse = uploadState.LastResponse as FileUploadResponse;
                            return new FileUploadResponse
                            {
                                IsSuccess = true,
                                Message = message,
                                OriginalName = lastResponse.OriginalName,
                                UploadTime = lastResponse.UploadTime,
                                Guid = uploadState.LastResponse.Guid
                            };
                        }
                    }
                    // else if (statusCode == HttpStatusCode.Forbidden)
                    // {
                    //     return await HandleResponse.HandleResponse403<FileUploadResponse>(httpResponse, $"Status code = '{statusCode}'.", CurrentUserProvider);
                    // }
                    // else
                    // {
                    //     filePrefix = await ObjectNameProvider.ServerPrefix(fileInfo.FullName);

                    //     var notSuccess = await HandleResponse.BuildNotSuccessResult(httpResponse, fileInfo, filePrefix, LoggingService);
                    //     return notSuccess;
                    // }

                    if (whetherGetGuid)
                    {
                        Console.WriteLine($"Response {statusCode} is recieved for first request");
                    }
                    else
                    {
                        uploadState.IsFirstChunk = false;
                        uploadState.IncreasePartNumber();

                        Console.WriteLine($"Response {statusCode} is recieved for part number = {uploadState.PartNumber}");
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);

                    return new FileUploadResponse
                    {
                        IsSuccess = false,
                        Message = ex.Message
                    };
                }
            }

            return new FileUploadResponse
            {
                IsSuccess = true,
                Guid = uploadState.LastResponse.Guid
            };
        }

        internal long AddContentRange(MultipartFormDataContent content, ChunkUploadState uploadState, FileInfo fileInfo)
        {
            long endByte;
            if ((((uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE) - 1) > fileInfo.Length)
            {
                uploadState.IsLastChunk = true;
                endByte = fileInfo.Length - 1;

                //content.Headers.TryAddWithoutValidation("content-range", "bytes " + uploadState.PartNumber * (long)UploadConstants.SingleChunkMaxSize + "-" + endByte + @"/" + fileInfo.Length);
            }
            else
            {
                endByte = ((uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE) - 1;
                //content.Headers.TryAddWithoutValidation("content-range", "bytes " + uploadState.PartNumber * (long)UploadConstants.SingleChunkMaxSize + "-" + endByte + @"/" + fileInfo.Length);
            }
            content.Headers.Add("content-range", $"bytes {uploadState.PartNumber * FILE_UPLOAD_CHUNK_SIZE}-{endByte}{@"/"}{fileInfo.Length}");

            return endByte;
        }

        internal void AddETags(MultipartFormDataContent content, List<string> md5S)
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

    }
}
