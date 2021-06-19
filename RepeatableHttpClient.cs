namespace LUC.Services.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class RepeatableHttpClient : HttpClient
    {
        #region Constants

        private const int MaxRetries = 100; 
        private const int SecondsDelay429 = 3; // TODO 1.0 Add logic return to queue if it processed to long.
        private const int SecondsDelay500 = 60;
        private const int SecondsDelay502 = 60;
        private const int SecondsDelayOther = 3;

        private const int SecondsBetweenAttempts = 10;

        #endregion

        #region Properties

        public List<HttpStatusCode> RepeatRequestStatusCodes { get; set; }

        #endregion

        #region Constructors

        public RepeatableHttpClient() : base()
        {
            RepeatRequestStatusCodes = new List<HttpStatusCode>();
            Timeout = new TimeSpan(0, 3, 0);
        }

        public RepeatableHttpClient(string accessToken) : base()
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", accessToken);
            RepeatRequestStatusCodes = new List<HttpStatusCode>();
            Timeout = new TimeSpan(0, 3, 0);
        }

        public RepeatableHttpClient(string accessToken, bool disposeHandler) : base(new HttpClientHandler(), disposeHandler)
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", accessToken);
            RepeatRequestStatusCodes = new List<HttpStatusCode>();
            Timeout = new TimeSpan(0, 3, 0);
        }

        public RepeatableHttpClient(string accessToken, HttpStatusCode repeatRequestStatusCode) : base()
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", accessToken);
            RepeatRequestStatusCodes = new List<HttpStatusCode>
            {
                repeatRequestStatusCode
            };
            Timeout = new TimeSpan(0, 20, 0);
        }

        public RepeatableHttpClient(string accessToken, HttpStatusCode repeatRequestStatusCode, bool disposeHandler) : base(new HttpClientHandler(), disposeHandler)
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", accessToken);
            RepeatRequestStatusCodes = new List<HttpStatusCode>
            {
                repeatRequestStatusCode
            };
            Timeout = new TimeSpan(0, 20, 0);
        }

        #endregion

        #region Methods

        public async Task<HttpResponseMessage> SendRepeatableAsync(string requestUri, Func<HttpContent> contentReciever, HttpMethod method)
        {
            HttpResponseMessage response = null;

            for (var i = 0; i < MaxRetries; i++)
            {
                HttpRequestMessage request = null;

                try
                {
                    request = new HttpRequestMessage
                    {
                        RequestUri = new Uri(requestUri),
                        Method = method
                    };

                    if (contentReciever != null)
                    {
                        request.Content = contentReciever();
                    };

                    #if DEBUG
                    var watch = Stopwatch.StartNew();
                    #endif

                    response = await SendAsync(request);

                    #if DEBUG
                    watch.Stop();
                    var elapsedMs = watch.ElapsedMilliseconds;

                    if (elapsedMs > 10000)
                    {
                        Console.WriteLine($"Responce time for request {requestUri} is {elapsedMs / 1000} sec.");
                    }

                    #endif
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine($"ObjectDisposedException occured. Try send request again...");
                    await Task.Delay(1000);
                    continue;
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine($"TaskCanceledException occured. Try send request again...");
                    await Task.Delay(100);
                    continue;
                }
                catch (HttpRequestException)
                {
                    Console.WriteLine($"HttpRequestException occured. Try send request again...");                   
                    await WriteLineRequestContent(request);
                    await Task.Delay(60000);
                    continue;
                }
                catch (WebException)
                {
                    Console.WriteLine($"WebException occured. Try send request again...");
                    await Task.Delay(10000);
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString(), ex.Message);
                }

                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    Console.WriteLine($"Response 500. Try send request again... Request uri = '{requestUri}'");
                    var stringRequest = await WriteLineRequestContent(request);//Exception: requestMessage.Content is disposed
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response 500 from server. Error = '{error}', Request = '{stringRequest}'");

                    await Task.Delay(SecondsDelay500 * 1000);

                    continue;
                }
                else if (response.StatusCode == (HttpStatusCode)429)
                {
                    Console.WriteLine($"Response 429. Try send request again...");
                    await WriteLineRequestContent(request);
                    var error = await response.Content.ReadAsStringAsync();

                    await Task.Delay(SecondsDelay429 * 1000);

                    continue;
                }
                else if (response.StatusCode == HttpStatusCode.BadGateway)
                {
                    Console.WriteLine($"Response 502. Try send request again...");
                    await WriteLineRequestContent(request);
                    var error = await response.Content.ReadAsStringAsync();

                    await Task.Delay(SecondsDelay502 * 1000);

                    continue;
                }
                else if (RepeatRequestStatusCodes.Contains(response.StatusCode))
                {
                    Console.WriteLine($"Response: {response.StatusCode}. Try send request again...");
                    await WriteLineRequestContent(request);
                    await Task.Delay(SecondsDelayOther * 100);

                    #if DEBUG
                    var error = await response.Content.ReadAsStringAsync();

                    if (error.Contains("4"))
                    {

                    }
                    #endif

                    continue;
                }
                else
                {
                    // TODO Add loggging of Ellapsedtime from headers.
                    return response;
                }
            }

            var result = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Artificial error 500")
            };
            return result;
        }

        private async Task<string> WriteLineRequestContent(HttpRequestMessage requestMessage)
        {
            if (requestMessage != null && requestMessage.Content != null)
            {
                var requestContent = await requestMessage.Content.ReadAsStringAsync();
                Console.WriteLine("Request content = " + requestContent);
                return requestContent;
            }

            return null;
        }

        public static HttpContent CloneHttpContent(HttpContent content)
        {
            if (content == null) return null;

            var ms = new MemoryStream();

            try
            {
                content.CopyToAsync(ms).Wait();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            ms.Position = 0;

            var clone = new StreamContent(ms);
            foreach (var header in content.Headers)
            {
                clone.Headers.Add(header.Key, header.Value);
            }

            return clone;
        }

        public async Task<String> ServerMessage(HttpContent responseContent)
        {
            var attemptNumber = 0;
            var isOnToShow24HoursError = true;
            //Whether responseContent can be changed without one more Post?
            while (responseContent == null)
            {
                attemptNumber++;
                await Task.Delay(SecondsBetweenAttempts);

                if (attemptNumber > 24 * 60 * 2 && isOnToShow24HoursError)
                {
                    isOnToShow24HoursError = false;
                    Console.WriteLine("App is offline more than 24 hours.");
                }
            }

            var serverMessage = await responseContent.ReadAsStringAsync();
            Console.WriteLine(serverMessage); //need mostly for the PoC Client
            return serverMessage;
        }


        #endregion
    }
}
