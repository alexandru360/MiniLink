# Arhitectura — Procesare XML Spitale pe AWS
## S3 + Lambda per spital + DynamoDB deduplication + SQL Server on-premise

---

## Contextul sistemului

Spitalele client uploadează XML-uri de dimensiuni mari în S3 (fiecare în bucket-ul propriu).
Fiecare spital trimite XML-urile în formatul lui propriu — nu există un standard comun.
Un Lambda dedicat per spital parsează XML-ul și salvează datele în SQL Server on-premise.
Clienții citesc datele procesate direct din SQL Server.

---

## Diagrama arhitecturală completă

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         UPLOAD SPITALE                                  │
└─────────────────────────────────────────────────────────────────────────┘

  Spital A ──→ S3 Bucket: hospital-a-uploads/
                  └── 2024-05-01/patients_export.xml  (huge)

  Spital B ──→ S3 Bucket: hospital-b-uploads/
                  └── data/20240501_full.xml

  Spital N ──→ S3 Bucket: hospital-n-uploads/
                  └── export.xml

  (fiecare bucket are S3 Event Notification configurat spre Lambda-ul propriu)

┌─────────────────────────────────────────────────────────────────────────┐
│                         EVENT TRIGGER                                   │
└─────────────────────────────────────────────────────────────────────────┘

  S3 Event (ObjectCreated) ──→ Lambda hospital-a-processor
  S3 Event (ObjectCreated) ──→ Lambda hospital-b-processor
  S3 Event (ObjectCreated) ──→ Lambda hospital-n-processor

┌─────────────────────────────────────────────────────────────────────────┐
│                    LAMBDA FLOW (identic per spital)                     │
└─────────────────────────────────────────────────────────────────────────┘

  Lambda hospital-X-processor:

    1. Primește S3 event (bucket + key)
    2. ── DEDUPLICATION ──────────────────────────────────────────────────
       │  DynamoDB PutItem cu ConditionExpression:
       │    "attribute_not_exists(fileKey)"
       │    Item: { fileKey, hospitalId, status=PROCESSING, startedAt }
       │
       │  Dacă condiția eșuează → fișierul e deja procesat/în procesare
       │  → oprire, nu procesăm de două ori
       └────────────────────────────────────────────────────────────────
    3. Download stream XML din S3 (streaming, nu load complet în memorie)
    4. Parsare XML cu XmlReader (streaming pentru fișiere huge)
    5. Batch insert în SQL Server on-premise
    6. DynamoDB UpdateItem: status=PROCESSED, finishedAt, rowsInserted
    7. Dacă eroare: DynamoDB UpdateItem: status=FAILED, errorMessage
                    → mesaj în DLQ → alertă CloudWatch

┌─────────────────────────────────────────────────────────────────────────┐
│                    CONECTIVITATE ON-PREMISE                             │
└─────────────────────────────────────────────────────────────────────────┘

  Lambda (în VPC privat)
    │
    ├── AWS Site-to-Site VPN  ──→  Rețeaua internă spital/companie
    │   sau Direct Connect              │
    │                                   └── SQL Server on-premise
    │
    └── Lambda e în subnet privat (nu are internet access direct)
        Internet access prin NAT Gateway (pentru S3, DynamoDB via endpoints)

  Alternativă fără VPN:
    Lambda ──→ API on-premise (REST/gRPC) ──→ SQL Server
    (dacă compania expune un endpoint securizat extern)

┌─────────────────────────────────────────────────────────────────────────┐
│                    DynamoDB — SCHEMA DETALIATĂ                         │
└─────────────────────────────────────────────────────────────────────────┘

  Tabel: xml-processing-state

  Partition key: fileKey  (ex: "hospital-a-uploads/2024-05-01/patients.xml")

  Atribute:
    hospitalId    → "hospital-a"
    status        → PROCESSING | PROCESSED | FAILED
    checksum      → MD5/ETag din S3 (deduplication pe conținut, nu doar nume)
    startedAt     → Unix timestamp
    finishedAt    → Unix timestamp (null dacă în procesare)
    rowsInserted  → numărul de rânduri salvate în SQL Server
    errorMessage  → mesajul de eroare dacă status=FAILED
    ttl           → expiră automat după 90 zile (nu ții state vechi la infinit)

  GSI: hospitalId-startedAt-index
    → query "toate fișierele spitalului A din ultima săptămână"
    → dashboard procesare per spital

┌─────────────────────────────────────────────────────────────────────────┐
│                    CLOUDFORMATION — CE PROVIZIONA                      │
└─────────────────────────────────────────────────────────────────────────┘

  Un CloudFormation Stack per spital (sau nested stacks):

  Resources per spital:
    - S3 Bucket (hospital-X-uploads) + Event Notification
    - Lambda Function (hospital-X-processor)
      + IAM Role (S3 read, DynamoDB read/write, VPC access)
      + Environment Variables (connection string, hospital config)
      + VPC Config (subnet IDs, security group)
      + Timeout: 15 min (max Lambda)
      + Memory: 1024MB+ (XML parsing e memory-intensive)
    - SQS DLQ (hospital-X-dlq)
    - CloudWatch Alarm pe DLQ depth > 0

  Shared Stack (o singură dată):
    - DynamoDB Table (xml-processing-state)
    - VPC + Subnets private + NAT Gateway
    - VPN Connection to on-premise
    - CloudWatch Dashboard

┌─────────────────────────────────────────────────────────────────────────┐
│                    CITIRE DATE — CLIENȚI                               │
└─────────────────────────────────────────────────────────────────────────┘

  Clienți (aplicații web/desktop)
    │
    └──→ SQL Server on-premise (direct, prin rețeaua internă)
         Datele sunt deja procesate și normalizate de Lambda
```

---

## Codul Lambda .NET — Pattern pentru XML huge + Deduplication

### Deduplication atomică cu DynamoDB Conditional Write

```csharp
public class Function
{
    private static readonly AmazonDynamoDBClient _dynamo = new();
    private static readonly AmazonS3Client _s3 = new();
    private static readonly string _connectionString =
        Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")!;
    private static readonly string _hospitalId =
        Environment.GetEnvironmentVariable("HOSPITAL_ID")!;

    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        foreach (var record in s3Event.Records)
        {
            var fileKey = record.S3.Object.Key;
            var bucket  = record.S3.Bucket.Name;
            var etag    = record.S3.Object.ETag; // checksum din S3

            // DEDUPLICATION — write atomică, eșuează dacă fileKey există deja
            try
            {
                await _dynamo.PutItemAsync(new PutItemRequest
                {
                    TableName = "xml-processing-state",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["fileKey"]    = new() { S = fileKey },
                        ["hospitalId"] = new() { S = _hospitalId },
                        ["status"]     = new() { S = "PROCESSING" },
                        ["checksum"]   = new() { S = etag },
                        ["startedAt"]  = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                        ["ttl"]        = new() { N = DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds().ToString() },
                    },
                    // Dacă fileKey există deja → ConditionalCheckFailedException → skip
                    ConditionExpression = "attribute_not_exists(fileKey)"
                });
            }
            catch (ConditionalCheckFailedException)
            {
                context.Logger.LogWarning($"Fișier deja procesat sau în procesare: {fileKey}");
                continue;
            }

            try
            {
                var rowsInserted = await ProcessFile(bucket, fileKey, context);
                await UpdateStatus(fileKey, "PROCESSED", rowsInserted: rowsInserted);
                context.Logger.LogInformation($"Procesat {fileKey}: {rowsInserted} rânduri");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Eroare {fileKey}: {ex.Message}");
                await UpdateStatus(fileKey, "FAILED", errorMessage: ex.Message);
                throw; // re-throw → mesaj merge în DLQ după 3 încercări
            }
        }
    }

    private async Task<int> ProcessFile(string bucket, string key, ILambdaContext context)
    {
        // Streaming download din S3 — nu încarcă tot fișierul în memorie
        var response = await _s3.GetObjectAsync(bucket, key);

        await using var stream = response.ResponseStream;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });

        var batch   = new List<PatientRecord>(500);
        var total   = 0;

        // XmlReader = streaming, citește nod cu nod (nu încarcă DOM-ul întreg)
        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "Patient")
                continue;

            var xml     = await reader.ReadOuterXmlAsync();
            var patient = ParsePatient(xml); // logica specifică fiecărui spital
            batch.Add(patient);

            // Batch insert la fiecare 500 rânduri — nu faci 1 INSERT per rând
            if (batch.Count >= 500)
            {
                await BulkInsert(batch);
                total += batch.Count;
                batch.Clear();

                context.Logger.LogInformation($"Progres: {total} rânduri inserate...");

                // Verifici dacă mai ai timp (Lambda timeout = 15 min)
                if (context.RemainingTime < TimeSpan.FromMinutes(2))
                    throw new TimeoutException($"Lambda aproape de timeout după {total} rânduri");
            }
        }

        if (batch.Count > 0)
        {
            await BulkInsert(batch);
            total += batch.Count;
        }

        return total;
    }

    private async Task BulkInsert(List<PatientRecord> records)
    {
        // SqlBulkCopy pentru insert masiv — mult mai rapid decât INSERT row by row
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = "dbo.Patients",
            BatchSize = 500,
            BulkCopyTimeout = 60
        };

        var table = ToDataTable(records);
        await bulkCopy.WriteToServerAsync(table);
    }

    private async Task UpdateStatus(string fileKey, string status,
        int? rowsInserted = null, string? errorMessage = null)
    {
        var updates = new Dictionary<string, AttributeValueUpdate>
        {
            ["status"]     = new() { Action = AttributeAction.PUT, Value = new() { S = status } },
            ["finishedAt"] = new() { Action = AttributeAction.PUT,
                                     Value = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() } },
        };

        if (rowsInserted.HasValue)
            updates["rowsInserted"] = new() { Action = AttributeAction.PUT,
                                              Value = new() { N = rowsInserted.Value.ToString() } };
        if (errorMessage != null)
            updates["errorMessage"] = new() { Action = AttributeAction.PUT,
                                              Value = new() { S = errorMessage } };

        await _dynamo.UpdateItemAsync("xml-processing-state",
            new Dictionary<string, AttributeValue> { ["fileKey"] = new() { S = fileKey } },
            updates);
    }
}
```

---

## Problemele reale pe care le-ai întâmpinat (și soluțiile)

### Problema 1 — XML-uri huge, Lambda timeout

```
Fișier de 2GB XML → Lambda cu 512MB memory → OutOfMemoryException

Soluție aplicată:
  XmlReader (streaming) în loc de XDocument.Load() care încarcă tot în memorie
  Memory Lambda: 1024MB sau 2048MB (crești CPU odată cu memory)
  Timeout Lambda: 15 minute (maximul)
  Batch insert: SqlBulkCopy în loc de INSERT row by row
```

### Problema 2 — Același fișier uploadat de două ori de spital

```
Spitalul A uploadează același XML de două ori (accident sau retry manual)
→ fără deduplication: date duplicate în SQL Server

Soluție: DynamoDB Conditional Write
  attribute_not_exists(fileKey) → atomică, nu există race condition
  Al doilea upload → ConditionalCheckFailedException → skip silențios
```

### Problema 3 — Spitalul trimite XML malformat

```
Lambda aruncă XmlException la parsare
→ fără DLQ: mesajul e pierdut, nimeni nu știe

Soluție:
  Re-throw exception din Lambda
  SQS DLQ după 3 retry-uri
  CloudWatch Alarm pe DLQ depth > 0
  DynamoDB status=FAILED cu errorMessage
  Dashboard per spital → vezi exact care fișiere au eșuat și de ce
```

### Problema 4 — Conectivitate la SQL Server on-premise

```
Lambda nu poate ajunge la SQL Server direct (e on-premise, nu în AWS)

Soluție: Lambda în VPC privat + Site-to-Site VPN
  Lambda → VPC subnet privat → VPN tunnel → rețea internă → SQL Server

Alternativă dacă VPN nu era disponibil:
  Lambda → NAT Gateway → API endpoint expus de companie (HTTPS) → SQL Server
  (mai puțin eficient, latency mai mare per batch)
```

---

## CloudFormation — structura template-ului per spital

```yaml
# hospital-processor-template.yaml (parametrizat per spital)
Parameters:
  HospitalId:
    Type: String         # ex: hospital-a
  SqlConnectionString:
    Type: AWS::SSM::Parameter::Value<String>
    Default: /hospitals/hospital-a/sql-connection  # secretul în SSM, nu hardcodat

Resources:

  HospitalBucket:
    Type: AWS::S3::Bucket
    Properties:
      BucketName: !Sub "${HospitalId}-uploads"
      NotificationConfiguration:
        LambdaConfigurations:
          - Event: s3:ObjectCreated:*
            Function: !GetAtt ProcessorLambda.Arn

  ProcessorLambda:
    Type: AWS::Lambda::Function
    Properties:
      FunctionName: !Sub "${HospitalId}-processor"
      Runtime: dotnet8
      Handler: HospitalProcessor::HospitalProcessor.Function::FunctionHandler
      Timeout: 900        # 15 minute
      MemorySize: 2048    # 2GB pentru XML-uri huge
      VpcConfig:
        SubnetIds: !Ref PrivateSubnets
        SecurityGroupIds: !Ref LambdaSecurityGroup
      Environment:
        Variables:
          HOSPITAL_ID: !Ref HospitalId
          SQL_CONNECTION_STRING: !Ref SqlConnectionString
      DeadLetterConfig:
        TargetArn: !GetAtt DLQ.Arn

  DLQ:
    Type: AWS::SQS::Queue
    Properties:
      QueueName: !Sub "${HospitalId}-dlq"
      MessageRetentionPeriod: 1209600  # 14 zile

  DLQAlarm:
    Type: AWS::CloudWatch::Alarm
    Properties:
      AlarmName: !Sub "${HospitalId}-dlq-not-empty"
      MetricName: ApproximateNumberOfMessagesVisible
      Namespace: AWS/SQS
      Dimensions:
        - Name: QueueName
          Value: !GetAtt DLQ.QueueName
      ComparisonOperator: GreaterThanThreshold
      Threshold: 0
      EvaluationPeriods: 1
```

---

## Rezumat servicii și rolul fiecăruia

| Serviciu | Rol în arhitectură |
|---|---|
| **S3** | Stocare XML-uri, câte un bucket per spital, izolare completă |
| **Lambda** | Un procesor dedicat per spital — fiecare știe formatul XML al spitalului lui |
| **DynamoDB** | State tracking (PROCESSING/PROCESSED/FAILED) + deduplication atomică cu Conditional Write |
| **SQL Server on-premise** | Baza de date finală, citită de clienți — Lambda scrie prin VPN |
| **CloudFormation** | Provizionează tot stack-ul per spital (bucket + lambda + DLQ + alarm) dintr-un template parametrizat |
| **SQS DLQ** | Prinde fișierele care au eșuat de 3 ori — fără DLQ ai pierdere silențioasă de date |
| **CloudWatch** | Alarms pe DLQ + dashboard cu status procesare per spital |
| **VPN Site-to-Site** | Conexiune privată Lambda (în VPC) → SQL Server on-premise |
| **SSM Parameter Store** | Connection string-ul SQL Server stocat securizat, nu hardcodat în Lambda |
