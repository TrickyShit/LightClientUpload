namespace LUC.Services.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;

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
    }
}