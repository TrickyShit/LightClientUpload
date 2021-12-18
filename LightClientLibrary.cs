using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using LUC.DVVSet;

using Newtonsoft.Json;

using Serilog;

namespace LightClientLibrary
{
    /// <summary>
    /// Main class
    /// </summary>
    public class LightClient
    {
        /// <summary>
        /// If this size is exceeded, the file will be split into parts
        /// </summary>
        public const Int32 FileUploadChunkSize = 2000000;

        /// <summary>
        /// Server Url
        /// </summary>
        public String Host { get; set; }

        private String IncrementVersion(String userId, String timestamp, String oldversion = "")
        {
            Clock dot;
            if (String.IsNullOrEmpty(oldversion))
            {
                dot = Dvvdotnet.Update(new Clock(timestamp), userId);
            }
            else
            {
                var incomeClock = Clock.StringToClock(oldversion);

                if (incomeClock == null) //if oldversion is incorrect
                    return null;

                dot = Dvvdotnet.Update(incomeClock, userId);
            }
            var d = Clock.ClockToString(dot);
            var version = Convert.ToBase64String(Encoding.UTF8.GetBytes(d));

            return version;
        }

        /// <summary>
        /// Provides authorization to server with login, password and Url of server
        /// </summary>
        /// <param name="login">Login for authorization to server</param>
        /// <param name="password">Password for authorization to server</param>
        /// <param name="host">Server Url</param>
        /// <returns>HttpResponseMessage from server</returns>
        public async Task<HttpResponseMessage> LoginAsync(String login, String password, String host)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var stringContent = JsonConvert.SerializeObject(new LoginRequest
                    {
                        Login = login,
                        Password = password
                    });

                    var content = new StringContent(stringContent, Encoding.UTF8, "application/json");
                    var loginUri = Combine(host, "riak", "login");

                    var request = await client.PostAsync(loginUri, content);

                    return request;
                }
            }
            catch (HttpRequestException)
            {
                return new HttpResponseMessage { StatusCode = HttpStatusCode.BadRequest };
            }
            catch (WebException)
            {
                return new HttpResponseMessage { StatusCode = HttpStatusCode.BadRequest };
            }
            catch (SocketException)
            {
                return new HttpResponseMessage { StatusCode = HttpStatusCode.BadRequest };
            }
        }

        /// <summary>
        /// Uploads file to server.
        /// </summary>
        /// <param name = "host" > Server Url.</param>
        /// <param name = "token" > Authorization token from server.</param> 
        /// <param name = "userId" > User identificator from server.</param>
        /// <param name = "bucketId"> Bucket from server.</param>
        /// <param name = "fullPath"> Full path to the file</param>
        /// <param name = "filePrefix"> Prefix from server, need if file located in the subdirectory, can be empty</param>
        /// <param name = "lastseenversion"> Vector clock version from server (optional parameter)</param>
        /// <returns>HttpResponseMessage from server</returns>
        public async Task<HttpResponseMessage> Upload(String host, String token, String userId, String bucketId, String fullPath, String filePrefix, String lastseenversion = "")
        {
            Host = host;
            String requestUri;
            if (filePrefix.EndsWith("/"))
            {
                filePrefix = filePrefix.Remove(filePrefix.Length - 1);
            }

            requestUri = filePrefix == ""
                ? Combine(host, "riak", "upload", bucketId)
                : Combine(host, "riak", "upload", bucketId, "?prefix=" + filePrefix);
            var fileInfo = new FileInfo(fullPath);

            var chunkUploadState = new ChunkUploadState
            {
                ChunkRequestUri = requestUri,
                Guid = "",
                IsLastChunk = fileInfo.Length < FileUploadChunkSize,
                IsFirstChunk = true
            };

            DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            String timeStamp = ((Int32)(lastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();

            String version =
                lastseenversion == "" ?
                IncrementVersion(userId, timeStamp) :
                IncrementVersion(userId, timeStamp, lastseenversion);

            if (version == null)
            {
                version = IncrementVersion(userId, timeStamp);
            }

            var uploadParams = new Dictionary<String, String>
                {
                    {"user_id", userId},
                    {"version", version},
                    {"prefix", filePrefix},
                };

            return await ResponseOfIterativelyUploadFile(fileInfo, token, chunkUploadState, uploadParams);
        }

        internal Int64 AddContentRange(MultipartFormDataContent content, ChunkUploadState uploadState, FileInfo fileInfo)
        {
            var endByte = ((uploadState.PartNumber + 1) * FileUploadChunkSize - 1) > fileInfo.Length
                            ? fileInfo.Length - 1
                            : (uploadState.PartNumber + 1) * FileUploadChunkSize - 1;
            content.Headers.Add("content-range", $"bytes {uploadState.PartNumber * FileUploadChunkSize}-{endByte}{@"/"}{fileInfo.Length}");

            return endByte;
        }

        internal void AddETags(MultipartFormDataContent content, List<String> md5S)
        {
            var etags = new StringBuilder();

            if (md5S != null)
            {
                var part = 1;
                foreach (var md5 in md5S)
                {
                    etags.Append($"{part},{md5},");
                    part++;
                }

                etags = etags.Remove(etags.Length - 1, 1);
            }
            content.Add(new StringContent(etags.ToString()), "etags[]");
        }

        private String Combine(params String[] uri)
        {
            uri[0] = uri[0].TrimEnd('/');
            var result = new StringBuilder();
            result.Append(uri[0] + "/");
            for (var i = 1; i < uri.Length; i++)
            {
                if (uri[i] == null || uri[i] == "")
                {
                    continue;
                }

                uri[i] = uri[i].TrimStart('/').TrimEnd('/');
                result.Append(uri[i] + "/");
            }
            return result.ToString();
        }
        private MultipartFormDataContent MultipartFormData(List<String> calculatedMd5S, String md5OfChunk,
            ChunkUploadState uploadState, Dictionary<String, String> uploadParams, FileInfo fileInfo, Byte[] bytes = null)
        {
            var boundary = "-----" + DateTime.Now.Ticks.ToString("x");
            var multiPartContent = new MultipartFormDataContent(boundary);

            //In a normal HTTP response, the Content-Disposition header is an indication that the expected response content will be displayed in the browser
            //as a web page or part of a web page, or as an attachment that can then be downloaded and saved locally.

            foreach (var param in uploadParams)
            {
                multiPartContent.Add(new StringContent(param.Value), param.Key);
            }

            if (bytes == null)
            {
                bytes = new Byte[0];
            }

            multiPartContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "files[]",
                FileName = fileInfo.Name,
                Size = fileInfo.Length
            };

            var bytemd5 = new ByteArrayContent(bytes);
            bytemd5.Headers.ContentMD5 = Convert.FromBase64String(md5OfChunk);
            multiPartContent.Add(bytemd5, "files[]", fileInfo.Name);
            multiPartContent.Add(new StringContent(md5OfChunk), "md5");
            AddContentRange(multiPartContent, uploadState, fileInfo);

            if (!uploadState.IsLastChunk || bytes.Length == 0)
            {
                return multiPartContent;
            }

            AddETags(multiPartContent, calculatedMd5S);

            var i = 0;
            Log.Information("Last lap!");
            foreach (var calculatedItemMd5 in calculatedMd5S)
            {
                Log.Information($"Chunk: {i} - md5 {calculatedItemMd5}");
                i++;
            }
            return multiPartContent;
        }

        private async Task<HttpResponseMessage> ResponseOfIterativelyUploadFile(FileInfo fileInfo, String token, ChunkUploadState uploadState,
                                                                                       Dictionary<String, String> uploadParams)
        {
            var fileUploadResponse = new FileUploadResponse();
            var fullPath = fileInfo.FullName;
            var calculatedMd5S = new List<String>();
            var baseRequestUrl = uploadState.ChunkRequestUri;

            try
            {
                foreach (var currentPlainBytes in FileUploadResponse.IterateFileChunksWithoutFileBlocking(fullPath, offset: 0))
                {
                    var currentLocalMd5 = fileUploadResponse.CalculateMd5Hash(currentPlainBytes);
                    calculatedMd5S.Add(currentLocalMd5);

                    if (currentPlainBytes.Length < FileUploadChunkSize)
                    {
                        uploadState.IsLastChunk = true;
                    }

                    var percents = uploadState.PartNumber * FileUploadChunkSize / (Double)fileInfo.Length;
                    Log.Information($"Upload part[{uploadState.PartNumber}] for file {fileInfo.Name}. Uploaded {percents:P2}");

                    MultipartFormDataContent multipartFormData;
                    HttpResponseMessage response;

                    if (uploadParams.ContainsKey("guid"))
                    {
                        if (uploadState.LastResponse?.UploadId != null)
                        {
                            uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";
                        }

                        multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, uploadState,
                                                                 uploadParams, fileInfo);

                        response = await ServerUploadResponse(multipartFormData, token, uploadState,
                                                                                        currentLocalMd5, fileInfo);

                        var str = await response.Content.ReadAsStringAsync();

                        var responseGetGuid = JsonConvert.DeserializeObject<FileUploadResponse>(str);
                        if (response.IsSuccessStatusCode)
                        {
                            responseGetGuid.IsSuccess = true;
                        }

                        if (responseGetGuid != null && !responseGetGuid.IsSuccess)
                        {
                            return response;
                        }

                        if (response.StatusCode is (HttpStatusCode)206)
                        {
                            if (uploadState.IsLastChunk)
                            {
                                return response;
                            }

                            uploadState.IncreasePartNumber();

                            continue;
                        }

                        //if (uploadState.LastResponse != null)
                        //{
                        //    FileUploadResponse.TryWriteGuidAndLocalPathMarkersIfNotTheSame(fileInfo,
                        //        uploadState.LastResponse.Guid);
                        //}
                    }

                    if (uploadState.LastResponse?.UploadId != null)
                    {
                        uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";
                    }

                    multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, uploadState,
                                                            uploadParams, fileInfo, currentPlainBytes);
                    multipartFormData.Add(new StringContent((uploadState.PartNumber).ToString()), "part_number");

                    response = await ServerUploadResponse(multipartFormData, token, uploadState,
                       currentLocalMd5, fileInfo);
                    var st = await response.Content.ReadAsStringAsync();
                    var responseGet = JsonConvert.DeserializeObject<FileUploadResponse>(st);

                    if (responseGet != null)
                    {
                        Log.Information($"Response {responseGet.Guid} is recieved for first request");

                        if (uploadState.IsFirstChunk)
                        {
                            uploadState.IsFirstChunk = false;
                            if (uploadState.LastResponse != null)
                            {
                                uploadParams.Add("guid", uploadState.LastResponse.Guid);
                            }
                        }

                        Log.Information(
                            $"Response {responseGet.UploadId} is recieved for part number = {uploadState.PartNumber}");
                    }

                    if (uploadState.IsLastChunk)
                    {
                        return response;
                    }

                    uploadState.IncreasePartNumber();

                    var responseIfChanged = fileUploadResponse.ResponseIfChangedWhileUploadFile(fullPath, File.GetLastWriteTimeUtc(fullPath));
                    if (!responseIfChanged.IsSuccess)
                    {
                        return response;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                var errorResponse = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.NotFound
                };
                return errorResponse;
            }
            return new HttpResponseMessage();     //not used anyway
        }

        private async Task<HttpResponseMessage> ServerUploadResponse(MultipartFormDataContent multipartContent,
                                                                     String token, ChunkUploadState uploadState,
                                                                     String currentLocalMd5, FileInfo fileInfo)
        {
            var httpResponse = new HttpResponseMessage();

            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(Host);
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);

                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                try
                {
                    httpResponse = httpClient.PostAsync(uploadState.ChunkRequestUri, multipartContent).Result;

                    var responseContent = httpResponse.Content;
                    var json = await responseContent.ReadAsStringAsync();

                    //add method HandleGoodUploadRequest
                    if (httpResponse.IsSuccessStatusCode)
                    {
                        try
                        {
                            uploadState.LastResponse = JsonConvert.DeserializeObject<FileUploadResponse>(json);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex.ToString(), ex.Message);
                            return httpResponse;
                        }

                        if (uploadState.LastResponse != null && currentLocalMd5 != uploadState.LastResponse.Md5)
                        {
                            throw new ArgumentException("Calculated Md5 and Md5 from server are different");
                        }

                        if (uploadState.IsLastChunk)
                        {

                            var message = $"File {fileInfo.FullName} was uploaded";
                            Log.Information(message);

                            return httpResponse;
                        }
                    }
                    else
                    {
                        Log.Error(json);
                        return httpResponse;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message);

                    return httpResponse;
                }
            }
            return httpResponse;
        }
    }
}