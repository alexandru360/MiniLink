using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MiniLink.RedirectLink;

public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();
    private static readonly AmazonSQSClient _sqs = new();
    private static readonly string _queueUrl = Environment.GetEnvironmentVariable("CLICK_QUEUE_URL") ?? "";

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var shortCode = request.PathParameters?["shortCode"];
        if (string.IsNullOrEmpty(shortCode))
            return new APIGatewayHttpApiV2ProxyResponse { StatusCode = 400, Body = "{\"error\":\"shortCode lipsă\"}" };

        var result = await _dynamo.GetItemAsync("minilink-links",
            new Dictionary<string, AttributeValue> { ["shortCode"] = new() { S = shortCode } });

        if (!result.Item.Any())
            return NotFound();

        var originalUrl = result.Item["originalUrl"].S;
        var ttl = long.Parse(result.Item["ttl"].N);

        if (ttl < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return NotFound();

        if (!string.IsNullOrEmpty(_queueUrl))
        {
            _ = _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = JsonSerializer.Serialize(new
                {
                    ShortCode = shortCode,
                    ClickId   = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    UserAgent = request.Headers != null && request.Headers.TryGetValue("user-agent", out var ua) ? ua : ""
                })
            });
        }

        context.Logger.LogInformation($"Redirect: {shortCode} -> {originalUrl}");
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = 301,
            Headers = new Dictionary<string, string> { ["Location"] = originalUrl }
        };
    }

    private static APIGatewayHttpApiV2ProxyResponse NotFound() => new()
    {
        StatusCode = 404,
        Body = "{\"error\":\"link negăsit sau expirat\"}",
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
    };
}
