using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using MimeMapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace erl.AspNetCore.AgsToken
{
    public class Operation
    {
        private readonly string _rootUrl;
        private readonly string _proxyUrl;
        
        private AgsTokenResponse _tokenRes = new AgsTokenResponse();
        private string _user;
        private string _password;
        private string _tokenUrl;
        private string _referer;
        private string _domain;

        // HttpClient is intended to be instantiated once and re-used throughout the life of an application.
        // Instantiating an HttpClient class for every request will exhaust the number of sockets available under heavy loads. 
        // This will result in SocketException errors.
        // See: https://docs.microsoft.com/en-us/dotnet/api/system.net.http.httpclient?view=netstandard-2.0#remarks
        // Also important to note which methods on the client which are thread-safe, Operation will be used in async.
        private static HttpClient _httpClient;

        static Operation()
        {
            _httpClient = new HttpClient() { Timeout = TimeSpan.FromHours(1) };
        }

        public Operation(string rootUrl)
        {
            _rootUrl = rootUrl;
        }

        public Operation(string rootUrl, string proxyUrl) : this(rootUrl)
        {
            _proxyUrl = proxyUrl;
        }

        public string Version { get; set; }

        

        

        // Without a locking mechanism the authentication code is not thread-safe.
        // Run concurrently several requests will fail while auth is in progress
        private static readonly SemaphoreSlim AuthLock = new SemaphoreSlim(1, 1);

        public async Task<AgsTokenResponse> Authenticate(string user, string password, string domain = null, string referer = null)
        {
            _user = user;
            _password = password;
            _referer = referer;
            _domain = domain;

            var restInfo = await Get<Info>($"{_rootUrl}/rest/info", new Dictionary<string, object>());

            _tokenUrl = restInfo?.AuthInfo?.TokenServicesUrl;

            if (string.IsNullOrWhiteSpace(_tokenUrl))
                throw new Exception("Could not discover tokenUrl, cannot authenticate");

            return await ImplicitAuthenticate();
        }

        private async Task<AgsTokenResponse> ImplicitAuthenticate()
        {
            await AuthLock.WaitAsync();

            try
            {
                if (_tokenRes.IsValid())
                {
                    return _tokenRes;
                }

                _tokenRes = null;

                if (string.IsNullOrWhiteSpace(_domain))
                    _tokenRes = await InitToken(_tokenUrl, _user, _password, _referer);
                else
                    _tokenRes = await InitIWA(_tokenUrl, _user, _password, _domain);
            }
            finally
            {
                if (AuthLock.CurrentCount == 0)
                    AuthLock.Release();
            }

            return _tokenRes;
        }
        

        public async Task<AgsTokenResponse> InitToken(string tokenUrl, string username, string password, string referer = null)
        {
            var parameters = new Dictionary<string, string> { { "username", username }, { "password", password } };
            if (referer != null)
            {
                parameters.Add("client", "referer");
                parameters.Add("referer", referer);
            }
            else
            {
                parameters.Add("client", "requestip");
            }
            
            var token = await Post<AgsTokenResponse>(tokenUrl, parameters, _proxyUrl);
            return token;

        }

        public async Task<AgsTokenResponse> InitIWA(string tokenUrl, string user, string password, string domain)
        {
            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(user, password, domain)
            };

            _httpClient = new HttpClient(handler);

            var uri = new Uri(tokenUrl);

            var parameters = new Dictionary<string, object>
            {
                {"request", "getToken"},
                {"serverUrl", $"{_rootUrl}/rest/services"},
                {"referer", uri.Authority},
            };

            return await Get<AgsTokenResponse>(tokenUrl, parameters, _proxyUrl);
        }

        private string GetGPServerBaseUrl(string gpServerName) => $"{_rootUrl}/rest/services/{gpServerName}/GPServer";

        public async Task<GPServer> GetGPServer(string gpServerName)
        {
            return await GetRequest<GPServer>(GetGPServerBaseUrl(gpServerName), new Dictionary<string, object>());
        }

        public async Task<JobResult> ExecuteGPServer(string gpServerName, string taskName, Dictionary<string, string> parameters, CancellationToken ct = default)
        {
            var gpServer = await GetGPServer(gpServerName);

            if (gpServer == null)
                throw new Exception($"GPServer '{gpServerName}' was not found.");

            if (!gpServer.TaskNames.Contains(taskName))
                throw new Exception($"GPServer '{gpServerName} did not contain task '{taskName}'. Possible tasks are: [{string.Join(", ", gpServer.TaskNames)}]");

            switch (gpServer.ExecutionType)
            {
                case GPExecutionType.Synchronous:
                    return await ExecuteSyncGPServer(gpServerName, taskName, parameters);
                case GPExecutionType.Asynchronous:
                    return await ExecuteAsyncGPServer(gpServerName, taskName, parameters, ct);
                default:
                    throw new NotImplementedException($"Missing implementation for {gpServer.ExecutionType}");
            }
        }

        private async Task<JobResult> ExecuteSyncGPServer(string gpServerName, string taskName, Dictionary<string, string> parameters)
        {
            return await PostRequest<JobResult>($"{GetGPServerBaseUrl(gpServerName)}/{taskName}/execute", parameters);
        }

        private async Task<JobResult> ExecuteAsyncGPServer(string gpServerName, string taskName, Dictionary<string, string> parameters, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var startResult = await PostRequest<JobResult>($"{GetGPServerBaseUrl(gpServerName)}/{taskName}/submitJob", parameters);

            if (startResult?.JobStatus != JobStatus.Submitted)
                throw new JobResultException(startResult, $"Expected JobStatus to be {JobStatus.Submitted}, but was {startResult?.JobStatus}");

            try
            {
                return await AwaitSucceededGPServerJob(gpServerName, taskName, startResult.JobId, ct);
            }
            catch (OperationCanceledException)
            {
                await PostRequest<JobResult>($"{GetGPServerBaseUrl(gpServerName)}/{taskName}/jobs/{startResult.JobId}/cancel", new Dictionary<string, string>());

                throw;
            }
        }

        private async Task<JobResult> AwaitSucceededGPServerJob(string gpServerName, string taskName, string jobId, CancellationToken ct)
        {
            do
            {
                var result = await GetRequest<JobResult>($"{GetGPServerBaseUrl(gpServerName)}/{taskName}/jobs/{jobId}", new Dictionary<string, object>());

                switch (result.JobStatus)
                {
                    case JobStatus.Succeeded:
                        return result;
                    case JobStatus.Cancelling:
                    case JobStatus.Cancelled:
                    case JobStatus.Deleting:
                    case JobStatus.Deleted:
                    case JobStatus.Failed:
                    case JobStatus.TimedOut:
                        throw new JobResultException(result);
                }

                await Task.Delay(3000, ct); //3 seconds delay
            }
            while (!ct.IsCancellationRequested);

            ct.ThrowIfCancellationRequested();

            throw new Exception("No way to get here, but the compiler doesn't know that");
        }

        
        public async Task<SearchItemResult> SearchItem(string rootUrl, string query)
        {
            var parameters = new Dictionary<string, object> { { "q", query } };
            return await GetRequest<SearchItemResult>($"{rootUrl}/search", parameters);
        }

        public async Task WaitForFreeInstances(string featureLayerUrl, int timeOutSeconds)
        {
            try
            {
                var uriBuilder = new UriBuilder(featureLayerUrl);
                var uri = uriBuilder.Uri;

                var parts = uri.PathAndQuery.Split("/".ToCharArray()).ToList();
                var subParts = parts
                    .Take(parts.Count - 2)
                    .Skip(parts.FindIndex(p => p.Equals("services", StringComparison.CurrentCultureIgnoreCase)) + 1)
                    .ToArray();

                var serviceFolder = subParts.Length > 1 ? subParts[0] : "";
                var serviceName = subParts[subParts.Length - 1];

                while ((await GetServiceStatistics(serviceFolder, serviceName, "MapServer")).Summary.AvailableInstances <= 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(timeOutSeconds));
                }
            }
            catch (Exception)
            {
                //Ignore this. It is possible that the user does not have enough privileges
            }
        }

        public async Task<ServiceStatisticsResult> GetServiceStatistics(string serviceFolder, string serviceName,
            string serviceType)
        {
            var parameters = new Dictionary<string, object>();
            var subPath = string.IsNullOrWhiteSpace(serviceFolder) ? "" : $"/{serviceFolder}";
            subPath = $"{subPath}/{serviceName}.{serviceType}";

            var url = $"{_rootUrl}/admin/services{subPath}/statistics";
            return await GetRequest<ServiceStatisticsResult>(url, parameters);
        }

        /*
        public async Task<T> GetRequest<T>(string url, string resource, IDictionary<string, object> parameters) where T : Result
        {
            var resourceUrl = string.IsNullOrEmpty(resource) ? url : url + "/" + resource;
            var result = await GetRequest<T>(resourceUrl, parameters);
            if (result.Error != null) throw new WebException(result.Error.FullDescription);

            switch (result)
            {
                case FeatureQueryResult featureQueryResult:
                    featureQueryResult.FeatureLayer = await GetFeatureLayerInfo(url);
                    featureQueryResult.Features.ForEach(feature => feature.FeatureLayer = featureQueryResult.FeatureLayer);
                    break;
                case RecordQueryResult recordQueryResult:
                    recordQueryResult.FeatureLayer = await GetFeatureLayerInfo(url);
                    recordQueryResult.Features.ForEach(record => record.FeatureLayer = recordQueryResult.FeatureLayer);
                    break;
            }

            result.Url = resourceUrl;

            return result;
        }
        */

        public async Task<T> PostRequest<T>(string url, string resource, IDictionary<string, string> parameters) where T : Result
        {
            var resourceUrl = string.IsNullOrEmpty(resource) ? url : url + "/" + resource;
            var result = await PostRequest<T>(resourceUrl, parameters);
            if (result.Error != null) throw new WebException(result.Error.FullDescription);
            return result;
        }

        public async Task<T> GetRequest<T>(string url, IDictionary<string, object> parameters)
        {
            if (!_tokenRes.IsValid())
                await ImplicitAuthenticate();

            if (_tokenRes != null) parameters.Add("token", _tokenRes.token);

            return await Get<T>(url, parameters, _proxyUrl);
        }

        private static async Task<T> Get<T>(string url, IDictionary<string, object> parameters, string proxyUrl = null)
        {
            if (!string.IsNullOrEmpty(proxyUrl)) url = proxyUrl + "?" + url;
            parameters.Add("f", "json");

            var response = await _httpClient.GetAsync(ToUri(url, parameters));
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var result = content.FromJson<T>();
            return result;
        }

        public async Task<T> PostRequest<T>(string url, IDictionary<string, string> parameters)
        {
            if (!_tokenRes.IsValid())
                await ImplicitAuthenticate();

            if (_tokenRes != null) parameters.Add("token", _tokenRes.token);

            return await Post<T>(url, parameters, _proxyUrl);
        }

        private static async Task<T> Post<T>(string url, IDictionary<string, string> parameters, string proxyUrl = null)
        {
            if (!string.IsNullOrEmpty(proxyUrl)) url = proxyUrl + "?" + url;
            parameters.Add("f", "json");

            var encodedItems = parameters.Select(i => WebUtility.UrlEncode(i.Key) + "=" + WebUtility.UrlEncode(i.Value));
            var encodedContent = new StringContent(string.Join("&", encodedItems), null, "application/x-www-form-urlencoded");

            var response = await _httpClient.PostAsync(url, encodedContent);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var result = content.FromJson<T>();
            return result;
        }

        public async Task DownloadFile(string url, string fileName, IDictionary<string, object> parameters = null)
        {
            if (!_tokenRes.IsValid())
                await ImplicitAuthenticate();

            if (parameters == null)
                parameters = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(_proxyUrl)) url = _proxyUrl + "?" + url;
            if (_tokenRes != null) parameters.Add("token", _tokenRes.token);

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using (var fs = new FileStream(fileName, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }
        }

        public async Task<byte[]> DownloadFile(string url, IDictionary<string, object> parameters = null)
        {
            if (!_tokenRes.IsValid())
                await ImplicitAuthenticate();

            if (parameters == null)
                parameters = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(_proxyUrl)) url = _proxyUrl + "?" + url;
            if (_tokenRes != null) parameters.Add("token", _tokenRes.token);

            return await _httpClient.GetByteArrayAsync(ToUri(url, parameters));
        }

        public async Task<T> UploadFile<T>(string url, string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            return await UploadFile<T>(url, bytes, Path.GetFileName(filePath));
        }

        public async Task<T> UploadFile<T>(string url, byte[] bytes, string fileName)
        {
            if (!_tokenRes.IsValid())
                await ImplicitAuthenticate();

            if (!string.IsNullOrEmpty(_proxyUrl)) url = _proxyUrl + "?" + url;

            var multiPartContent = new MultipartFormDataContent("----WebKitFormBoundary" + Guid.NewGuid());
            var fileContents = new ByteArrayContent(bytes);

            fileContents.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                FileName = fileName,
                Name = "attachment"
            };

            fileContents.Headers.ContentType = new MediaTypeHeaderValue(MimeUtility.GetMimeMapping(fileName));
            multiPartContent.Add(fileContents);

            var gdbVersionContent = new StringContent("", Encoding.UTF8);
            gdbVersionContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "gdbVersion"
            };
            multiPartContent.Add(gdbVersionContent);

            var formatContent = new StringContent("json", Encoding.UTF8);
            formatContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "f"
            };
            multiPartContent.Add(formatContent);

            if (_tokenRes != null)
            {
                var tokenContent = new StringContent(_tokenRes.token, Encoding.UTF8);
                tokenContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = "token"
                };
                multiPartContent.Add(tokenContent);
            }

            var response = await _httpClient.PostAsync(url, multiPartContent);
            var content = await response.Content.ReadAsStringAsync();
            var result = content.FromJson<T>();

            return result;
        }

        private static string ToQueryString(IDictionary<string, object> parameters) =>
            string.Join("&", parameters.Select(x => $"{x.Key}={x.Value}"));

        private static Uri ToUri(string url, IDictionary<string, object> parameters)
        {
            var uriBuilder = new UriBuilder(url) { Query = ToQueryString(parameters) };
            // When you set .Query on UriBuilder the string is automatically url-escaped
            return uriBuilder.Uri;
        }
     
    }
    public static class JsonExtensions
    {
        private static JsonSerializerSettings GetSettings()
        {
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new StringEnumConverter());
            settings.NullValueHandling = NullValueHandling.Ignore;
            settings.DateParseHandling = DateParseHandling.None;

            return settings;
        }
        public static string ToJson(this object jsonObj)
        {
            return JsonConvert.SerializeObject(jsonObj, Formatting.None, GetSettings());
        }
        public static T FromJson<T>(this string jsonStr)
        {
            return JsonConvert.DeserializeObject<T>(jsonStr, GetSettings());
        }
    }
}
