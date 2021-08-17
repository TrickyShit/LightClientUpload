﻿using System;
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

namespace LightClient
{
    /// <summary>
    /// Main class
    /// </summary>
    public class LightClient
    {
        /// <summary>
        /// If this size is exceeded, the file will be split into parts
        /// </summary>
        public const int FILE_UPLOAD_CHUNK_SIZE = 2000000;
        private string version;
        /// <summary>
        /// Server Url
        /// </summary>
        public string Host;

        /// <summary>
        /// Provides authorization to server with login, password and Url of server
        /// </summary>
        /// <param name="login">Login for authorization to server</param>
        /// <param name="password">Password for authorization to server</param>
        /// <param name="host">Server Url</param>
        /// <returns>HttpResponseMessage from server</returns>
        public async Task<HttpResponseMessage> LoginAsync(string login, string password, string host)
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
        /// <param name = "user_id" > User identificator from server.</param>
        /// <param name = "bucket_id"> Bucket from server.</param>
        /// <param name = "fullPath"> Full path to the file</param>
        /// <param name = "filePrefix"> Prefix from server, need if file located in the subdirectory, can be empty</param>
        /// <param name = "lastseenversion"> Vector clock version from server (optional parameter)</param>
        /// <returns>HttpResponseMessage from server</returns>
        public async Task<HttpResponseMessage> Upload(string host, string token, string user_id, string bucket_id, string fullPath, string filePrefix, string lastseenversion = "")
        {
            Host = host;
            string requestUri;
            if (filePrefix.EndsWith("/")) filePrefix = filePrefix.Remove(filePrefix.Length - 1);
            if (filePrefix == "")
                requestUri = Combine(host, "riak", "upload", bucket_id);
            else
                requestUri = Combine(host, "riak", "upload", bucket_id, "?prefix=" + filePrefix);
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            var timeStamp = ((int)(lastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();
            version = IncrementVersion(user_id, timeStamp, lastseenversion);
            var fileInfo = new FileInfo(fullPath);

            var chunkUploadState = new ChunkUploadState
            {
                ChunkRequestUri = requestUri,
                Guid = "",
                IsLastChunk = fileInfo.Length < FILE_UPLOAD_CHUNK_SIZE,
                IsFirstChunk = true
            };

            var uploadParams = new Dictionary<string, string>
                {
                    {"user_id", user_id},
                    {"version", version},
                    {"prefix", filePrefix},
                };

            return await ResponseOfIterativelyUploadFile(fileInfo, token, chunkUploadState, uploadParams);
        }

        internal long AddContentRange(MultipartFormDataContent content, ChunkUploadState uploadState, FileInfo fileInfo)
        {
            var endByte = ((uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE - 1) > fileInfo.Length
                            ? fileInfo.Length - 1
                            : (uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE - 1;
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

        private string Combine(params string[] uri)
        {
            uri[0] = uri[0].TrimEnd('/');
            string result = "";
            result += uri[0] + "/";
            for (var i = 1; i < uri.Length; i++)
            {
                if (uri[i] == "") continue;
                uri[i] = uri[i].TrimStart('/');
                uri[i] = uri[i].TrimEnd('/');
                result += uri[i] + "/";
            }
            return result;
        }

        private string IncrementVersion(string userId, string timestamp, string oldversion = "")
        {
            var dvvset = new Dvvdotnet();
            Clock dot;
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

        private MultipartFormDataContent MultipartFormData(List<string> calculatedMd5S, string md5OfChunk,
            ChunkUploadState uploadState, Dictionary<string, string> uploadParams, FileInfo fileInfo, Byte[] bytes = null)
        {
            var fileUploadResponse = new FileUploadResponse();
            var boundary = "-----" + DateTime.Now.Ticks.ToString("x");
            var multiPartContent = new MultipartFormDataContent(boundary);

            //In a normal HTTP response, the Content-Disposition header is an indication that the expected response content will be displayed in the browser
            //as a web page or part of a web page, or as an attachment that can then be downloaded and saved locally.

            foreach (var param in uploadParams)
            {
                multiPartContent.Add(new StringContent(param.Value), param.Key);
            }

            ByteArrayContent bytemd5;

            if (bytes == null) bytes = new byte[0];
            multiPartContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "files[]",
                FileName = fileInfo.Name,
                Size = fileInfo.Length
            };
            bytemd5 = new ByteArrayContent(bytes);
            bytemd5.Headers.ContentMD5 = Convert.FromBase64String(md5OfChunk);
            multiPartContent.Add(bytemd5, "files[]", fileInfo.Name);
            multiPartContent.Add(new StringContent(md5OfChunk), "md5");
            AddContentRange(multiPartContent, uploadState, fileInfo);

            if (!uploadState.IsLastChunk || bytes.Length == 0)
                return multiPartContent;

            AddETags(multiPartContent, calculatedMd5S);

            var i = 0;
            Console.WriteLine("Last lap!");
            foreach (var calculatedItemMd5 in calculatedMd5S)
            {
                Console.WriteLine($"Chunk: {i} - md5 {calculatedItemMd5}");
                i++;
            }
            return multiPartContent;
        }

        private async Task<HttpResponseMessage> ResponseOfIterativelyUploadFile(FileInfo fileInfo, string token, ChunkUploadState uploadState,
                                                                                       Dictionary<string, string> uploadParams)
        {
            var fileUploadResponse = new FileUploadResponse();
            string fullPath = fileInfo.FullName;
            var calculatedMd5S = new List<string>();
            string baseRequestUrl = uploadState.ChunkRequestUri;
            MultipartFormDataContent multipartFormData;
            HttpResponseMessage response;

            try
            {
                foreach (var currentPlainBytes in FileUploadResponse.IterateFileChunksWithoutFileBlocking(fullPath, offset: 0))
                {
                    var currentLocalMd5 = fileUploadResponse.CalculateMd5Hash(currentPlainBytes);
                    calculatedMd5S.Add(currentLocalMd5);

                    if (currentPlainBytes.Length < FILE_UPLOAD_CHUNK_SIZE)
                        uploadState.IsLastChunk = true;

                    var percents = uploadState.PartNumber * FILE_UPLOAD_CHUNK_SIZE / (double)fileInfo.Length;
                    Console.WriteLine($"Upload part[{uploadState.PartNumber}] for file {fileInfo.Name}. Uploaded {percents:P2}");

                    if (uploadParams.ContainsKey("guid"))
                    {
                        if (uploadState.LastResponse?.UploadId != null)
                            uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";

                        multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, uploadState,
                                                                 uploadParams, fileInfo);

                        response = await ServerUploadResponse(multipartFormData, token, uploadState,
                                                                                        currentLocalMd5, fileInfo);

                        var str = await response.Content.ReadAsStringAsync();
                        var responseGetGuid = JsonConvert.DeserializeObject<FileUploadResponse>(str);

                        if (!responseGetGuid.IsSuccess)
                            return response;

                        if (response.StatusCode is (HttpStatusCode)206)
                        {
                            if (uploadState.IsLastChunk)
                                return response;
                            else
                                uploadState.IncreasePartNumber();

                            continue;
                        }


                        FileUploadResponse.TryWriteGuidAndLocalPathMarkersIfNotTheSame(fileInfo, uploadState.LastResponse.Guid);

                        //if (fileInfo.Length <= FILE_UPLOAD_CHUNK_SIZE)   //If file < 2000000 bytes then first request = upload to server
                        //    return response;

                    }

                    if (uploadState.LastResponse?.UploadId != null)
                        uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";

                    multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, uploadState,
                                                            uploadParams, fileInfo, currentPlainBytes);
                    //multipartFormData.Add(new StringContent(uploadState.PartNumber.ToString()), "part_number");

                    response = await ServerUploadResponse(multipartFormData, token, uploadState,
                       currentLocalMd5, fileInfo);
                    var st = await response.Content.ReadAsStringAsync();
                    var responseGet = JsonConvert.DeserializeObject<FileUploadResponse>(st);

                    Console.WriteLine($"Response {responseGet.Guid} is recieved for first request");

                    if (uploadState.IsFirstChunk)
                    {
                        uploadState.IsFirstChunk = false;
                        uploadParams.Add("guid", uploadState.LastResponse.Guid);
                    }

                    Console.WriteLine($"Response {responseGet.UploadId} is recieved for part number = {uploadState.PartNumber}");

                    if (uploadState.IsLastChunk)
                        return response;

                    uploadState.IncreasePartNumber();

                    var responseIfChanged = fileUploadResponse.ResponseIfChangedWhileUploadFile(fullPath, File.GetLastWriteTimeUtc(fullPath));
                    if (!responseIfChanged.IsSuccess)
                        return response;
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
                                                                     string token, ChunkUploadState uploadState,
                                                                     string currentLocalMd5, FileInfo fileInfo)
        {
            HttpResponseMessage httpResponse = new HttpResponseMessage();

            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(Host);
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);

                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                try
                {
                    httpResponse = httpClient.PostAsync(uploadState.ChunkRequestUri, multipartContent).Result;

                    HttpContent responseContent = httpResponse.Content;
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
                            Console.WriteLine(ex.ToString(), ex.Message);
                            return httpResponse;
                        }

                        if (currentLocalMd5 != uploadState.LastResponse.Md5 && false) // TODO Release 2.0 Server Temp do not use the logic.
                        {
                            var message = "Calculated Md5 and Md5 from server are different";
                            Console.WriteLine(message);

                            return httpResponse;
                        }

                        if (uploadState.IsLastChunk)
                        {
                            FileUploadResponse.TryWriteLastSeenVersion(fileInfo, version);

                            var message = $"File {fileInfo.FullName} was uploaded";
                            Console.WriteLine(message);
                            Console.WriteLine();

                            return httpResponse;
                        }
                    }
                    else
                    {
                        return httpResponse;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);

                    return httpResponse;
                }
            }
            return httpResponse;
        }
    }
}