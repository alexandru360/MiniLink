using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MiniLink.CleanupExpired;

public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();

    public async Task FunctionHandler(object e, ILambdaContext context)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await _dynamo.ScanAsync(new ScanRequest
        {
            TableName = "minilink-links",
            FilterExpression = "ttl < :now",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":now"] = new() { N = now.ToString() }
            },
            ProjectionExpression = "shortCode"
        });

        context.Logger.LogInformation($"Găsite {result.Items.Count} linkuri expirate.");

        foreach (var item in result.Items)
        {
            await _dynamo.DeleteItemAsync("minilink-links",
                new Dictionary<string, AttributeValue>
                {
                    ["shortCode"] = item["shortCode"]
                });
        }

        context.Logger.LogInformation("Cleanup complet.");
    }
}
