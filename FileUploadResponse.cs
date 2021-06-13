namespace LightClientUpload
{
    public class FileUploadResponse : BaseResponse
    {
        public FileUploadResponse() : base()
        {
        }

        public FileUploadResponse(bool isSuccess, bool isForbidden, string message) : base(isSuccess, isForbidden, message)
        {
        }

        public string OriginalName { get; set; }

        public long ModifiedUtc { get; set; }

        public string Guid { get; set; }

        public string ToString()
        {
            var responseStr = $"{nameof(FileUploadResponse)}:\n" +
                $"{nameof(OriginalName)} = {OriginalName};\n" +
                $"{nameof(ModifiedUtc)} = {ModifiedUtc};\n" +
                $"{nameof(Guid)} = {Guid}";

            return responseStr;
        }
    }

    public abstract class BaseResponse : INotificationResult
    {
        public BaseResponse()
        {
        }

        public BaseResponse(bool isSuccess, bool isForbidden, string message)
        {
            IsSuccess = isSuccess;
            IsForbidden = isForbidden;
            Message = message;
        }

        public bool IsSuccess { get; set; } = true;

        public string Message { get; set; }

        public bool IsForbidden { get; set; } = false;
    }

}
