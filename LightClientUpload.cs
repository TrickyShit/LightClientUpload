using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using LUC.DVVSet;
using Newtonsoft.Json;

namespace LightClient
{
    public class LightClient
    {
        public const Int32 FILE_UPLOAD_CHUNK_SIZE = 2000000;    //Max size of fileparts
        private string version;

        public LightClient()
        {
        }

        //Host - server Url. Example - https://lightupon.cloud
        //Token - authorization token from server. Example - "647c7fde-936c-447a-8640-55dc8c1c69cb"
        //User_id - identificator from server. Example - "03a3a647d7e65013f515b16b1d9225b6"
        //bucket_id - bucket from server. Example - "the-integrationtests-integration1-res"
        //fullPath - full path to the file
        //filePrefix - prefix from server, need if file located in the subdirectory, else ""
        //guid - file identificator from server

        public async Task<HttpResponseMessage> Upload(string host, string token, string user_id, string bucket_id, string fullPath, string filePrefix, string guid = "")
        {
            var requestUri = Combine(host, "riak", "upload", bucket_id);
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            var timeStamp = ((int)(lastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();
            version = IncrementVersion(user_id, timeStamp);
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

        internal long AddContentRange(MultipartFormDataContent content, ChunkUploadState uploadState, FileInfo fileInfo)
        {
            long endByte = ((uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE - 1) > fileInfo.Length
                ? fileInfo.Length - 1
                : ((uploadState.PartNumber + 1) * FILE_UPLOAD_CHUNK_SIZE) - 1;
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

        private async Task<HttpResponseMessage> ResponseOfIterativelyUploadFile(FileInfo fileInfo, string token, ChunkUploadState uploadState,
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

                if (currentPlainBytes.Length < FILE_UPLOAD_CHUNK_SIZE)
                    uploadState.IsLastChunk = true;

                var percents = uploadState.PartNumber * FILE_UPLOAD_CHUNK_SIZE / (double)fileInfo.Length;
                Console.WriteLine($"Upload part[{uploadState.PartNumber}] for file {fileInfo.Name}. Uploaded {percents:P2}");

                MultipartFormDataContent multipartFormData;
                if (uploadState.IsFirstChunk && !uploadParams.ContainsKey("guid"))
                {
                    multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, currentPlainBytes,
                                                            uploadState, uploadParams, fileInfo);

                    HttpResponseMessage response = await ServerUploadResponse(multipartFormData, token, uploadState,
                                                                                     currentLocalMd5, fileInfo);
                    var str = await response.Content.ReadAsStringAsync();
                    var responseGetGuid = JsonConvert.DeserializeObject<FileUploadResponse>(str);

                    if (!responseGetGuid.IsSuccess)
                        return response;

                    if (!uploadParams.ContainsKey("guid"))
                    {
                        Console.WriteLine($"Response {responseGetGuid} is recieved for first request");
                        uploadParams.Add("guid", uploadState.LastResponse.Guid);
                    }

                    //fileUploadResponse.TryWriteGuidAndLocalPathMarkersIfNotTheSame(fullPath, uploadState.LastResponse.Guid);  // - this is logic for download, not upload

                    if (fileInfo.Length <= FILE_UPLOAD_CHUNK_SIZE)   //If file < 2000000 bytes then first request = upload to server
                        return response;
                }

                if (uploadState.LastResponse?.UploadId != null)
                    uploadState.ChunkRequestUri = $"{baseRequestUrl}{uploadState.LastResponse.UploadId}/{uploadState.PartNumber + 1}/";

                multipartFormData = MultipartFormData(calculatedMd5S, currentLocalMd5, currentPlainBytes, uploadState, uploadParams, fileInfo);

                var responseChunkUpload = await ServerUploadResponse(multipartFormData, token, uploadState, currentLocalMd5,
                     fileInfo);

                if (uploadState.IsFirstChunk)
                {
                    uploadState.IsFirstChunk = false;
                    Console.WriteLine($"Response {responseChunkUpload} is recieved for part number = {uploadState.PartNumber}");
                }

                if (uploadState.IsLastChunk)
                {
                    var response = JsonConvert.DeserializeObject<FileUploadResponse>(
            await responseChunkUpload.Content.ReadAsStringAsync());

                    return responseChunkUpload;
                }

                uploadState.IncreasePartNumber();

                var responseIfChanged = fileUploadResponse.ResponseIfChangedWhileUploadFile(fullPath, File.GetLastWriteTimeUtc(fullPath));
                if (!responseIfChanged.IsSuccess)
                {
                    return responseChunkUpload;
                }
            }
            return new HttpResponseMessage();     //not used anyway
        }

        private MultipartFormDataContent MultipartFormData(List<string> calculatedMd5S, string md5OfChunk, Byte[] bytes,
            ChunkUploadState uploadState, Dictionary<string, string> uploadParams, FileInfo fileInfo)
        {
            var fileUploadResponse = new FileUploadResponse();
            var boundary = "-----" + DateTime.Now.Ticks.ToString("x");
            var multiPartContent = new MultipartFormDataContent(boundary);

            //In a normal HTTP response, the Content-Disposition header is an indication that the expected response content will be displayed in the browser as a web page or part of a web page, or as an attachment that can then be downloaded and saved locally.
            //multiPartContent.Headers.TryAddWithoutValidation("Content-Type", "multipart/mixed; boundary=" + boundary);
            multiPartContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "files[]",
                FileName = fileInfo.Name,
                Size = fileInfo.Length
            };

            foreach (var param in uploadParams)
            {
                multiPartContent.Add(new StringContent(param.Value), param.Key);
            }

            var bytemd5 = new ByteArrayContent(bytes);
            bytemd5.Headers.ContentMD5 = Convert.FromBase64String(md5OfChunk);
            multiPartContent.Add(bytemd5, "files[]", fileInfo.Name);

            //var md5OfFullFile = fileUploadResponse.CalculateMd5Hash(fileInfo.FullName);
            //multiPartContent.Headers.ContentMD5 = Convert.FromBase64String(md5OfFullFile);
            multiPartContent.Add(new StringContent(md5OfChunk), "md5");

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

        private async Task<HttpResponseMessage> ServerUploadResponse(MultipartFormDataContent multipartContent, string token, ChunkUploadState uploadState,
    string currentLocalMd5, FileInfo fileInfo)
        {
            HttpResponseMessage httpResponse = new HttpResponseMessage();

            ServicePointManager.Expect100Continue = true;   //try to get response 100 from server
            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(uploadState.ChunkRequestUri);
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);

                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                try
                {
                    httpResponse = httpClient.PostAsync(uploadState.ChunkRequestUri, multipartContent).Result;

                    HttpStatusCode statusCode = httpResponse.StatusCode;
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