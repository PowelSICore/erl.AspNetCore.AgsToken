using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace erl.AspNetCore.AgsToken
{
    public class Error
    {
        [JsonProperty("code")]
        public string Code { get; set; }
        [JsonProperty("message")]
        public string Message { get; set; }
        [JsonProperty("details")]
        public List<string> Details { get; set; }
        [JsonProperty("description")]
        public string Description { get; set; }
        [JsonIgnore]
        public string FullDescription
        {
            get
            {
                var err = Message;
                if (!string.IsNullOrEmpty(Description))
                    err += Environment.NewLine + Description;

                if (Details != null)
                    err = Details.Aggregate(err,
                        (current, errorDetail) => current + Environment.NewLine + errorDetail);

                return err;
            }
        }
    }

    public abstract class Result
    {
        [JsonProperty("error")]
        public Error Error { get; set; }
        public string Url { get; internal set; }
    }

    

    

    
    public class TokenResult : Result
    {
        [JsonProperty("token")]
        public string Token { get; set; }
        [JsonProperty("expires")]
        public DateTime Expires { get; set; }
    }

    internal static class TokenResultExtensions
    {
        public static bool IsValid(this TokenResult result)
            => result != null 
                && result.Expires.Subtract(DateTime.UtcNow) > TimeSpan.FromMinutes(5) 
                && !string.IsNullOrWhiteSpace(result.Token);
    }

    public class OIDQueryResult : Result
    {
        [JsonProperty("objectIdFieldName")]
        public string ObjectIdFieldName { get; set; }
        [JsonProperty("objectIds")]
        public long[] ObjectIds { get; set; }
    }

    public class DeleteResult : Result
    {
        [JsonProperty("success")]
        public bool Success { get; set; }
    }

    public class EditResult : Result
    {
        [JsonProperty("objectId")]
        public long ObjectId { get; set; }
        [JsonProperty("success")]
        public bool Success { get; set; }
        [JsonProperty("globalId")]
        public string GlobalId { get; set; }
    }

    public class ApplyEditResult : Result
    {
        [JsonProperty("addResults")]
        public EditResult[] AddResults { get; set; }
        [JsonProperty("updateResults")]
        public EditResult[] UpdateResults { get; set; }
        [JsonProperty("deleteResults")]
        public EditResult[] DeleteResults { get; set; }
    }

    public class UpdateResult : Result
    {
        [JsonProperty("addResults")]
        public EditResult[] AddResults { get; set; }
        [JsonProperty("updateResults")]
        public EditResult[] UpdateResults { get; set; }
    }

    
    public class AddAttachmentResult : Result
    {
        [JsonProperty("objectId")]
        public long ObjectId { get; set; }

        [JsonProperty("globalId")]
        public string GlobalId { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

    }

    public class SearchItemResult : Result
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("start")]
        public int Start { get; set; }

        [JsonProperty("num")]
        public int Num { get; set; }

        [JsonProperty("nextStart")]
        public int NextStart { get; set; }

    }

    public class Info
    {
        [JsonProperty("currentVersion")]
        public string CurrentVersion { get; set; }
        [JsonProperty("fullVersion")]
        public string FullVersion { get; set; }
        [JsonProperty("owningSystemUrl")]
        public string OwningSystemUrl { get; set; }
        [JsonProperty("authInfo")]
        public AuthInfo AuthInfo { get; set; }
    }

    public class AuthInfo
    {
        [JsonProperty("isTokenBasedSecurity")]
        public bool IsTokenBasedSecurity { get; set; }
        [JsonProperty("tokenServicesUrl")]
        public string TokenServicesUrl { get; set; }
    }

    public class GPServer
    {
        [JsonProperty("serviceDescription")]
        public string ServiceDescription { get; set; }
        [JsonProperty("tasks")]
        public string[] TaskNames { get; set; }
        [JsonProperty("executionType")]
        public GPExecutionType ExecutionType { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum GPExecutionType
    {
        [EnumMember(Value = "esriExecutionTypeSynchronous")]
        Synchronous,
        [EnumMember(Value = "esriExecutionTypeAsynchronous")]
        Asynchronous
    }

    public class JobResult
    {
        [JsonProperty("jobId")]
        public string JobId { get; set; }
        [JsonProperty("jobStatus")]
        public JobStatus JobStatus { get; set; }
        [JsonProperty("messages")]
        public JobMessage[] Messages { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum JobStatus
    {
        [EnumMember(Value = "esriJobNew")]
        New,
        [EnumMember(Value = "esriJobSubmitted")]
        Submitted,
        [EnumMember(Value = "esriJobWaiting")]
        Waiting,
        [EnumMember(Value = "esriJobExecuting")]
        Executing,
        [EnumMember(Value = "esriJobSucceeded")]
        Succeeded,
        [EnumMember(Value = "esriJobFailed")]
        Failed,
        [EnumMember(Value = "esriJobTimedOut")]
        TimedOut,
        [EnumMember(Value = "esriJobCancelling")]
        Cancelling,
        [EnumMember(Value = "esriJobCancelled")]
        Cancelled,
        [EnumMember(Value = "esriJobDeleting")]
        Deleting,
        [EnumMember(Value = "esriJobDeleted")]
        Deleted
    }

    public class JobMessage
    {
        [JsonProperty("description")]
        public string Description { get; set; }
        [JsonProperty("type")]
        public JobMessageType MessageType { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum JobMessageType
    {
        [EnumMember(Value = "esriJobMessageTypeInformative")]
        Informative,
        [EnumMember(Value = "esriJobMessageTypeWarning")]
        Warning,
        [EnumMember(Value = "esriJobMessageTypeError")]
        Error,
        [EnumMember(Value = "esriJobMessageTypeEmpty")]
        Empty,
        [EnumMember(Value = "esriJobMessageTypeAbort")]
        Abort
    }

    public class JobResultException : Exception
    {
        public JobResultException(JobResult jobResult, string message = null) : base(StringifyJobResultError(jobResult, message))
        {
            JobResult = jobResult;
        }

        public JobResult JobResult { get; }

        private static string StringifyJobResultError(JobResult result, string message = null)
        {
            var sb = new StringBuilder(message ?? $"Job '{result.JobId}' encountered an error with job status '{result.JobStatus}'");
            if (result.Messages?.Any() == true)
            {
                sb.AppendLine();
                sb.AppendLine("---- Job messages start ----");
                foreach (var m in result.Messages)
                {
                    sb.AppendLine($"{m.MessageType}: {m.Description}");
                }
                sb.AppendLine("---- Job messages end ----");
            }
            return sb.ToString();
        }
    }

    public class ServiceStatisticsResult: Result
    {
        public ServiceStatisticsSummary Summary { get; set; }

        public List<ServiceStatisticsSummary> PerMachineSummary { get; set; }
    }

    public class ServiceStatisticsSummary
    {
        [JsonProperty("folderName")]
        public string FolderName { get; set; }

        [JsonProperty("serviceName")]
        public string ServiceName { get; set; }

        [JsonProperty("type")]
        public string ServiceType { get; set; }

        [JsonProperty("max")]
        public int MaxInstances { get; set; }

        [JsonProperty("busy")]
        public int BusyInstances { get; set; }

        [JsonProperty("free")]
        public int FreeInstances { get; set; }

        public int AvailableInstances => MaxInstances - BusyInstances;

        [JsonProperty("initializing")]
        public int InitializingInstances { get; set; }

        [JsonProperty("notCreated")]
        public int NotCreatedInstances { get; set; }

        [JsonProperty("transactions")]
        public long Transactions { get; set; }

        [JsonProperty("totalBusyTime")]
        public long TotalBusyTime { get; set; }

        [JsonProperty("machineName")]
        public string MachineName { get; set; }

        [JsonProperty("isStatisticsAvailable")]
        public bool IsStatisticsAvailable { get; set; }
    }
}
