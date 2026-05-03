# MiniLink — Tutorial complet
## URL Shortener cu Click Analytics pe AWS Free Tier

**Timp total estimat:** ~8 ore  
**Cost:** $0  
**Ce construiești:** API care scurtează URL-uri, redirect cu tracking de clickuri, frontend static, cleanup automat zilnic

---

## Cum navighez în consola AWS (citește asta întâi)

Consola AWS s-a schimbat mult. Iată ce trebuie să știi:

- **Bara de căutare** e în partea de sus a paginii (scrie numele serviciului acolo, ex: `DynamoDB`). Asta e cel mai rapid mod să ajungi oriunde.
- **Region-ul** e vizibil în colțul din dreapta sus, lângă numele contului — asigură-te că ești pe **eu-west-1 (Ireland)** tot timpul. Dacă nu e, dai click pe numele regiunii și selectezi `Europe (Ireland) eu-west-1`.
- **Meniul hamburger** (trei linii orizontale, stânga sus) deschide lista de servicii recente și favorite.
- Când dai click pe un serviciu din search bar, se deschide consola acelui serviciu cu meniul lui propriu în stânga.

---

## Ce ai nevoie înainte să începi

- Cont AWS (free tier) — dacă nu ai, creează acum la aws.amazon.com, durează 5 min
- .NET 8 SDK instalat local
- AWS CLI instalat local (`aws --version`)
- `dotnet tool install -g Amazon.Lambda.Tools` (o singură dată)
- Un editor (VS Code sau Rider)

---

### PASUL 1 — Cont AWS + securitate de bază (~15 min)

**Activează MFA pe contul root:**

1. Mergi la [console.aws.amazon.com](https://console.aws.amazon.com) și loghează-te
2. În dreapta sus vei vedea numele contului tău (sau un număr de cont) — **click pe el**
3. Din meniul dropdown care apare, click pe **Security credentials**
4. Ești acum în pagina "My security credentials"
5. Caută secțiunea **Multi-factor authentication (MFA)** — e cam la mijlocul paginii
6. Click pe butonul **Assign MFA device**
7. Dă un nume device-ului (ex: `my-phone`), selectează **Authenticator app** → Next
8. Deschide Google Authenticator pe telefon → Add account → Scan QR code → scanează ce apare pe ecran
9. Introdu două coduri consecutive din app → **Add MFA**
10. **Nu folosi contul root pentru nimic altceva de acum înainte**

**Creează un utilizator IAM pentru lucru zilnic:**

1. În bara de căutare din partea de sus, scrie `IAM` și apasă Enter sau click pe **IAM** în rezultate
2. Ești acum în consola IAM — în meniul din **stânga** vei vedea o listă: Users, User groups, Roles, etc.
3. Click pe **Users** din meniul din stânga
4. Click pe butonul portocaliu **Create user** (dreapta sus)
5. **User name:** `minilink-dev` → click **Next**
6. La "Set permissions" selectează **Attach policies directly** (al treilea radio button)
7. În caseta de search din tabel scrie `AdministratorAccess` și bifează căsuța din stânga rezultatului
8. Click **Next** → click **Create user**
9. Click pe userul `minilink-dev` din listă ca să intri în detaliile lui
10. Click pe tab-ul **Security credentials** (e al doilea tab, după Summary)
11. Derulează în jos până la secțiunea **Access keys** → click **Create access key**
12. Selectează **Command Line Interface (CLI)** → bifează checkbox-ul de confirmare de jos → **Next**
13. Description tag: lasă gol sau scrie `minilink-dev-key` → **Create access key**
14. **IMPORTANT: copiază sau descarcă CSV-ul ACUM** — după ce închizi pagina asta, secretul nu mai e vizibil

**Configurează AWS CLI local:**
```bash
aws configure --profile minilink
# AWS Access Key ID: [cheia de mai sus]
# AWS Secret Access Key: [secretul de mai sus]
# Default region name: eu-west-1
# Default output format: json
```

**Verificare:**
```bash
aws sts get-caller-identity --profile minilink
# Trebuie să vezi account ID + user ARN
```

---

### PASUL 2 — DynamoDB (~20 min)

**Navighează la DynamoDB:**
1. Click în bara de căutare din partea de sus, scrie `DynamoDB`, click pe **DynamoDB** în rezultate
2. Ești în consola DynamoDB — în meniul din **stânga** dai click pe **Tables**
3. Click pe butonul portocaliu **Create table** (dreapta sus)

**Creează Tabel 1: minilink-links** (stochează URL-urile scurtate)

Pe pagina "Create table":
- **Table name:** `minilink-links`
- **Partition key:** `shortCode` — în dropdown din dreapta selectează **String** (dacă nu e deja)
- Sort key: lasă **necompletat** (nu adaugăm sort key la asta)
- Mai jos găsești **Table settings** — selectează **Customize settings** (al doilea radio button)
- **Table class:** DynamoDB Standard (primul, e selectat deja)
- La **Read/write capacity settings** selectează **Provisioned**
  - Read capacity units: `1`
  - Write capacity units: `1`
  - **Asta e Free Tier — nu schimba la On-demand că plătești**
- Click **Create table** (buton portocaliu jos-dreapta)
- Aștepți ~10 secunde până statusul devine **Active**

**Creează Tabel 2: minilink-clicks** (stochează clickurile)

Click pe **Create table** din nou:
- **Table name:** `minilink-clicks`
- **Partition key:** `shortCode` → String
- **Sort key:** `clickId` → String (bifezi "Add sort key" și completezi)
- Table settings: **Customize settings** → Provisioned → Read=1, Write=1
- Click **Create table**

**Activează TTL pe tabelul minilink-links:**
1. Click pe `minilink-links` din lista de tabele ca să intri în el
2. Vei vedea tab-uri în partea de sus: Overview, Indexes, Monitor, Global tables, Backups, **Additional settings** — click pe **Additional settings**
3. Derulează în jos până la secțiunea **Time to Live (TTL)**
4. Click pe **Enable**
5. **TTL attribute name:** `ttl`
6. Click **Save changes**

**Creează un GSI pe minilink-clicks:**
1. Click pe `minilink-clicks` din lista de tabele
2. Click pe tab-ul **Indexes**
3. Click pe **Create index**
4. **Partition key:** `shortCode` → String
5. **Sort key:** `timestamp` → **Number** (schimbă din dropdown!)
6. **Index name:** `shortCode-timestamp-index` (se completează automat, lasă-l)
7. **Projected attributes:** All
8. Capacity: Provisioned → Read=1, Write=1
9. Click **Create index** — apare cu status "Creating", durează ~1 min

---

### PASUL 3 — SSM Parameter Store (~10 min)

**Navighează la Systems Manager:**
1. În bara de căutare scrie `Systems Manager` → click pe **Systems Manager**
2. Ești în consola Systems Manager — meniul din **stânga** e lung, caută **Parameter Store** (e cam la mijloc, în secțiunea "Application Management")
3. Click pe **Parameter Store**
4. Click pe **Create parameter** (buton portocaliu dreapta sus)

**Creează Parametru 1:**
- **Name:** `/minilink/max-link-lifetime-days`
- **Description:** lasă gol
- **Tier:** Standard (e selectat implicit — nu schimba, Standard e gratuit)
- **Type:** String
- **Value:** `30`
- Click **Create parameter**

Click pe **Create parameter** din nou pentru al doilea:

**Creează Parametru 2:**
- **Name:** `/minilink/base-url`
- **Tier:** Standard
- **Type:** String
- **Value:** `https://placeholder.cloudfront.net`
  - Atenție: asta e un placeholder pe care îl actualizezi la Pasul 11 după ce creezi CloudFront
- Click **Create parameter**

---

### PASUL 4 — Codul Lambda (.NET) (~2.5h)

Acesta e pasul de cod — nu mai e nevoie de consolă AWS, lucrezi local în terminal.

Deschide terminalul și creează proiectul:

```bash
mkdir -p ~/Dev/learning-for-jobs/MiniLink/src
cd ~/Dev/learning-for-jobs/MiniLink/src

dotnet new lambda.EmptyFunction -n MiniLink.CreateLink --framework net8.0
dotnet new lambda.EmptyFunction -n MiniLink.RedirectLink --framework net8.0
dotnet new lambda.EmptyFunction -n MiniLink.ProcessClick --framework net8.0
dotnet new lambda.EmptyFunction -n MiniLink.CleanupExpired --framework net8.0
```

**Adaugă NuGet packages în fiecare proiect** (editează fiecare `.csproj`):

```xml
<!-- În MiniLink.CreateLink/MiniLink.CreateLink.csproj — adaugă în <ItemGroup> -->
<PackageReference Include="Amazon.Lambda.APIGatewayEvents" Version="2.7.0" />
<PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.305" />
<PackageReference Include="AWSSDK.SimpleSystemsManagement" Version="3.7.305" />
<PackageReference Include="AWSSDK.SQS" Version="3.7.300" />
```

```xml
<!-- În MiniLink.RedirectLink — la fel ca CreateLink -->
```

```xml
<!-- În MiniLink.ProcessClick/MiniLink.ProcessClick.csproj -->
<PackageReference Include="Amazon.Lambda.SQSEvents" Version="5.1.0" />
<PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.305" />
<PackageReference Include="AWSSDK.SimpleNotificationService" Version="3.7.300" />
```

```xml
<!-- În MiniLink.CleanupExpired/MiniLink.CleanupExpired.csproj -->
<PackageReference Include="Amazon.Lambda.CloudWatchEvents" Version="4.2.0" />
<PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.305" />
```

---

**`src/MiniLink.CreateLink/src/MiniLink.CreateLink/Function.cs`** — înlocuiește tot cu:

```csharp
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
        // citim SSM o singură dată per cold start
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

        return Ok(JsonSerializer.Serialize(new { shortUrl = $"{_baseUrl}/{shortCode}" }));
    }

    private static string GenerateCode()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = Guid.NewGuid().ToByteArray();
        return new string(bytes.Take(6).Select(b => chars[b % chars.Length]).ToArray());
    }

    private static APIGatewayHttpApiV2ProxyResponse Ok(string body) =>
        new() { StatusCode = 200, Body = body, Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" } };

    private static APIGatewayHttpApiV2ProxyResponse BadRequest(string msg) =>
        new() { StatusCode = 400, Body = $"{{\"error\":\"{msg}\"}}" };
}

public record CreateLinkRequest(string? Url);
```

---

**`src/MiniLink.RedirectLink/src/MiniLink.RedirectLink/Function.cs`:**

```csharp
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
            return new APIGatewayHttpApiV2ProxyResponse { StatusCode = 400 };

        var result = await _dynamo.GetItemAsync("minilink-links",
            new Dictionary<string, AttributeValue> { ["shortCode"] = new() { S = shortCode } });

        if (!result.Item.Any()) return NotFound();

        var originalUrl = result.Item["originalUrl"].S;
        var ttl = long.Parse(result.Item["ttl"].N);
        if (ttl < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return NotFound();

        // fire-and-forget click event (nu blocăm redirect-ul)
        _ = _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = JsonSerializer.Serialize(new
            {
                shortCode,
                clickId  = Guid.NewGuid().ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                userAgent = request.Headers?.GetValueOrDefault("user-agent") ?? ""
            })
        });

        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = 301,
            Headers = new Dictionary<string, string> { ["Location"] = originalUrl }
        };
    }

    private static APIGatewayHttpApiV2ProxyResponse NotFound() =>
        new() { StatusCode = 404, Body = "{\"error\":\"link negăsit sau expirat\"}" };
}
```

---

**`src/MiniLink.ProcessClick/src/MiniLink.ProcessClick/Function.cs`:**

```csharp
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

                // numărăm clickuri total — dacă depășim 1000 notificăm
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
                context.Logger.LogError($"Eroare la procesare {record.MessageId}: {ex.Message}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}

public record ClickEvent(string ShortCode, string ClickId, long Timestamp, string UserAgent);
```

---

**`src/MiniLink.CleanupExpired/src/MiniLink.CleanupExpired/Function.cs`:**

```csharp
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MiniLink.CleanupExpired;

public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();

    public async Task FunctionHandler(CloudWatchEvent<object> e, ILambdaContext context)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // TTL face asta automat, dar facem și manual pentru exercițiu
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

        context.Logger.LogInformation($"Găsite {result.Items.Count} linkuri expirate de șters.");

        foreach (var item in result.Items)
        {
            await _dynamo.DeleteItemAsync("minilink-links",
                new Dictionary<string, AttributeValue> { ["shortCode"] = item["shortCode"] });
        }

        context.Logger.LogInformation("Cleanup complet.");
    }
}
```

---

**Build & package toate Lambda-urile:**

```bash
cd ~/Dev/learning-for-jobs/MiniLink/src

for proj in CreateLink RedirectLink ProcessClick CleanupExpired; do
  cd MiniLink.$proj/src/MiniLink.$proj
  dotnet lambda package -o function.zip
  cd ../../..
done
```

Verificare — trebuie să ai:
```
MiniLink.CreateLink/src/MiniLink.CreateLink/function.zip
MiniLink.RedirectLink/src/MiniLink.RedirectLink/function.zip
MiniLink.ProcessClick/src/MiniLink.ProcessClick/function.zip
MiniLink.CleanupExpired/src/MiniLink.CleanupExpired/function.zip
```

---

### PASUL 5 — IAM Role pentru Lambda (~15 min)

**Navighează la IAM:**
1. Bara de căutare → scrie `IAM` → click pe **IAM**
2. Meniu stânga → click pe **Roles**
3. Click pe butonul portocaliu **Create role** (dreapta sus)

**Configurează role-ul:**
1. **Trusted entity type:** selectează **AWS service** (primul radio button)
2. La **Use case** caută în lista de jos `Lambda` și selecteaz-o → click **Next**
3. Ești acum pe pagina "Add permissions" — trebuie să adaugi 5 policies, una câte una:
   - În caseta de căutare scrie `AmazonDynamoDBFullAccess` → bifează căsuța din stânga
   - Șterge căutarea, scrie `AmazonSQSFullAccess` → bifează
   - Șterge, scrie `AmazonSNSFullAccess` → bifează
   - Șterge, scrie `AmazonSSMReadOnlyAccess` → bifează
   - Șterge, scrie `CloudWatchLogsFullAccess` → bifează
4. Click **Next**
5. **Role name:** `minilink-lambda-role`
6. Click **Create role**

> **Notă pentru interviu:** În producție nu dai `FullAccess` pe un role de Lambda. Folosești politici customizate cu resurse specifice (ARN-ul exact al tabelei, nu `*`). CDK face asta automat cu `table.grantReadWriteData(fn)`.

---

### PASUL 6 — SQS Queue (~10 min)

**Navighează la SQS:**
1. Bara de căutare → scrie `SQS` → click pe **Simple Queue Service**
2. Click pe butonul portocaliu **Create queue** (dreapta sus)

**Configurează queue-ul principal:**
- **Type:** selectează **Standard** (primul — nu FIFO, nu avem nevoie de ordering strict)
- **Name:** `minilink-clicks`
- Derulează în jos la **Configuration:**
  - **Visibility timeout:** schimbă din 30 în `30` seconds (verifică să fie 30)
  - Message retention period: 4 days (lasă default)
- Derulează mai jos la **Dead-letter queue:**
  - Click pe **Enabled**
  - La **Choose queue** click pe **Create new** — se deschide un câmp nou
  - Queue name: `minilink-clicks-dlq`
  - **Maximum receives:** `3`
- Click **Create queue** (buton portocaliu jos)

**Copiază URL-ul queue-ului:**
- După creare ești pe pagina queue-ului — în secțiunea **Details** găsești **URL**
- Copiază-l undeva (îl vei folosi la Lambda 2 din pasul următor)
- Format: `https://sqs.eu-west-1.amazonaws.com/123456789/minilink-clicks`

---

### PASUL 7 — SNS Topic (~5 min)

**Navighează la SNS:**
1. Bara de căutare → scrie `SNS` → click pe **Simple Notification Service**
2. Meniu stânga → click pe **Topics**
3. Click pe butonul portocaliu **Create topic**

**Configurează topic-ul:**
- **Type:** selectează **Standard**
- **Name:** `minilink-milestones`
- Restul setărilor le lași default
- Click **Create topic**

**Copiază ARN-ul topic-ului:**
- Ești acum pe pagina topic-ului — în secțiunea **Details** găsești **ARN**
- Copiază-l undeva (format: `arn:aws:sns:eu-west-1:123456789:minilink-milestones`)

**Adaugă subscription (email):**
1. Mai jos pe aceeași pagină sau din meniu stânga → **Subscriptions** → click **Create subscription**
2. **Topic ARN:** e deja completat cu ARN-ul tău
3. **Protocol:** din dropdown selectează **Email**
4. **Endpoint:** scrie emailul tău
5. Click **Create subscription**
6. Deschide emailul — vei primi un email de la AWS cu subiectul "AWS Notification - Subscription Confirmation" → click pe linkul de confirmare din email

---

### PASUL 8 — Creează funcțiile Lambda în consolă (~30 min)

**Navighează la Lambda:**
1. Bara de căutare → scrie `Lambda` → click pe **Lambda**
2. Meniu stânga → click pe **Functions**
3. Click pe butonul portocaliu **Create function**

Repetă procesul de mai jos pentru fiecare din cele 4 funcții.

---

#### Lambda 1: minilink-create-link

Pe pagina "Create function":
- Selectează **Author from scratch** (primul radio button, e selectat deja)
- **Function name:** `minilink-create-link`
- **Runtime:** dă click pe dropdown → caută și selectează **.NET 8 (C#/PowerShell)**
- **Architecture:** x86_64 (e selectat deja)
- Derulează în jos la **Change default execution role**:
  - Selectează **Use an existing role**
  - Din dropdown selectează `minilink-lambda-role`
- Click **Create function**

Ești acum pe pagina funcției. Urmează câteva sub-pași:

**Uploadează codul:**
1. Ești pe tab-ul **Code** (primul tab de sus)
2. Click pe butonul **Upload from** (dreapta sus în secțiunea "Code source") → selectează **.zip file**
3. Click **Upload** → navighează la `~/Dev/learning-for-jobs/MiniLink/src/MiniLink.CreateLink/src/MiniLink.CreateLink/function.zip` → Open
4. Click **Save**
5. Mai jos, în secțiunea **Runtime settings**, click pe **Edit**
6. **Handler:** `MiniLink.CreateLink::MiniLink.CreateLink.Function::FunctionHandler`
7. Click **Save**

**Setează timeout:**
1. Click pe tab-ul **Configuration** (al doilea tab)
2. În meniul din stânga al tab-ului Configuration, click pe **General configuration**
3. Click pe butonul **Edit** (dreapta sus)
4. **Timeout:** schimbă în `0` min `29` sec
5. Click **Save**

---

#### Lambda 2: minilink-redirect-link

Click pe **Functions** din meniu stânga → **Create function** din nou:
- **Function name:** `minilink-redirect-link`
- **Runtime:** .NET 8 (C#/PowerShell)
- **Execution role:** Use existing → `minilink-lambda-role`
- Click **Create function**

**Upload cod:**
- Tab **Code** → Upload from → .zip file → `MiniLink.RedirectLink/src/MiniLink.RedirectLink/function.zip`
- Runtime settings → Edit → **Handler:** `MiniLink.RedirectLink::MiniLink.RedirectLink.Function::FunctionHandler` → Save

**Timeout:**
- Tab **Configuration** → General configuration → Edit → 0 min 29 sec → Save

**Environment variables:**
1. Tab **Configuration** → în meniu stânga click pe **Environment variables**
2. Click pe **Edit**
3. Click pe **Add environment variable**
4. **Key:** `CLICK_QUEUE_URL`
5. **Value:** URL-ul SQS copiat la pasul 6 (format: `https://sqs.eu-west-1.amazonaws.com/...`)
6. Click **Save**

---

#### Lambda 3: minilink-process-click

Functions → Create function:
- **Function name:** `minilink-process-click`
- **Runtime:** .NET 8, **Execution role:** `minilink-lambda-role`
- Click **Create function**

**Upload cod:**
- Tab **Code** → Upload → `MiniLink.ProcessClick/src/MiniLink.ProcessClick/function.zip`
- Handler: `MiniLink.ProcessClick::MiniLink.ProcessClick.Function::FunctionHandler`

**Timeout:** 0 min 29 sec

**Environment variables:**
- Tab Configuration → Environment variables → Edit → Add:
  - **Key:** `MILESTONE_TOPIC_ARN`
  - **Value:** ARN-ul SNS copiat la pasul 7
- Save

**Adaugă SQS trigger:**
1. Tab **Configuration** → în meniu stânga click pe **Triggers**
2. Click pe **Add trigger**
3. Dropdown **Select a source** → caută și selectează **SQS**
4. **SQS queue:** din dropdown selectează `minilink-clicks`
5. **Batch size:** `10`
6. Bifează **Report batch item failures** — important, altfel la o eroare parțială se reprocessează tot batch-ul
7. Click **Add**

> **Problemă frecventă — eroare la adăugarea trigger-ului:**
> Dacă primești eroarea `The function execution role does not have permissions to call ReceiveMessage on SQS`, înseamnă că Lambda a creat automat un rol nou (în loc să folosească `minilink-lambda-role`). Fix rapid:
> 1. Tab **Configuration** → **Permissions** → click pe numele rolului (link albastru, format `minilink-process-click-role-XXXXXXX`)
> 2. Se deschide IAM — click pe **Add permissions → Attach policies**
> 3. Caută `AWSLambdaSQSQueueExecutionRole` → bifează → click **Add permissions**
> 4. Revino la Lambda și încearcă din nou să adaugi trigger-ul

---

#### Lambda 4: minilink-cleanup-expired

Functions → Create function:
- **Function name:** `minilink-cleanup-expired`
- **Runtime:** .NET 8, **Execution role:** `minilink-lambda-role`
- Click **Create function**

**Upload cod:**
- Tab **Code** → Upload → `MiniLink.CleanupExpired/src/MiniLink.CleanupExpired/function.zip`
- Handler: `MiniLink.CleanupExpired::MiniLink.CleanupExpired.Function::FunctionHandler`

**Timeout:** 0 min 60 sec (scanarea poate dura mai mult)

(Fără environment variables)

---

### PASUL 9 — API Gateway (~30 min)

**Navighează la API Gateway:**
1. Bara de căutare → scrie `API Gateway` → click pe **API Gateway**
2. Ești pe pagina "APIs" — click pe **Create API** (dreapta sus)
3. Vei vedea mai multe tipuri de API — la **HTTP API** click pe **Build**

---

**Step 1 — Configure API:**

Ești pe pagina "Configure API" cu 4 pași în stânga (Configure API → Configure routes → Define stages → Review and create).

1. **API name:** scrie `minilink-api`
2. **IP address type:** lasă `IPv4` selectat
3. La secțiunea **Integrations** click pe **Add integration**
   - **Integration type:** selectează `Lambda function`
   - **AWS Region:** eu-north-1 (sau region-ul tău)
   - **Lambda function:** din dropdown selectează `minilink-create-link`
4. Click din nou pe **Add integration** pentru a doua funcție:
   - **Integration type:** `Lambda function`
   - **Lambda function:** `minilink-redirect-link`
5. Click **Next**

---

**Step 2 — Configure routes:**

Ești pe pagina "Configure routes — optional". Nu există rute pre-configurate, le adaugi manual.

1. Click pe **Add route**:
   - **Method:** `POST`
   - **Resource path:** `/links`
   - **Integration target:** din dropdown selectează `minilink-create-link`
2. Click pe **Add route** din nou:
   - **Method:** `GET`
   - **Resource path:** `/{shortCode}`
   - **Integration target:** din dropdown selectează `minilink-redirect-link`
3. Click **Next**

---

**Step 3 — Define stages:**

Ești pe pagina "Define stages — optional".

- Lasă `$default` cu **Auto-deploy: enabled** (toggle albastru)
- Click **Next**

---

**Step 4 — Review and create:**

Verifică că apare:
- **API name:** `minilink-api`, IPv4
- **Integrations:** `minilink-create-link`, `minilink-redirect-link`
- **Routes:** `POST /links`, `GET /{shortCode}`
- **Stages:** `$default (Auto-deploy: enabled)`

Click **Create**.

---

**Copiază Invoke URL:**
- Ești acum pe pagina API-ului — în secțiunea **Details** găsești **Invoke URL**
- Format: `https://XXXXXXXX.execute-api.eu-north-1.amazonaws.com`
- Copiaz-o undeva, o vei folosi la CloudFront

---

**Activează CORS:**
1. Meniu stânga → click pe **CORS**
2. Click pe **Configure**
3. **Access-Control-Allow-Origin:** `*`
4. **Access-Control-Allow-Methods:** `*`
5. **Access-Control-Allow-Headers:** `Content-Type`
6. Click **Save**

**Testează din terminal:**
```bash
# Creează un link scurt
curl -X POST https://XXXXXXXX.execute-api.eu-west-1.amazonaws.com/links \
  -H "Content-Type: application/json" \
  -d '{"url":"https://google.com"}'
# Răspuns așteptat: {"shortUrl":"https://placeholder.cloudfront.net/ab12cd"}

# Testează redirect (cu -L urmărește redirect-ul)
curl -L https://XXXXXXXX.execute-api.eu-west-1.amazonaws.com/ab12cd
# Trebuie să ajungă la google.com
```

---

### PASUL 10 — S3 Static Frontend (~20 min)

**Creează fișierele frontend local** (în terminal):

```bash
mkdir -p ~/Dev/learning-for-jobs/MiniLink/frontend
```

**`frontend/index.html`:**
```html
<!DOCTYPE html>
<html lang="ro">
<head>
  <meta charset="UTF-8">
  <title>MiniLink</title>
  <style>
    body { font-family: sans-serif; max-width: 600px; margin: 60px auto; padding: 0 20px; }
    input { width: 100%; padding: 10px; font-size: 16px; margin-bottom: 10px; box-sizing: border-box; }
    button { padding: 10px 20px; font-size: 16px; cursor: pointer; }
    #result { margin-top: 20px; font-size: 18px; }
    #result a { color: #007bff; }
  </style>
</head>
<body>
  <h1>MiniLink</h1>
  <p>Scurtează orice URL instant</p>
  <form id="form">
    <input type="url" id="url" placeholder="https://exemplu.com" required />
    <button type="submit">Scurtează</button>
  </form>
  <div id="result"></div>
  <script>
    const API = '/api'; // proxiat prin CloudFront

    document.getElementById('form').addEventListener('submit', async e => {
      e.preventDefault();
      const url = document.getElementById('url').value;
      document.getElementById('result').textContent = 'Se procesează...';
      try {
        const res = await fetch(`${API}/links`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ url })
        });
        const data = await res.json();
        document.getElementById('result').innerHTML =
          `Link scurt: <a href="${data.shortUrl}" target="_blank">${data.shortUrl}</a>`;
      } catch (err) {
        document.getElementById('result').textContent = 'Eroare. Încearcă din nou.';
      }
    });
  </script>
</body>
</html>
```

**Navighează la S3:**
1. Bara de căutare → scrie `S3` → click pe **S3**
2. Click pe **Create bucket** (buton portocaliu dreapta sus)

**Ești pe pagina "Create bucket" — configurează astfel:**

- **Bucket type:** lasă `General purpose` selectat (primul radio button)
- **Bucket namespace:** lasă `Global namespace` selectat
- **Bucket name:** `minilink-frontend` — dacă primești eroare că numele e luat, adaugă câteva cifre la final ex: `minilink-frontend-6757` (numele trebuie să fie unic global)
- **Copy settings from existing bucket:** lasă gol

- **Object Ownership:** lasă `ACLs disabled (recommended)` selectat

- **Block Public Access settings for this bucket:**
  - Lasă **Block all public access** bifat (checkbox-ul principal)
  - CloudFront va accesa S3 printr-un mecanism special (OAC), nu direct public

- **Bucket Versioning:** lasă `Disable` selectat

- **Default encryption:**
  - Lasă `Server-side encryption with Amazon S3 managed keys (SSE-S3)` selectat
  - **Bucket Key:** lasă `Enable` selectat

- Restul (Tags, Advanced settings) le lași default

- Click **Create bucket** (buton portocaliu jos-dreapta)

**Uploadează index.html:**
1. Click pe bucket-ul nou creat din listă
2. Click pe **Upload** (buton portocaliu dreapta sus)
3. Click pe **Add files**
4. Navighează la `~/Dev/learning-for-jobs/MiniLink/frontend/index.html` și selecteaz-o
5. Click **Upload** (buton portocaliu jos)
6. Aștepți să apară "Upload succeeded" → click **Close**

---

### PASUL 11 — CloudFront (~30 min)

**Navighează la CloudFront:**
1. Bara de căutare → scrie `CloudFront` → click pe **CloudFront**
2. Click pe **Create distribution** (buton portocaliu dreapta sus)

Wizardul are 4-5 pași în stânga: Get started → Specify origin → Enable security → Review and create.

---

**Step 1 — Get started:**

- **Distribution name:** `minilink`
- **Description:** lasă gol
- **Distribution type:** lasă `Single website or app` selectat (primul radio button)
- **Domain** (Route 53 managed domain): lasă gol — nu avem domeniu custom
- **Tags:** lasă gol
- Click **Next**

---

**Step 2 — Specify origin:**

- **Origin type:** lasă `Amazon S3` selectat (primul radio button)
- **S3 origin:** click pe **Browse S3** și selectează bucket-ul `minilink-frontend` creat la pasul 10
- **Origin path:** lasă gol
- **Settings:**
  - Bifează **Allow private S3 bucket access to CloudFront — Recommended** (dacă nu e deja bifat)
  - **Origin settings:** lasă `Use recommended origin settings` selectat
  - **Cache settings:** lasă `Use recommended cache settings tailored to serving S3 content` selectat
- Click **Next**

---

**Step 3 — Enable security:**

- Lasă setările default (WAF poți lăsa dezactivat — costă bani)
- Click **Next**

---

**Step 4 — Review and create:**

- Verifică că apare bucket-ul S3 ca origin
- Click **Create distribution**
- CloudFront va arăta status **"Deploying"** — **durează 3-5 minute**

---

**Setează Default root object (fă asta primul):**

Pe pagina distribution-ului (tab-ul **General** e deja selectat):
1. În secțiunea **Settings** observi că **Default root object** e `-` (gol)
2. Click pe **Edit** (buton dreapta sus în secțiunea Settings)
3. Găsești câmpul **Default root object** → scrie `index.html`
4. Click **Save changes**

---

**Adaugă behavior pentru API Gateway (după ce distribution-ul e creat):**

Acesta e pasul cel mai important — fără el, apelurile `/api/*` nu ajung la Lambda.

**Mai întâi adaugă API Gateway ca origin (tab Origins):**
1. Click pe tab-ul **Origins**
2. Click pe **Create origin**
3. **Origin domain:** `4z9xy96gih.execute-api.eu-north-1.amazonaws.com` (fără `https://`)
4. **Protocol:** HTTPS only
5. Restul setărilor le lași default
6. Click **Create origin**

**Apoi creează behavior-ul (tab Behaviors):**
1. Click pe tab-ul **Behaviors**
2. Click pe **Create behavior**
3. **Path pattern:** `/api/*`
4. **Origin and origin groups:** din dropdown selectează origin-ul API Gateway adăugat mai sus (nu S3!)
5. **Viewer protocol policy:** lasă `Redirect HTTP to HTTPS`
6. **Allowed HTTP methods:** selectează `GET, HEAD, OPTIONS, PUT, POST, PATCH, DELETE` (al treilea radio button)
7. **Cache key and origin requests:** lasă `Cache policy and origin request policy (recommended)`
8. **Cache policy:** din dropdown schimbă în `CachingDisabled` (niciodată nu cachezi apeluri API!)
9. **Function associations:** lasă toate `No association`
10. Click **Create behavior**

---

**Aplică bucket policy pe S3:**
1. Pe pagina distribution-ului caută un banner galben: "The S3 bucket policy needs to be updated" → click pe **Copy policy**
   - Dacă nu e banner, mergi la tab-ul **Origins** → click pe origin-ul S3 → **Edit** → vei vedea butonul **Copy policy**
2. Deschide un tab nou → **S3** → click pe bucket-ul `minilink-frontend` → tab **Permissions**
3. Derulează la **Bucket policy** → click **Edit**
4. Paste-uiește policy-ul copiat (șterge orice era acolo)
5. Click **Save changes**

**Copiază domain-ul CloudFront:**
- Înapoi la CloudFront → pe pagina distribution-ului, în secțiunea Details găsești **Distribution domain name**
- Format: `XXXXXXXXXXXX.cloudfront.net`

**Actualizează SSM `/minilink/base-url`:**
1. Bara de căutare → `Systems Manager` → Parameter Store
2. Click pe `/minilink/base-url` din listă
3. Click pe **Edit**
4. **Value:** `https://XXXXXXXXXXXX.cloudfront.net` (domain-ul CloudFront de mai sus)
5. Click **Save changes**

**Testează frontendul:**
- Deschide în browser: `https://XXXXXXXXXXXX.cloudfront.net`
- Trebuie să apară pagina MiniLink. Încearcă să scurtezi un URL.

---

### PASUL 12 — EventBridge Rule (Cleanup automat) (~10 min)

**Navighează la EventBridge:**
1. Bara de căutare → scrie `EventBridge` → click pe **Amazon EventBridge**
2. Meniu stânga → click pe **Rules**
3. Vei vedea un dropdown "Event bus" — asigură-te că e selectat **default**
4. Click pe **Create rule**

**Configurează rule-ul:**
1. **Name:** `minilink-daily-cleanup`
2. **Rule type:** selectează **Schedule** (al doilea radio button)
3. Click **Continue in EventBridge Scheduler** — **NU**, click pe **Next** de jos (rămâi în Rules, nu merge în Scheduler)
4. La **Schedule pattern** selectează **A fine-grained schedule (cron expression)**
5. **Cron expression:**
   ```
   0 3 * * ? *
   ```
   (zilnic la 3:00 AM UTC — în AWS syntax `?` înlocuiește `*` pentru day-of-week când specifici day-of-month)
6. Click **Next**

**Configurează target:**
1. **Target types:** selectează **AWS service**
2. La **Select a target** din dropdown alege **Lambda function**
3. **Function:** din dropdown selectează `minilink-cleanup-expired`
4. Click **Next** → **Next** → **Create rule**

---

### PASUL 13 — CloudWatch Dashboard + Log Retention (~20 min)

**Setează log retention** (obligatoriu — fără asta logurile cresc la infinit și costă bani):

1. Bara de căutare → scrie `CloudWatch` → click pe **CloudWatch**
2. Meniu stânga → derulează în jos la secțiunea **Logs** → click pe **Log groups**
3. Vei vedea log group-urile Lambda create automat: `/aws/lambda/minilink-create-link`, `/aws/lambda/minilink-redirect-link`, etc.
4. Click pe primul log group
5. Click pe **Actions** (dropdown dreapta sus) → **Edit retention setting**
6. Din dropdown selectează **7 days**
7. Click **Save**
8. Dă click pe **Log groups** din breadcrumb-ul de sus ca să te întorci și repetă pentru celelalte 3

**Creează Dashboard:**
1. Meniu stânga → click pe **Dashboards**
2. Click pe **Create dashboard**
3. **Dashboard name:** `MiniLink` → click **Create dashboard**
4. Apare un popup "Add widget" → selectează **Line** → click **Next**
5. Click pe **Metrics** (tab-ul din stânga)
6. Click pe **Lambda** → **By Function Name**
7. Găsești `minilink-redirect-link` → bifează `Invocations`
8. Click **Create widget**
9. Click pe **Add widget** din nou → selectează **Number** → Next
10. Click pe **SQS** → **By Queue Name** → `minilink-clicks` → bifează `ApproximateNumberOfMessagesVisible`
11. Click **Create widget**
12. Adaugă încă un **Number** widget similar pentru `minilink-clicks-dlq` — dacă e > 0, ceva e broken
13. Click **Save dashboard** (dreapta sus)

**Creează un Alarm pentru erori:**
1. Meniu stânga → **Alarms** → click pe **All alarms**
2. Click pe **Create alarm**
3. Click pe **Select metric**
4. Click pe **Lambda** → **By Function Name**
5. Găsești `minilink-redirect-link` → bifează `Errors` → click **Select metric**
6. **Period:** 5 minutes
7. **Threshold type:** Static
8. **Whenever Errors is:** Greater than `5`
9. Click **Next**
10. La **Notification** → **Send a notification to the following SNS topic** → selectează **Create new topic**
11. **Topic name:** `minilink-ops`
12. **Email endpoints:** emailul tău
13. Click **Create topic**
14. Click **Next**
15. **Alarm name:** `minilink-redirect-errors`
16. Click **Next** → **Create alarm**

---

### PASUL 14 — GitHub Actions CI/CD (~45 min)

**Crează repo pe GitHub** (dacă nu ai deja):
```bash
cd ~/Dev/learning-for-jobs/MiniLink
git init
git remote add origin https://github.com/USERNAME/minilink.git
```

**Creează OIDC provider în AWS (o singură dată, conectezi GitHub cu AWS):**
1. Bara de căutare → `IAM` → meniu stânga → click pe **Identity providers**
2. Click pe **Add provider**
3. **Provider type:** selectează **OpenID Connect**
4. **Provider URL:** `https://token.actions.githubusercontent.com`
5. Click pe **Get thumbprint** — AWS validează URL-ul și completează thumbprint-ul automat
6. **Audience:** `sts.amazonaws.com`
7. Click **Add provider**

**Creează role pentru GitHub Actions:**
1. IAM → meniu stânga → **Roles** → **Create role**
2. **Trusted entity type:** selectează **Web identity**
3. **Identity provider:** din dropdown selectează `token.actions.githubusercontent.com`
4. **Audience:** din dropdown selectează `sts.amazonaws.com`
5. Click **Next**
6. Caută și bifează `AdministratorAccess`
7. Click **Next** → **Role name:** `minilink-github-deploy` → **Create role**

**Restricționează role-ul la repo-ul tău** (editezi trust policy):
1. Click pe `minilink-github-deploy` din lista de roles
2. Click pe tab-ul **Trust relationships**
3. Click pe **Edit trust policy**
4. Găsești în JSON secțiunea `"Condition"` — înlocuiește-o cu:
```json
"Condition": {
  "StringLike": {
    "token.actions.githubusercontent.com:sub": "repo:USERNAME/minilink:*"
  },
  "StringEquals": {
    "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
  }
}
```
5. Înlocuiește `USERNAME` cu username-ul tău de GitHub
6. Click **Update policy**

**Copiază ARN-ul role-ului:**
- Ești pe pagina role-ului — în secțiunea **Summary** găsești **ARN**
- Format: `arn:aws:iam::ACCOUNT_ID:role/minilink-github-deploy`

**Adaugă secret în GitHub repo:**
1. Mergi pe GitHub → repo-ul tău → click pe **Settings** (tab-ul din meniu)
2. Meniu stânga → **Secrets and variables** → **Actions**
3. Click pe **New repository secret**
4. **Name:** `AWS_DEPLOY_ROLE_ARN`
5. **Secret:** ARN-ul copiat mai sus
6. Click **Add secret**

**Crează workflow:**
```bash
mkdir -p ~/Dev/learning-for-jobs/MiniLink/.github/workflows
```

**`.github/workflows/deploy.yml`:**
```yaml
name: Deploy MiniLink

on:
  push:
    branches: [main]

permissions:
  id-token: write
  contents: read

env:
  AWS_REGION: eu-west-1

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Install Lambda tools
        run: dotnet tool install -g Amazon.Lambda.Tools

      - name: Configure AWS via OIDC
        uses: aws-actions/configure-aws-credentials@v4
        with:
          role-to-assume: ${{ secrets.AWS_DEPLOY_ROLE_ARN }}
          aws-region: ${{ env.AWS_REGION }}

      - name: Build & package Lambdas
        run: |
          for proj in CreateLink RedirectLink ProcessClick CleanupExpired; do
            cd src/MiniLink.$proj/src/MiniLink.$proj
            dotnet lambda package -o function.zip
            cd ../../../..
          done

      - name: Deploy CreateLink
        run: |
          aws lambda update-function-code \
            --function-name minilink-create-link \
            --zip-file fileb://src/MiniLink.CreateLink/src/MiniLink.CreateLink/function.zip \
            --region $AWS_REGION

      - name: Deploy RedirectLink
        run: |
          aws lambda update-function-code \
            --function-name minilink-redirect-link \
            --zip-file fileb://src/MiniLink.RedirectLink/src/MiniLink.RedirectLink/function.zip \
            --region $AWS_REGION

      - name: Deploy ProcessClick
        run: |
          aws lambda update-function-code \
            --function-name minilink-process-click \
            --zip-file fileb://src/MiniLink.ProcessClick/src/MiniLink.ProcessClick/function.zip \
            --region $AWS_REGION

      - name: Deploy CleanupExpired
        run: |
          aws lambda update-function-code \
            --function-name minilink-cleanup-expired \
            --zip-file fileb://src/MiniLink.CleanupExpired/src/MiniLink.CleanupExpired/function.zip \
            --region $AWS_REGION

      - name: Deploy frontend to S3
        run: |
          aws s3 sync frontend/ s3://minilink-frontend-CONTUL_TAU/ \
            --delete \
            --region $AWS_REGION

      - name: Invalidate CloudFront cache
        run: |
          aws cloudfront create-invalidation \
            --distribution-id DISTRIBUTION_ID \
            --paths "/*"
```

> **Înlocuiește** `minilink-frontend-CONTUL_TAU` cu numele exact al bucket-ului tău S3 și `DISTRIBUTION_ID` cu ID-ul CloudFront distribution (îl găsești în CloudFront → Distributions — e un cod de 14 caractere, ex: `E1PA6795UKMFR9`).

```bash
git add .
git commit -m "feat: MiniLink initial setup"
git push origin main
```

Mergi pe **GitHub → repo-ul tău → tab Actions** și urmărești pipeline-ul live. Fiecare step devine verde pe rând.

---

### PASUL 15 — Verificare finală (~25 min)

```bash
# 1. Creează un link scurt
curl -X POST https://CLOUDFRONT_DOMAIN/api/links \
  -H "Content-Type: application/json" \
  -d '{"url":"https://anthropic.com"}'

# Copiază shortCode din răspuns, ex: "ab12cd"

# 2. Urmărește redirect-ul
curl -L https://CLOUDFRONT_DOMAIN/ab12cd
# Trebuie să ajungă la anthropic.com

# 3. Verifică frontendul
xdg-open https://CLOUDFRONT_DOMAIN
```

**Checklist în consolă AWS:**

- [ ] **DynamoDB** → Tables → `minilink-links` → tab **Explore items** → trebuie să apară un item cu shortCode
- [ ] **DynamoDB** → Tables → `minilink-clicks` → **Explore items** → trebuie să apară clickul generat de redirect
- [ ] **SQS** → Queues → `minilink-clicks` → în tab **Monitoring** → **Messages available** = 0 (a fost procesat de Lambda)
- [ ] **SQS** → `minilink-clicks-dlq` → **Messages available** = 0 (dacă e > 0, Lambda ProcessClick aruncă erori)
- [ ] **CloudWatch** → Log groups → `/aws/lambda/minilink-redirect-link` → click pe el → vei vedea un **Log stream** recent → click pe el → trebuie să apară loguri
- [ ] **CloudWatch** → Dashboards → `MiniLink` → graficul de Invocations trebuie să arate cel puțin un punct
- [ ] **GitHub** → Actions → pipeline verde

**Testează cleanup manual:**
1. Bara de căutare → Lambda → Functions → click pe `minilink-cleanup-expired`
2. Click pe butonul **Test** (dreapta sus)
3. La **Event name** scrie `test`
4. La **Template** din dropdown selectează **CloudWatch Scheduled Event**
5. Click **Test**
6. Vei vedea rezultatul direct pe pagină — click pe **Details** să vezi log-urile
7. Trebuie să apară: "Găsite X linkuri expirate de șters." și "Cleanup complet."

---

## Recapitulare pentru interviu

### Ce ai construit și ce poți explica

| Serviciu | Ce ai făcut concret | Ce spui la interviu |
|---|---|---|
| **IAM** | Role cu policies, OIDC trust | "Am folosit OIDC în loc de access keys — GitHub primește un JWT temporar de la AWS STS, fără credențiale hardcodate" |
| **DynamoDB** | 2 tabele, TTL, GSI | "Am ales provisioned 1+1 pentru free tier, TTL pentru cleanup automat, GSI pentru query pe timestamp" |
| **SSM** | 2 parametri, citit în Lambda cold start | "Citesc SSM o singură dată per cold start în static constructor, nu per invocație" |
| **Lambda** | 4 funcții .NET, timeout, memory | "Cold start pe .NET 8 e ~800ms. Cu AOT pe PROVIDED_AL2023 scade la ~100ms" |
| **API Gateway** | HTTP API v2, routes, CORS | "Am ales HTTP API v2 în loc de REST API — 70% mai ieftin, latency mai mic, suficient pentru ce am nevoie" |
| **SQS** | Queue + DLQ, visibility timeout, batch failure | "Visibility timeout = 30s >= Lambda timeout. DLQ după 3 eșecuri. SQSBatchResponse pentru partial failures" |
| **SNS** | Topic, email subscription | "Fan-out — un singur publish ajunge la toți subscriberii (email, SQS, Lambda)" |
| **S3** | Bucket privat, fișier static | "Block all public access — S3 niciodată public direct, accesul vine prin CloudFront OAC" |
| **CloudFront** | 2 behaviors: S3 + API Gateway, OAC | "Behavior `/api/*` cu CachingDisabled. OAC în loc de OAI — mai sigur, recomandat din 2022" |
| **EventBridge** | Cron rule la 3 AM | "Syntax AWS: `0 3 * * ? *` — `?` pentru day-of-week când specifici day-of-month" |
| **CloudWatch** | Log retention, Dashboard, Alarm | "Log retention obligatoriu — fără el cresc la infinit. Alarm pe error rate > 5%" |
| **GitHub Actions** | OIDC, build .NET, deploy Lambda | "Pipeline complet: build → package → deploy Lambda + S3 sync + CloudFront invalidation" |

---

## Costuri reale pe Free Tier

| Serviciu | Free Tier | Consum tău | Cost |
|---|---|---|---|
| Lambda | 1M req/lună, 400k GB-s | Câteva sute | $0 |
| API Gateway HTTP | 1M req/lună (12 luni) | Câteva sute | $0 |
| DynamoDB | 25 WCU+RCU provizionat | 2+2 din 25 | $0 |
| S3 | 5GB, 20k GET (12 luni) | Sub 1MB | $0 |
| CloudFront | 1TB transfer (12 luni) | Sub 1MB | $0 |
| SQS | 1M req/lună | Câteva sute | $0 |
| SNS | 1M publish/lună | 0 (pragul de 1000 neacoperit) | $0 |
| SSM Standard | Gratuit | 2 parametri | $0 |
| EventBridge | 14M events/lună | 1/zi = 30/lună | $0 |
| CloudWatch | 10 alarme free | 1 alarmă | $0 |
| **TOTAL** | | | **$0** |

> **ATENȚIE:** Dacă nu mai vrei să plătești nimic, la final șterge tot:
> CloudFront → Disable → Delete | Lambda → Delete functions | API Gateway → Delete | S3 → Empty bucket → Delete bucket | DynamoDB → Delete tables | SQS → Delete queues | SNS → Delete topic | EventBridge → Delete rule | CloudWatch → Delete dashboard + alarms + log groups
