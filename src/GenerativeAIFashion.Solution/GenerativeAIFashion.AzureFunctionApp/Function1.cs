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
                #region Get files and prompt from client site
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

                #endregion


                // Now send them to Gemini and return the result
                #region Gemini call

                var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY_1");                

                var model = Environment.GetEnvironmentVariable("GEMINI_IMAGE_MODEL")
                       ?? "gemini-2.5-flash-image-preview";

                var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

                var parts = new List<object>
            {
                new { text = (prompt ?? "").Trim() },
                new
                {
                    inline_data = new
                    {
                        mime_type = userImage.Value.mime,
                        data = Convert.ToBase64String(userImage.Value.bytes)
                    }
                },
                new
                {
                    inline_data = new
                    {
                        mime_type = clothingImage.Value.mime,
                        data = Convert.ToBase64String(clothingImage.Value.bytes)
                    }
                }
            };

                var requestBody = new
                {
                    contents = new[] { new { parts } },
                    generationConfig = new
                    {
                        temperature = 0.7
                    }
                };

                using var client = _httpClientFactory.CreateClient();
                using var http = new HttpRequestMessage(HttpMethod.Post, endpoint);
                http.Headers.Add("x-goog-api-key", apiKey);
                http.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                var genResp = await client.SendAsync(http);
                var raw = await genResp.Content.ReadAsStringAsync();

                if (!genResp.IsSuccessStatusCode)
                {
                    _logger.LogError("Gemini error {Status}: {Body}", genResp.StatusCode, raw);
                    return await Error(req, HttpStatusCode.BadGateway, raw);
                }

                // --- Robustly extract first inline image ---
                if (!TryExtractInlineImage(raw, out var outMime, out var outB64, out var reason))
                {
                    _logger.LogWarning("No inline image found. Reason: {Reason}. Raw: {Raw}",
                        reason ?? "unknown", Truncate(raw, 2000));
                    var err = new
                    {
                        error = "No image returned from Gemini.",
                        reason,
                        raw = Truncate(raw, 2000)
                    };
                    var notOk = req.CreateResponse(HttpStatusCode.BadGateway);
                    AddCors(notOk, req);
                    notOk.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await notOk.WriteStringAsync(JsonSerializer.Serialize(err));
                    return notOk;
                }

                var bytesOut = Convert.FromBase64String(outB64!);
                var ok = req.CreateResponse(HttpStatusCode.OK);
                AddCors(ok, req);
                ok.Headers.Add("Content-Type", outMime ?? "image/png");
                await ok.WriteBytesAsync(bytesOut);
                return ok;

                #endregion
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error.");
                var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
                AddCors(resp, req);
                resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                await resp.WriteStringAsync("Server error.");
                return resp;
            }
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

        private static bool TryExtractInlineImage(string json, out string? mime, out string? b64, out string? reason)
        {
            mime = null; b64 = null; reason = null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                reason = "No candidates array or empty.";
                // Optional: promptFeedback details
                if (root.TryGetProperty("promptFeedback", out var pf) &&
                    pf.TryGetProperty("blockReason", out var br))
                {
                    reason = $"Blocked: {br.GetString()}";
                }
                return false;
            }

            foreach (var cand in candidates.EnumerateArray())
            {
                // Some responses include finishReason / safetyRatings — grab a hint if present
                if (cand.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    reason ??= $"finishReason={fr.GetString()}";

                if (!cand.TryGetProperty("content", out var content))
                    continue;

                if (!content.TryGetProperty("parts", out var partsEl))
                    continue;

                foreach (var p in partsEl.EnumerateArray())
                {
                    // inline image may appear as inline_data or inlineData
                    if (p.TryGetProperty("inline_data", out var id) || p.TryGetProperty("inlineData", out id))
                    {
                        if (id.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
                        {
                            b64 = dataEl.GetString();
                            if (id.TryGetProperty("mime_type", out var mt) || id.TryGetProperty("mimeType", out mt))
                                mime = mt.GetString();

                            if (!string.IsNullOrEmpty(b64))
                                return true;
                        }
                    }
                }
            }

            reason ??= "No inline_data part found.";
            return false;
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";

      

        private static void AddCors(HttpResponseData resp, HttpRequestData req)
        {
            // In prod, validate against a whitelist. For local ease, echo the Origin.
            var origin = req.Headers.TryGetValues("Origin", out var vals) ? vals.FirstOrDefault() : null;
            if (!string.IsNullOrEmpty(origin))
                resp.Headers.Add("Access-Control-Allow-Origin", origin);
            else
                resp.Headers.Add("Access-Control-Allow-Origin", "*"); // adjust for production if needed

            resp.Headers.Add("Vary", "Origin");
            resp.Headers.Add("Access-Control-Allow-Credentials", "true");
        }
    }
}
