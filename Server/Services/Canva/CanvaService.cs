using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Server.Services.Canva
{
    public class CanvaConfig
    {
        public string AccessToken { get; set; } = default!;
        public string? BrandTemplateId { get; set; }
    }

    public class CanvaService
    {
        private readonly HttpClient _http;
        private const string BaseUrl = "https://api.canva.com/rest/v1";

        public CanvaService(HttpClient http)
        {
            _http = http;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url, string accessToken)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }

        /// <summary>
        /// Creates a new design in Canva.
        /// </summary>
        public async Task<CanvaDesignResult> CreateDesignAsync(
            CanvaConfig config, string title, string designTypeName = "doc", string? assetId = null)
        {
            var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/designs", config.AccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    title,
                    design_type = new { type = "preset", name = designTypeName },
                    asset_id = assetId
                }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaDesignResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaDesignResult();

            if (root.TryGetProperty("design", out var design))
            {
                result.DesignId = design.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Title = design.TryGetProperty("title", out var t) ? t.GetString() : null;

                if (design.TryGetProperty("urls", out var urls))
                {
                    result.EditUrl = urls.TryGetProperty("edit_url", out var eu) ? eu.GetString() : null;
                    result.ViewUrl = urls.TryGetProperty("view_url", out var vu) ? vu.GetString() : null;
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a design autofill job from a brand template.
        /// </summary>
        public async Task<CanvaAutofillResult> CreateAutofillJobAsync(
            CanvaConfig config, string brandTemplateId, string title,
            Dictionary<string, CanvaAutofillField> data)
        {
            var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/autofills", config.AccessToken);

            var body = new Dictionary<string, object>
            {
                ["brand_template_id"] = brandTemplateId,
                ["title"] = title,
                ["data"] = data
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaAutofillResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaAutofillResult();

            if (root.TryGetProperty("job", out var job))
            {
                result.JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Status = job.TryGetProperty("status", out var s) ? s.GetString() : null;
            }

            return result;
        }

        /// <summary>
        /// Gets the status of an autofill job.
        /// </summary>
        public async Task<CanvaAutofillResult> GetAutofillJobAsync(CanvaConfig config, string jobId)
        {
            var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/autofills/{jobId}", config.AccessToken);
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaAutofillResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaAutofillResult();

            if (root.TryGetProperty("job", out var job))
            {
                result.JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Status = job.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (job.TryGetProperty("result", out var res) &&
                    res.TryGetProperty("design", out var design))
                {
                    result.DesignId = design.TryGetProperty("id", out var did) ? did.GetString() : null;

                    if (design.TryGetProperty("urls", out var urls))
                    {
                        result.EditUrl = urls.TryGetProperty("edit_url", out var eu) ? eu.GetString() : null;
                        result.ViewUrl = urls.TryGetProperty("view_url", out var vu) ? vu.GetString() : null;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Polls an autofill job until completion or timeout.
        /// </summary>
        public async Task<CanvaAutofillResult> WaitForAutofillAsync(
            CanvaConfig config, string jobId, int maxAttempts = 30, int pollIntervalMs = 2000)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs);

                var result = await GetAutofillJobAsync(config, jobId);
                if (result.Error is not null) return result;
                if (result.Status == "success" || result.Status == "failed") return result;
            }

            return new CanvaAutofillResult
            {
                Error = $"Timeout esperando autofill de Canva (>{maxAttempts * pollIntervalMs / 1000}s)"
            };
        }

        /// <summary>
        /// Creates a design export job.
        /// </summary>
        public async Task<CanvaExportResult> CreateExportJobAsync(
            CanvaConfig config, string designId, string format = "png")
        {
            var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/exports", config.AccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    design_id = designId,
                    format = new { type = format }
                }),
                Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaExportResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaExportResult();

            if (root.TryGetProperty("job", out var job))
            {
                result.JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Status = job.TryGetProperty("status", out var s) ? s.GetString() : null;
            }

            return result;
        }

        /// <summary>
        /// Gets the status of an export job.
        /// </summary>
        public async Task<CanvaExportResult> GetExportJobAsync(CanvaConfig config, string jobId)
        {
            var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/exports/{jobId}", config.AccessToken);
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaExportResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaExportResult();

            if (root.TryGetProperty("job", out var job))
            {
                result.JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Status = job.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (job.TryGetProperty("urls", out var urls))
                {
                    var downloadUrls = new List<string>();
                    foreach (var url in urls.EnumerateArray())
                    {
                        var u = url.GetString();
                        if (u is not null) downloadUrls.Add(u);
                    }
                    result.DownloadUrls = downloadUrls;
                }
            }

            return result;
        }

        /// <summary>
        /// Polls an export job until completion or timeout, then downloads the exported files.
        /// </summary>
        public async Task<CanvaExportResult> WaitForExportAsync(
            CanvaConfig config, string jobId, int maxAttempts = 40, int pollIntervalMs = 2000)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs);

                var result = await GetExportJobAsync(config, jobId);
                if (result.Error is not null) return result;
                if (result.Status == "success" || result.Status == "failed") return result;
            }

            return new CanvaExportResult
            {
                Error = $"Timeout esperando exportacion de Canva (>{maxAttempts * pollIntervalMs / 1000}s)"
            };
        }

        /// <summary>
        /// Full workflow: create design from brand template (autofill), then export it.
        /// </summary>
        public async Task<CanvaPublishResult> AutofillAndExportAsync(
            CanvaConfig config, string brandTemplateId, string title,
            Dictionary<string, CanvaAutofillField> data, string exportFormat = "png")
        {
            // Step 1: Create autofill job
            var autofillJob = await CreateAutofillJobAsync(config, brandTemplateId, title, data);
            if (autofillJob.Error is not null)
                return new CanvaPublishResult { Error = autofillJob.Error };

            if (autofillJob.JobId is null)
                return new CanvaPublishResult { Error = "Canva no devolvio un ID de trabajo para autofill" };

            // Step 2: Wait for autofill completion
            var autofillResult = await WaitForAutofillAsync(config, autofillJob.JobId);
            if (autofillResult.Error is not null)
                return new CanvaPublishResult { Error = autofillResult.Error };

            if (autofillResult.Status == "failed")
                return new CanvaPublishResult { Error = "El trabajo de autofill de Canva fallo" };

            if (autofillResult.DesignId is null)
                return new CanvaPublishResult { Error = "Canva no devolvio un design ID del autofill" };

            // Step 3: Export the design
            var exportJob = await CreateExportJobAsync(config, autofillResult.DesignId, exportFormat);
            if (exportJob.Error is not null)
                return new CanvaPublishResult { Error = exportJob.Error };

            if (exportJob.JobId is null)
                return new CanvaPublishResult { Error = "Canva no devolvio un ID de trabajo para exportacion" };

            // Step 4: Wait for export completion
            var exportResult = await WaitForExportAsync(config, exportJob.JobId);
            if (exportResult.Error is not null)
                return new CanvaPublishResult { Error = exportResult.Error };

            if (exportResult.Status == "failed")
                return new CanvaPublishResult { Error = "La exportacion de Canva fallo" };

            // Step 5: Download exported files
            var downloadedFiles = new List<CanvaDownloadedFile>();
            if (exportResult.DownloadUrls is not null)
            {
                using var dlClient = new HttpClient();
                var pageNum = 1;
                foreach (var url in exportResult.DownloadUrls)
                {
                    var dlResp = await dlClient.GetAsync(url);
                    if (dlResp.IsSuccessStatusCode)
                    {
                        var bytes = await dlResp.Content.ReadAsByteArrayAsync();
                        var contentType = dlResp.Content.Headers.ContentType?.MediaType ?? $"image/{exportFormat}";
                        downloadedFiles.Add(new CanvaDownloadedFile
                        {
                            Data = bytes,
                            ContentType = contentType,
                            FileName = $"canva_page_{pageNum}.{exportFormat}"
                        });
                    }
                    pageNum++;
                }
            }

            return new CanvaPublishResult
            {
                DesignId = autofillResult.DesignId,
                EditUrl = autofillResult.EditUrl,
                ViewUrl = autofillResult.ViewUrl,
                DownloadedFiles = downloadedFiles,
                ExportFormat = exportFormat
            };
        }

        /// <summary>
        /// Simple workflow: create a new design and export it.
        /// </summary>
        public async Task<CanvaPublishResult> CreateAndExportAsync(
            CanvaConfig config, string title, string designTypeName = "doc", string exportFormat = "png")
        {
            // Step 1: Create design
            var designResult = await CreateDesignAsync(config, title, designTypeName);
            if (designResult.Error is not null)
                return new CanvaPublishResult { Error = designResult.Error };

            if (designResult.DesignId is null)
                return new CanvaPublishResult { Error = "Canva no devolvio un design ID" };

            // Step 2: Export the design
            var exportJob = await CreateExportJobAsync(config, designResult.DesignId, exportFormat);
            if (exportJob.Error is not null)
                return new CanvaPublishResult { Error = exportJob.Error };

            if (exportJob.JobId is null)
                return new CanvaPublishResult { Error = "Canva no devolvio un ID de trabajo para exportacion" };

            // Step 3: Wait for export completion
            var exportResult = await WaitForExportAsync(config, exportJob.JobId);
            if (exportResult.Error is not null)
                return new CanvaPublishResult { Error = exportResult.Error };

            if (exportResult.Status == "failed")
                return new CanvaPublishResult { Error = "La exportacion de Canva fallo" };

            // Step 4: Download exported files
            var downloadedFiles = new List<CanvaDownloadedFile>();
            if (exportResult.DownloadUrls is not null)
            {
                using var dlClient = new HttpClient();
                var pageNum = 1;
                foreach (var url in exportResult.DownloadUrls)
                {
                    var dlResp = await dlClient.GetAsync(url);
                    if (dlResp.IsSuccessStatusCode)
                    {
                        var bytes = await dlResp.Content.ReadAsByteArrayAsync();
                        var contentType = dlResp.Content.Headers.ContentType?.MediaType ?? $"image/{exportFormat}";
                        downloadedFiles.Add(new CanvaDownloadedFile
                        {
                            Data = bytes,
                            ContentType = contentType,
                            FileName = $"canva_page_{pageNum}.{exportFormat}"
                        });
                    }
                    pageNum++;
                }
            }

            return new CanvaPublishResult
            {
                DesignId = designResult.DesignId,
                EditUrl = designResult.EditUrl,
                ViewUrl = designResult.ViewUrl,
                DownloadedFiles = downloadedFiles,
                ExportFormat = exportFormat
            };
        }

        /// <summary>
        /// Uploads an asset to Canva (e.g., an image for autofill).
        /// </summary>
        public async Task<CanvaAssetUploadResult> UploadAssetAsync(
            CanvaConfig config, byte[] fileData, string assetName)
        {
            var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/asset-uploads", config.AccessToken);

            var nameBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(assetName));
            request.Headers.Add("Asset-Upload-Metadata", nameBase64);
            request.Content = new ByteArrayContent(fileData);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaAssetUploadResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CanvaAssetUploadResult();

            if (root.TryGetProperty("job", out var job))
            {
                result.JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null;
                result.Status = job.TryGetProperty("status", out var s) ? s.GetString() : null;
            }

            return result;
        }

        /// <summary>
        /// Polls an asset upload job until completion.
        /// </summary>
        public async Task<CanvaAssetUploadResult> WaitForAssetUploadAsync(
            CanvaConfig config, string jobId, int maxAttempts = 20, int pollIntervalMs = 2000)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs);

                var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/asset-uploads/{jobId}", config.AccessToken);
                var response = await _http.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("job", out var job))
                {
                    var status = job.TryGetProperty("status", out var s) ? s.GetString() : null;

                    if (status == "success")
                    {
                        var result = new CanvaAssetUploadResult
                        {
                            JobId = job.TryGetProperty("id", out var id) ? id.GetString() : null,
                            Status = "success"
                        };

                        if (job.TryGetProperty("asset", out var asset))
                            result.AssetId = asset.TryGetProperty("id", out var aid) ? aid.GetString() : null;

                        return result;
                    }

                    if (status == "failed")
                        return new CanvaAssetUploadResult
                        {
                            Status = "failed",
                            Error = "La subida de asset a Canva fallo"
                        };
                }
            }

            return new CanvaAssetUploadResult
            {
                Error = $"Timeout esperando subida de asset a Canva (>{maxAttempts * pollIntervalMs / 1000}s)"
            };
        }

        /// <summary>
        /// Gets the dataset (available data fields) for a brand template.
        /// </summary>
        public async Task<CanvaDatasetResult> GetBrandTemplateDatasetAsync(
            CanvaConfig config, string brandTemplateId)
        {
            var request = CreateRequest(HttpMethod.Get,
                $"{BaseUrl}/brand-templates/{brandTemplateId}/dataset", config.AccessToken);
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaDatasetResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            return new CanvaDatasetResult { RawJson = json };
        }

        /// <summary>
        /// Lists user designs.
        /// </summary>
        public async Task<CanvaListDesignsResult> ListDesignsAsync(CanvaConfig config)
        {
            var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/designs", config.AccessToken);
            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new CanvaListDesignsResult { Error = $"Canva API error ({(int)response.StatusCode}): {json}" };

            return new CanvaListDesignsResult { RawJson = json };
        }
    }

    // ── DTOs ──

    public class CanvaAutofillField
    {
        public string type { get; set; } = "text";
        public string? text { get; set; }
        public string? asset_id { get; set; }
    }

    public class CanvaDesignResult
    {
        public string? DesignId { get; set; }
        public string? Title { get; set; }
        public string? EditUrl { get; set; }
        public string? ViewUrl { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null && DesignId is not null;
    }

    public class CanvaAutofillResult
    {
        public string? JobId { get; set; }
        public string? Status { get; set; }
        public string? DesignId { get; set; }
        public string? EditUrl { get; set; }
        public string? ViewUrl { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null && Status == "success";
    }

    public class CanvaExportResult
    {
        public string? JobId { get; set; }
        public string? Status { get; set; }
        public List<string>? DownloadUrls { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null && Status == "success";
    }

    public class CanvaPublishResult
    {
        public string? DesignId { get; set; }
        public string? EditUrl { get; set; }
        public string? ViewUrl { get; set; }
        public List<CanvaDownloadedFile> DownloadedFiles { get; set; } = new();
        public string ExportFormat { get; set; } = "png";
        public string? Error { get; set; }
        public bool IsSuccess => Error is null && DesignId is not null;
    }

    public class CanvaDownloadedFile
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "";
        public string FileName { get; set; } = "";
    }

    public class CanvaAssetUploadResult
    {
        public string? JobId { get; set; }
        public string? Status { get; set; }
        public string? AssetId { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null && Status == "success";
    }

    public class CanvaDatasetResult
    {
        public string? RawJson { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null;
    }

    public class CanvaListDesignsResult
    {
        public string? RawJson { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => Error is null;
    }
}
