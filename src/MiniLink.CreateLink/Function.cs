using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MiniLink.CreateLink;

public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();
    private static readonly AmazonSimpleSystemsManagementClient _ssm = new();
    private static string? _baseUrl;
    private static int _maxDays;

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        if (_baseUrl == null)
        {
            var r1 = await _ssm.GetParameterAsync(new GetParameterRequest { Name = "/minilink/base-url" });
            var r2 = await _ssm.GetParameterAsync(new GetParameterRequest { Name = "/minilink/max-link-lifetime-days" });
            _baseUrl = r1.Parameter.Value;
            _maxDays = int.Parse(r2.Parameter.Value);
        }

        var body = JsonSerializer.Deserialize<CreateLinkRequest>(request.Body ?? "{}");
        if (body?.Url == null || !Uri.TryCreate(body.Url, UriKind.Absolute, out _))
            return BadRequest("url lipsă sau invalid");

        var shortCode = GenerateCode();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_maxDays).ToUnixTimeSeconds();

        await _dynamo.PutItemAsync("minilink-links", new Dictionary<string, AttributeValue>
        {
            ["shortCode"]   = new AttributeValue { S = shortCode },
            ["originalUrl"] = new AttributeValue { S = body.Url },
            ["createdAt"]   = new AttributeValue { N = now.ToString() },
            ["ttl"]         = new AttributeValue { N = expiresAt.ToString() },
        });

        context.Logger.LogInformation($"Link creat: {shortCode} -> {body.Url}");
        return Ok(JsonSerializer.Serialize(new { shortUrl = $"{_baseUrl}/{shortCode}" }));
    }

    private static string GenerateCode()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = Guid.NewGuid().ToByteArray();
        return new string(bytes.Take(6).Select(b => chars[b % chars.Length]).ToArray());
    }

    private static APIGatewayHttpApiV2ProxyResponse Ok(string body) => new()
    {
        StatusCode = 200,
        Body = body,
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
    };

    private static APIGatewayHttpApiV2ProxyResponse BadRequest(string msg) => new()
    {
        StatusCode = 400,
        Body = $"{{\"error\":\"{msg}\"}}",
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
    };
}

public record CreateLinkRequest(string? Url);
