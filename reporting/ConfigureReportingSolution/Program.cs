using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

var appName = "ConfigureReportingSolution.exe";

var solutionName = "DQTReportingPOC";  // TODO Change to "DQT Reporting"
var serviceEndpointName = "DQT Reporting POC";  // TODO Change to "Azure Service Bus"

var usage = $@"{appName} --crm-url <CRM environment URL> --client-id <client ID> --client-secret <client secret>";

[DoesNotReturn]
static void Error(params string[] lines)
{
    foreach (var line in lines)
    {
        Console.Error.WriteLine(line);
    }

    Environment.Exit(1);
}

var switchMappings = new Dictionary<string, string>()
{
    { "--crm-url", "CrmUrl" },
    { "--client-id", "CrmClientId" },
    { "--client-secret", "CrmClientSecret" }
};

var configuration = new ConfigurationBuilder()
    .AddCommandLine(args, switchMappings)
    .Build();

void CheckArgumentProvided(string swtch)
{
    var configurationKey = switchMappings[swtch];

    if (string.IsNullOrEmpty(configuration[configurationKey]))
    {
        Error($"Missing {swtch} argument.", "", usage);
    }
}

CheckArgumentProvided("--crm-url");
CheckArgumentProvided("--client-id");
CheckArgumentProvided("--client-secret");

var crmUrl = new Uri(configuration["CrmUrl"]);
var crmClientId = configuration["CrmClientId"];
var crmClientSecret = configuration["CrmClientSecret"];

ServiceClient serviceClient = new(crmUrl, crmClientId, crmClientSecret, useUniqueInstance: false);

async Task<Entity> FindEntity(string entityName, IDictionary<string, object> matchAttributes)
{
    var query = new QueryExpression(entityName)
    {
        Criteria = new FilterExpression(LogicalOperator.And),
        ColumnSet = new(allColumns: true)
    };

    foreach (var attr in matchAttributes)
    {
        query.Criteria.AddCondition(attr.Key, ConditionOperator.Equal, attr.Value);
    }

    var result = await serviceClient.RetrieveMultipleAsync(query);

    if (result.Entities.Count > 1)
    {
        throw new Exception($"Multiple {entityName} entities found matching criteria.");
    }

    if (result.Entities.Count == 0)
    {
        throw new Exception($"Could not find matching {entityName} entity.");
    }

    return result.Entities.Single();
}

/// <summary>
/// Creates a <see cref="Dictionary{string, object}"/> given pairs of keys and values.
/// </summary>
static Dictionary<string, object> D(params object[] keysWithValues)
{
    if (keysWithValues.Length % 2 != 0)
    {
        throw new ArgumentException("Mis-matched number of keys and values.", nameof(keysWithValues));
    }

    var result = new Dictionary<string, object>();

    for (var i = 0; i < keysWithValues.Length; i += 2)
    {
        result.Add((string)keysWithValues[i], keysWithValues[i + 1]);
    }

    return result;
}

var solution = await FindEntity("solution", D("uniquename", solutionName));

var serviceEndpoint = await FindEntity("serviceendpoint", D("name", serviceEndpointName));

Task<Entity> GetMessage(string messageName) => FindEntity("sdkmessage", D("name", messageName));
var messages = new[] { await GetMessage("create"), await GetMessage("update")/*, await GetMessageId("delete")*/ };

var reportingConfig = JsonNode.Parse(File.ReadAllText("config.json"))!;

foreach (var (entity, entityConfig) in reportingConfig["entities"]!.AsObject())
{
    Console.WriteLine($"Configuring {entity} entity..");

    var entityMetadata = serviceClient.GetEntityMetadata(entity);
    var entityDisplayName = entityMetadata.DisplayName.LocalizedLabels.First().Label;

    foreach (var message in messages)
    {
        var messageFilter = await FindEntity(
            "sdkmessagefilter",
            D("sdkmessageid", message.Id, "primaryobjecttypecode", entity, "secondaryobjecttypecode", "none"));

        async Task<Guid?> FindSdkMessageProcessingStep()
        {
            // Can't use FindEntity here since I can't figure out how to add a 'eventhandler' predicate to the query :-/

            var query = new QueryExpression("sdkmessageprocessingstep")
            {
                Criteria = new FilterExpression(LogicalOperator.And),
                ColumnSet = new(allColumns: true)
            };

            query.Criteria.AddCondition("sdkmessagefilterid", ConditionOperator.Equal, messageFilter.Id);
            query.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, message.Id);
            query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, serviceEndpoint.GetAttributeValue<Guid>("solutionid"));

            var result = await serviceClient.RetrieveMultipleAsync(query);

            return result.Entities.SingleOrDefault(e => e.GetAttributeValue<EntityReference>("eventhandler")?.Id == serviceEndpoint.Id)?.Id;
        }

        var stepId = await FindSdkMessageProcessingStep();

        var messageName = message.GetAttributeValue<string>("name");
        var stepName = $"{serviceEndpoint.GetAttributeValue<string>("name")}: {messageName} of {entityDisplayName}";
        var stepDescription = $"{messageName} of {entityDisplayName}";

        var stepEntity = new Entity()
        {
            LogicalName = "sdkmessageprocessingstep",
            Attributes = new()
            {
                { "sdkmessageid", new EntityReference("sdkmessage", message.Id) },
                { "sdkmessagefilterid", new EntityReference("sdkmessagefilter", messageFilter.Id) },
                { "eventhandler", new EntityReference("serviceendpoint", serviceEndpoint.Id) },
                { "name", stepName },
                { "description", stepDescription },
                { "mode", new OptionSetValue(1) },  // Asynchronous
                { "rank", 1 },
                { "stage", new OptionSetValue(40) },  // PostOperation
                { "supporteddeployment", new OptionSetValue(0) },  // ServerOnly
                { "asyncautodelete", true }
            }
        };

        if (stepId is null)
        {
            stepId = await serviceClient.CreateAsync(stepEntity);
            Console.WriteLine($"  created step for {messageName}");
        }
        else
        {
            stepEntity.Id = stepId.Value;
            await serviceClient.UpdateAsync(stepEntity);
            Console.WriteLine($"  updated step for {messageName}");
        }

        await serviceClient.ExecuteAsync(new AddSolutionComponentRequest()
        {
            ComponentType = 92,  // SDKMessageProcessingStep - reference https://github.com/microsoft/PowerApps-Samples/blob/1adb4891a312555a2c36cfe7b99c0a225a934a0d/cds/webapi/C%23/MetadataOperations/MetadataTypes/SolutionComponentType.cs
            ComponentId = stepId.Value,
            SolutionUniqueName = solutionName
        });
        Console.WriteLine($"    added step to {solutionName} solution");
    }

    Console.WriteLine();
}

Console.WriteLine("Done");
