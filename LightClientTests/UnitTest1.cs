using System.Data;
using System.IO;
using System.Threading.Tasks;
using LightClient;
using Newtonsoft.Json;
using NUnit.Framework;

namespace LightClientTests
{
    [TestFixture]
    public class Tests
    {
        private const string DiffUploadFile = @"E:\Downloads\integration1\diffuploadtest.txt";
        private readonly LightClient.LightClient lightClient = new LightClient.LightClient();
        public const long TicksPerSecond = 10;

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        [Order(1)]
        public void DiffUploadTest()
        {
            var host = "https://lightupon.cloud";
            var response = lightClient.LoginAsync("integration1", "integration1", host).Result;

            Assert.IsTrue(response.IsSuccessStatusCode);

            var str = response.Content.ReadAsStringAsync().Result;
            var responseValues = JsonConvert.DeserializeObject<LoginResponse>(str);

            if (File.Exists(DiffUploadFile)) File.Delete(DiffUploadFile);

            //Create a new file, 30MB
            var fs = new FileStream(DiffUploadFile, FileMode.CreateNew);
            fs.Seek(30L * 1024 * 1024, SeekOrigin.Begin);
            fs.WriteByte(0);
            fs.Close();

            //fill the file with random bytes (0-100)
            byte[] bytes = File.ReadAllBytes(DiffUploadFile);
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(System.DateTime.Now.Ticks % 100);
            File.WriteAllBytes(DiffUploadFile, bytes);

            var startUpload = System.DateTime.Now.Ticks / 1000000;  //set time precision to 0.1 seconds
            var uploadResponse = lightClient.Upload(host, responseValues.Token, responseValues.Id,
                responseValues.Groups[0].Bucket_Id, DiffUploadFile, "").Result;

            var durationUpload = System.DateTime.Now.Ticks / 1000000 - startUpload;

            Assert.IsTrue(uploadResponse.IsSuccessStatusCode);

            str = uploadResponse.Content.ReadAsStringAsync().Result;
            var uploadResponseValues = JsonConvert.DeserializeObject<FileUploadResponse>(str);

            //Change a first and last bytes of diff upload file
            bytes[0] = 111;
            bytes[bytes.Length - 1] = 111;
            File.WriteAllBytes(DiffUploadFile, bytes);

            var newUpload = System.DateTime.Now.Ticks / 1000000;
            var newUploadResponse = lightClient.Upload(host, responseValues.Token, responseValues.Id,
                responseValues.Groups[0].Bucket_Id, DiffUploadFile, "", uploadResponseValues.Version).Result;
            var newDurationUpload = System.DateTime.Now.Ticks / 1000000 - newUpload;

            Assert.IsTrue(newDurationUpload < durationUpload); //this upload should be a faster than first
            Assert.IsTrue(newUploadResponse.IsSuccessStatusCode);

        }
    }
}