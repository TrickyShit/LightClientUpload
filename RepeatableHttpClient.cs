namespace LUC.Services.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class RepeatableHttpClient : HttpClient
    {
        #region Constants

        private const int SecondsBetweenAttempts = 10;

        #endregion Constants

        #region Properties

        public List<HttpStatusCode> RepeatRequestStatusCodes { get; set; }

        #endregion Properties

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

        #endregion Constructors

        #region Methods

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

        #endregion Methods
    }
}