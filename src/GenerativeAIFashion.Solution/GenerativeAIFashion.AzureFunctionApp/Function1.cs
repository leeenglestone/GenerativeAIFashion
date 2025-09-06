using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GenerativeAIFashion.AzureFunctionApp
{
    public class Function1
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;

        public Function1(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
            _httpClientFactory = httpClientFactory;
        }

        [Function("Function1")]
        public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
        {
            try
            {

                // 1) Parse multipart/form-data
                if (!req.Headers.TryGetValues("Content-Type", out var contentTypeValues))
                    return await BadRequest(req, "Missing Content-Type.");

                var contentType = contentTypeValues.FirstOrDefault() ?? "";
                if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
                    mediaType.MediaType != "multipart/form-data")
                    return await BadRequest(req, "Content-Type must be multipart/form-data.");

                var boundary = HeaderUtilities.RemoveQuotes(mediaType.Parameters.First(p => p.Name.Equals("boundary")).Value).Value;
                if (string.IsNullOrWhiteSpace(boundary))
                    return await BadRequest(req, "Missing multipart boundary.");

                var reader = new MultipartReader(boundary, req.Body);
                string? prompt = null;
                (byte[] bytes, string mime)? userImage = null;
                (byte[] bytes, string mime)? clothingImage = null;

                for (MultipartSection? section = await reader.ReadNextSectionAsync();
                     section != null;
                     section = await reader.ReadNextSectionAsync())
                {
                    if (!Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || cd == null || !cd.DispositionType.Equals("form-data"))
                        continue;

                    var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;

                    if (cd.FileName.HasValue || cd.FileNameStar.HasValue)
                    {
                        // File field
                        string fileName = HeaderUtilities.RemoveQuotes(cd.FileNameStar.HasValue ? cd.FileNameStar : cd.FileName).Value ?? "file";
                        var ms = new MemoryStream();
                        await section.Body.CopyToAsync(ms);
                        var fileBytes = ms.ToArray();

                        var mime = section.ContentType ?? "application/octet-stream";

                        if (string.Equals(name, "userImage", StringComparison.OrdinalIgnoreCase))
                            userImage = (fileBytes, mime);
                        else if (string.Equals(name, "clothingImage", StringComparison.OrdinalIgnoreCase))
                            clothingImage = (fileBytes, mime);
                    }
                    else
                    {
                        // Text field
                        using var sr = new StreamReader(section.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                        var value = await sr.ReadToEndAsync();
                        if (string.Equals(name, "prompt", StringComparison.OrdinalIgnoreCase))
                            prompt = value;
                    }
                }

                if (userImage is null || clothingImage is null)
                    return await BadRequest(req, "Both userImage and clothingImage are required.");

               
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "Unhandled error.");
                //return await Error(req, HttpStatusCode.InternalServerError, "Server error.");
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

            response.WriteString("Welcome to Azure Functions!");

            return response;
        }

        // ---- helpers ----
        private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await resp.WriteStringAsync(message);
            return resp;
        }

        private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string message)
        {
            var resp = req.CreateResponse(code);
            resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await resp.WriteStringAsync(message);
            return resp;
        }
    }
}
