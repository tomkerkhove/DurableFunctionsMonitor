using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.WindowsAzure.Storage.Table;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Fluid;
using Fluid.Values;

namespace DurableFunctionsMonitor.DotNetBackend
{
    public static class Orchestration
    {
        // Handles orchestration instance operations.
        // GET /a/p/i/{taskHubName}/orchestrations('<id>')
        [FunctionName(nameof(DfmGetOrchestrationFunction))]
        public static async Task<IActionResult> DfmGetOrchestrationFunction(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Globals.ApiRoutePrefix + "/orchestrations('{instanceId}')")] HttpRequest req,
            string instanceId,
            [DurableClient(TaskHub = Globals.TaskHubRouteParamName)] IDurableClient durableClient,
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers, durableClient.TaskHubName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            var status = await durableClient.GetStatusAsync(instanceId, false, false, true);
            if (status == null)
            {
                return new NotFoundObjectResult($"Instance {instanceId} doesn't exist");
            }

            return new DetailedOrchestrationStatus(status).ToJsonContentResult(Globals.FixUndefinedsInJson);
        }

        // Handles orchestration instance operations.
        // GET /a/p/i/{taskHubName}/orchestrations('<id>')/history
        [FunctionName(nameof(DfmGetOrchestrationHistoryFunction))]
        public static async Task<IActionResult> DfmGetOrchestrationHistoryFunction(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Globals.ApiRoutePrefix + "/orchestrations('{instanceId}')/history")] HttpRequest req,
            string instanceId,
            [DurableClient(TaskHub = Globals.TaskHubRouteParamName)] IDurableClient durableClient,
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers, durableClient.TaskHubName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            var status = await GetInstanceStatus(instanceId, durableClient, log);
            if (status == null)
            {
                return new NotFoundObjectResult($"Instance {instanceId} doesn't exist");
            }

            var history = status.History == null ? new JArray() : status.History;
            var totalCount = history.Count;

            return new 
            {
                totalCount,
                history = history.ApplySkip(req.Query).ApplyTop(req.Query)
            }
            .ToJsonContentResult(Globals.FixUndefinedsInJson);
        }

        // Handles orchestration instance operations.
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/purge
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/rewind
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/terminate
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/raise-event
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/set-custom-status
        // POST /a/p/i/{taskHubName}/orchestrations('<id>')/restart
        [FunctionName(nameof(DfmPostOrchestrationFunction))]
        public static async Task<IActionResult> DfmPostOrchestrationFunction(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Globals.ApiRoutePrefix + "/orchestrations('{instanceId}')/{action?}")] HttpRequest req,
            string instanceId,
            string action,
            [DurableClient(TaskHub = Globals.TaskHubRouteParamName)] IDurableClient durableClient, 
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers, durableClient.TaskHubName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            // Checking that we're not in ReadOnly mode
            if (DfmEndpoint.Settings.Mode == DfmMode.ReadOnly)
            {
                log.LogError("Endpoint is in ReadOnly mode");
                return new StatusCodeResult(403);
            }

            string bodyString = await req.ReadAsStringAsync();

            switch (action)
            {
                case "purge":
                    await durableClient.PurgeInstanceHistoryAsync(instanceId);
                    break;
                case "rewind":
                    await durableClient.RewindAsync(instanceId, bodyString);
                    break;
                case "terminate":
                    await durableClient.TerminateAsync(instanceId, bodyString);
                    break;
                case "raise-event":

                    dynamic bodyObject = JObject.Parse(bodyString);
                    string eventName = bodyObject.name;
                    JObject eventData = bodyObject.data;

                    var match = ExpandedOrchestrationStatus.EntityIdRegex.Match(instanceId);
                    // if this looks like an Entity
                    if(match.Success)
                    {
                        // then sending signal
                        var entityId = new EntityId(match.Groups[1].Value, match.Groups[2].Value);

                        await durableClient.SignalEntityAsync(entityId, eventName, eventData);
                    }
                    else 
                    {
                        // otherwise raising event
                        await durableClient.RaiseEventAsync(instanceId, eventName, eventData);
                    }

                    break;
                case "set-custom-status":

                    // Updating the table directly, as there is no other known way
                    var table = TableClient.GetTableClient().GetTableReference($"{durableClient.TaskHubName}Instances");

                    var orcEntity = (await table.ExecuteAsync(TableOperation.Retrieve(instanceId, string.Empty))).Result as DynamicTableEntity;

                    if (string.IsNullOrEmpty(bodyString))
                    {
                        orcEntity.Properties.Remove("CustomStatus");
                    }
                    else
                    {
                        // Ensuring that it is at least a valid JSON
                        string customStatus = JObject.Parse(bodyString).ToString();
                        orcEntity.Properties["CustomStatus"] = new EntityProperty(customStatus);
                    }

                    await table.ExecuteAsync(TableOperation.Replace(orcEntity));

                    break;
                case "restart":
                    bool restartWithNewInstanceId = ((dynamic)JObject.Parse(bodyString)).restartWithNewInstanceId;

                    await durableClient.RestartAsync(instanceId, restartWithNewInstanceId);
                    break;
                default:
                    return new NotFoundResult();
            }

            return new OkResult();
        }

        // Renders a custom tab liquid template for this instance and returns the resulting HTML.
        // Why is it POST and not GET? Exactly: because we don't want to allow to navigate to this page directly (bypassing Content Security Policies)
        // POST /a/p/i{taskHubName}//orchestrations('<id>')/custom-tab-markup
        [FunctionName(nameof(DfmGetOrchestrationTabMarkupFunction))]
        public static async Task<IActionResult> DfmGetOrchestrationTabMarkupFunction(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Globals.ApiRoutePrefix + "/orchestrations('{instanceId}')/custom-tab-markup('{templateName}')")] HttpRequest req,
            string instanceId,
            string templateName,
            [DurableClient(TaskHub = Globals.TaskHubRouteParamName)] IDurableClient durableClient,
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers, durableClient.TaskHubName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            var status = await GetInstanceStatus(instanceId, durableClient, log);
            if (status == null)
            {
                return new NotFoundObjectResult($"Instance {instanceId} doesn't exist");
            }

            // The underlying Task never throws, so it's OK.
            var templatesMap = await CustomTemplates.GetTabTemplatesAsync();

            string templateCode = templatesMap.GetTemplate(status.GetEntityTypeName(), templateName);
            if (templateCode == null)
            {
                return new NotFoundObjectResult("The specified template doesn't exist");
            }

            try
            {
                var fluidTemplate = FluidTemplate.Parse(templateCode);
                var fluidContext = new TemplateContext(status);

                return new ContentResult()
                {
                    Content = fluidTemplate.Render(fluidContext),
                    ContentType = "text/html; charset=UTF-8"
                };
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
        }

        static Orchestration()
        {
            // Some Fluent-related initialization
            TemplateContext.GlobalMemberAccessStrategy.Register<JObject, object>((obj, fieldName) => obj[fieldName]);
            FluidValue.SetTypeMapping(typeof(JObject), obj => new ObjectValue(obj));
            FluidValue.SetTypeMapping(typeof(JValue), obj => FluidValue.Create(((JValue)obj).Value));
        }

        private static readonly string[] SubOrchestrationEventTypes = new[]
        {
            "SubOrchestrationInstanceCompleted",
            "SubOrchestrationInstanceFailed",
        };

        private static async Task<DetailedOrchestrationStatus> GetInstanceStatus(string instanceId, IDurableClient durableClient, ILogger log)
        {
            // Also trying to load SubOrchestrations _in parallel_
            var subOrchestrationsTask = GetSubOrchestrationsAsync(durableClient.TaskHubName, instanceId);

#pragma warning disable 4014 // Intentionally not awaiting and swallowing potential exceptions

            subOrchestrationsTask.ContinueWith(t => log.LogWarning(t.Exception, "Unable to load SubOrchestrations, but that's OK"),
                TaskContinuationOptions.OnlyOnFaulted);
                
#pragma warning restore 4014

            var status = await durableClient.GetStatusAsync(instanceId, true, true, true);
            if (status == null)
            {
                return null;
            }

            TryMatchingSubOrchestrations(status.History, subOrchestrationsTask);
            ConvertScheduledTime(status.History);

            return new DetailedOrchestrationStatus(status);
        }

        // Tries to get all SubOrchestration instanceIds for a given Orchestration
        private static async Task<IEnumerable<HistoryEntity>> GetSubOrchestrationsAsync(string taskHubName, string instanceId)
        {
            // Querying the table directly, as there is no other known way
            var table = TableClient.GetTableClient().GetTableReference($"{taskHubName}History");

            var query = new TableQuery<HistoryEntity>()
                .Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, instanceId),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("EventType", QueryComparisons.Equal, "SubOrchestrationInstanceCreated")
                ));

            return (await table.GetAllAsync(query)).OrderBy(he => he._Timestamp);
        }

        private static void ConvertScheduledTime(JArray history)
        {
            if (history == null)
            {
                return;
            }

            var orchestrationStartedEvent = history.FirstOrDefault(h => h.Value<string>("EventType") == "ExecutionStarted");

            foreach (var e in history)
            {
                if (e["ScheduledTime"] != null)
                {
                    // Converting to UTC and explicitly formatting as a string (otherwise default serializer outputs it as a local time)
                    var scheduledTime = e.Value<DateTime>("ScheduledTime").ToUniversalTime();
                    e["ScheduledTime"] = scheduledTime.ToString("o");

                    // Also adding DurationInMs field
                    var timestamp = e.Value<DateTime>("Timestamp").ToUniversalTime();
                    var duration = timestamp - scheduledTime;
                    e["DurationInMs"] = duration.TotalMilliseconds;
                }

                // Also adding duration of the whole orchestration
                if (e.Value<string>("EventType") == "ExecutionCompleted" && orchestrationStartedEvent != null)
                {
                    var scheduledTime = orchestrationStartedEvent.Value<DateTime>("Timestamp").ToUniversalTime();
                    var timestamp = e.Value<DateTime>("Timestamp").ToUniversalTime();
                    var duration = timestamp - scheduledTime;
                    e["DurationInMs"] = duration.TotalMilliseconds;
                }
            }
        }

        private static void TryMatchingSubOrchestrations(JArray history, Task<IEnumerable<HistoryEntity>> subOrchestrationsTask)
        {
            if (history == null)
            {
                return;
            }

            var subOrchestrationEvents = history
                .Where(h => SubOrchestrationEventTypes.Contains(h.Value<string>("EventType")))
                .ToList();

            if (subOrchestrationEvents.Count <= 0)
            {
                return;
            }

            try
            {
                foreach (var subOrchestration in subOrchestrationsTask.Result)
                {
                    // Trying to match by SubOrchestration name and start time
                    var matchingEvent = subOrchestrationEvents.FirstOrDefault(e =>
                        e.Value<string>("FunctionName") == subOrchestration.Name &&
                        e.Value<DateTime>("ScheduledTime") == subOrchestration._Timestamp
                    );

                    if (matchingEvent == null)
                    {
                        continue;
                    }

                    // Modifying the event object
                    matchingEvent["SubOrchestrationId"] = subOrchestration.InstanceId;

                    // Dropping this line, so that multiple suborchestrations are correlated correctly
                    subOrchestrationEvents.Remove(matchingEvent);
                }
            }
            catch (Exception)
            {
                // Intentionally swallowing any exceptions here
            }

            return;
        }

        private static IEnumerable<JToken> ApplyTop(this IEnumerable<JToken> history, IQueryCollection query)
        {
            var clause = query["$top"];
            return clause.Any() ? history.Take(int.Parse(clause)) : history;
        }
        private static IEnumerable<JToken> ApplySkip(this IEnumerable<JToken> history, IQueryCollection query)
        {
            var clause = query["$skip"];
            return clause.Any() ? history.Skip(int.Parse(clause)) : history;
        }
    }

    // Represents an record in XXXHistory table
    class HistoryEntity : TableEntity
    {
        public string InstanceId { get; set; }
        public string Name { get; set; }
        public DateTimeOffset _Timestamp { get; set; }
    }
}