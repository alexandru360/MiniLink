using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MiniLink.ProcessClick;

public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();
    private static readonly AmazonSimpleNotificationServiceClient _sns = new();
    private static readonly string _topicArn = Environment.GetEnvironmentVariable("MILESTONE_TOPIC_ARN") ?? "";

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                var click = JsonSerializer.Deserialize<ClickEvent>(record.Body)!;

                await _dynamo.PutItemAsync("minilink-clicks", new Dictionary<string, AttributeValue>
                {
                    ["shortCode"] = new() { S = click.ShortCode },
                    ["clickId"]   = new() { S = click.ClickId },
                    ["timestamp"] = new() { N = click.Timestamp.ToString() },
                    ["userAgent"] = new() { S = click.UserAgent },
                });

                var countResult = await _dynamo.QueryAsync(new QueryRequest
                {
                    TableName = "minilink-clicks",
                    IndexName = "shortCode-timestamp-index",
                    KeyConditionExpression = "shortCode = :sc",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":sc"] = new() { S = click.ShortCode }
                    },
                    Select = Select.COUNT
                });

                context.Logger.LogInformation($"Click procesat: {click.ShortCode}, total: {countResult.Count}");

                if (countResult.Count >= 1000 && !string.IsNullOrEmpty(_topicArn))
                {
                    await _sns.PublishAsync(new PublishRequest
                    {
                        TopicArn = _topicArn,
                        Subject  = $"MiniLink milestone: {click.ShortCode}",
                        Message  = $"Link-ul {click.ShortCode} a atins {countResult.Count} clickuri!"
                    });
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Eroare la {record.MessageId}: {ex.Message}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}

public record ClickEvent(string ShortCode, string ClickId, long Timestamp, string UserAgent);
