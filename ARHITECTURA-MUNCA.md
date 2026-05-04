# Arhitectura la muncă — Dell Software Bundles pe AWS
## Migrare Azure → AWS | 30 servicii | .NET

---

## 1. Contextul sistemului

Sistemul gestionează **software bundle-urile incluse la cumpărarea unui calculator Dell**.

**Flow principal:**
```
User cumpără Dell PC
  → contul de utilizator e creat în sistem
  → user se autentifică pe portalul Dell (SSO/OAuth)
  → sistemul verifică device-ul (serial number, BIOS ID)
  → apare lista de beneficii: licențe software, trial-uri, servicii
  → activarea fiecărui benefit trimite un event în sistem
```

**De ce e interesant:** Nu e un simplu CRUD — e un sistem event-driven cu 30 de servicii care trebuie să fie consistent, auditable, și să scaleze la volume de device registrations (lansări de modele noi = spike masiv).

---

## 2. Ce aveai în Azure și echivalentul în AWS

| Azure | AWS | Note |
|---|---|---|
| **Azure Service Bus** | **SQS** sau **MSK (Kafka)** | Decizia e critică — vezi Secțiunea 4 |
| **Azure Functions** | **Lambda** (.NET) | Același model event-driven |
| **Azure SQL / CosmosDB** | **RDS** sau **DynamoDB** | Depinde de structura datelor |
| **Azure Blob Storage** | **S3** | Deja migrat (BIOS files) |
| **Azure AD B2C** | **Cognito** | Auth pentru useri |
| **Azure API Management** | **API Gateway** | HTTP API v2 |
| **Azure Monitor** | **CloudWatch** | Logs + Alarms |
| **Azure DevOps** | **GitHub Actions + CodeDeploy** | CI/CD |

---

## 3. SQS vs Kafka — Decizia arhitecturală

### Când alegi **SQS**

**SQS e corect pentru scenariile tale când:**

- Mesajul e procesat **o singură dată** de **un singur consumer** (point-to-point)
- Ordinea nu contează sau contează doar în FIFO queue
- Nu ai nevoie să "refaci" procesarea de acum 3 zile
- Serviciul tău e **request/response async** (activezi un benefit → procesezi → gata)
- Vrei **zero management** — SQS e fully managed, nu ai nimic de configurat

**Exemplu concret la Dell:**
```
User activează beneficiul Norton → Lambda trimite în SQS →
ProcessBenefit Lambda îl citește → scrie în DB → trimite email confirmare
```
Dacă Lambda cade, mesajul rămâne în SQS (visibility timeout). Dacă pică de 3 ori → DLQ → alertă.

**Cost SQS:** ~$0.40 per 1 milion de mesaje. La 30 de servicii cu trafic normal = câțiva dolari/lună.

---

### Când alegi **Kafka (MSK pe AWS)**

**Kafka e corect pentru scenariile tale când:**

- Ai **multiple servicii care citesc același mesaj** (fan-out real, nu copii)
- Ai nevoie de **replay** — "procesează din nou toate activările din ultimele 7 zile"
- Ai **ordering strict pe un device serial number** (toate evenimentele unui device în ordine)
- Ai **volume foarte mari** (>10k mesaje/secundă) și vrei throughput predictibil
- Vrei **audit log imutabil** — Kafka e log-ul, nu doar o coadă

**Exemplu concret la Dell:**
```
Device registered event → intră în Kafka topic "device-events" →
  - ServiceA (benefits eligibility) citește și calculează ce beneficii primești
  - ServiceB (analytics) citește și actualizează dashboardul Dell
  - ServiceC (warranty) citește și înregistrează garanția
  - ServiceD (telemetry) citește și trimite la data lake
Toți citesc același event, independent, fără să se afecteze.
```

**Cost MSK (Kafka managed):** ~$200-500/lună pentru un cluster mic (3 brokeri m5.large). Nu e ieftin.

---

### Decizia recomandată pentru sistemul vostru

```
SQS Standard → pentru procesare simplă benefit activation (1 producer, 1 consumer)
SQS FIFO     → dacă ordinea contează per user (ex: upgrade/downgrade nu pot fi inversate)
Kafka (MSK)  → doar pentru evenimentele care trebuie citite de 3+ servicii simultan
               ex: "device registered" care declanșează 5 fluxuri diferite
```

**Sfatul practic:** Dacă ai migrat deja și funcționează, nu schimba ce merge. SQS e mai simplu de operat. Kafka adaugă complexitate reală (partiții, consumer groups, offset management). Alege Kafka doar dacă ai un use-case clar de replay sau fan-out care nu se rezolvă cu SNS+SQS.

---

## 4. Scalare în AWS — Cum faci și cât costă

### Lambda — Scalare automată

Lambda scalează **automat și instant** — fiecare request primește o instanță separată.

```
Ai 1 request/secundă  → 1 instanță Lambda
Ai 1000 request/sec   → 1000 instanțe Lambda (paralel, automat)
Ai 10000 request/sec  → 10000 instanțe (cu burst limit regional de 3000 la start)
```

**Limitele reale:**
- **Concurrency limit default:** 1000 per cont per regiune (poți cere mărire)
- **Burst limit:** 3000 instanțe noi în primul minut, apoi +500/minut după aceea
- **Reserved concurrency:** poți rezerva X instanțe pentru o funcție critică (ex: redirect-ul)
- **Provisioned concurrency:** instanțe pre-încălzite permanent (elimină cold start, costă mai mult)

**Exemplu practic:** La lansarea unui model Dell nou, dacă 50.000 de useri se înregistrează simultan:
- Primele 3000 de requesturi sunt instant (burst limit)
- Restul au un lag de câteva secunde până Lambda scalează
- Soluție: SQS ca buffer în față — Lambda ProcessBenefits citește la ritmul său, userii nu simt nimic

---

### Lambda — Cold Start vs Warm Pool vs Provisioned

#### Scenariul 1: Lambda "singur" (fără configurare specială)

```
Request vine → AWS caută o instanță caldă
  → Există una caldă → răspuns în ~10-50ms (execuție pură)
  → Nu există → COLD START: ~800-1200ms pentru .NET 8
                             (~100ms pentru .NET Native AOT)
```

**Cât costă cold start-ul?** Zero extra — plătești oricum pentru durata execuției. Problema e UX (latency pentru primul request).

**Când apare cold start?**
- Prima invocație după deploy
- Dacă nu ai trafic >15 minute (AWS dezalocă instanța)
- Când scalezi mai sus decât instanțele existente

---

#### Scenariul 2: Lambda în "pool comun" (fără reserved concurrency)

Asta e default-ul. Lambda-urile tale împart pool-ul global de capacitate al contului cu alte funcții.

```
Contul tău: 1000 concurrency limit
  minilink-redirect:   poate folosi 0-1000
  minilink-create:     poate folosi 0-1000
  minilink-process:    poate folosi 0-1000
  (suma lor nu poate depăși 1000 simultan)
```

**Problema:** Dacă cleanup-ul tău face un scan masiv și consumă 800 de concurrency slots, redirect-ul rămâne cu 200. La spike → throttling (429).

**Cost:** $0 extra față de execuție normală.

---

#### Scenariul 3: Reserved Concurrency (izolare garantată)

```csharp
// În IaC (CDK):
redirectLambda.addReservedConcurrentExecutions(200);
// Asta garantează că redirect-ul are mereu 200 slots
// Și asta LIMITEAZĂ și la maxim 200 (poți folosi ca throttle intenționat)
```

**Cost:** $0 extra — rezervezi din pool-ul existent, nu plătești mai mult.

**Când folosești:** Când ai o funcție critică (redirect, auth) care nu trebuie afectată de altele.

---

#### Scenariul 4: Provisioned Concurrency (pre-încălzit, fără cold start)

```
Setezi: provisioned concurrency = 10 pentru minilink-redirect
→ AWS menține 10 instanțe .NET deja pornite și gata
→ Primele 10 requesturi simultane: 0ms cold start
→ Al 11-lea request: cold start normal (dacă nu e alt warm)
```

**Cost real:**
```
Provisioned Concurrency pricing (eu-west-1):
  $0.000004646 per GB-secundă de concurrency provizionată
  $0.000009646 per GB-secundă execuție (mai ieftin decât normal $0.0000166667)

Exemplu: 10 instanțe × 512MB × 3600 secunde/oră × 24h × 30 zile
= 10 × 0.5GB × 2,592,000 sec/lună
= 12,960,000 GB-sec/lună × $0.000004646
= ~$60/lună pentru 10 instanțe pre-încălzite
```

**Când merită:** Când ai SLA strict și cold start-ul de 1 secundă e inacceptabil (ex: redirect-ul Dell trebuie să fie instant pentru UX).

---

### Tabel comparativ costuri Lambda

| Configurare | Cold Start | Cost extra/lună | Când folosești |
|---|---|---|---|
| Default (pool comun) | 800-1200ms .NET | $0 | Dev, funcții non-critice |
| Reserved concurrency | 800-1200ms .NET | $0 | Izolare, nu cold start |
| Provisioned (10 inst.) | 0ms | ~$60 | SLA strict, funcții critice |
| Native AOT .NET | 50-100ms | $0 | Cold start redus, build mai complex |

---

## 5. Cum tai costurile la 30 de servicii

### Principii generale

**1. Right-size Lambda memory**
```
Memory afectează și CPU-ul alocat (liniar).
512MB = 0.5 vCPU, 1024MB = 1 vCPU, 3008MB = 3 vCPU
Costul e per GB-sec, deci dacă dublezi memory dar execuția se înjumătățește → același cost.
Folosește AWS Lambda Power Tuning (open source) să găsești sweet spot-ul.
```

**2. SQS batch processing**
```csharp
// În loc să procesezi 1 mesaj odată (1 invocație Lambda per mesaj):
BatchSize = 10  // 1 invocație Lambda pentru 10 mesaje
// De 10x mai puține invocații, același cost per invocație
// La 1M mesaje/zi: 100k invocații în loc de 1M → de 10x mai ieftin
```

**3. DynamoDB On-Demand vs Provisioned**
```
Provisioned: plătești pentru capacitate rezervată (RCU/WCU), ieftin la trafic predictibil
On-Demand:   plătești per request, scump la trafic mare, dar $0 când nu ai trafic
Regula:      > 30% utilizare → Provisioned mai ieftin
             Spike-uri imprevizibile → On-Demand (nu throttle niciodată)
```

**4. RDS vs DynamoDB pentru datele voastre**
```
Datele relaționale (user → device → beneficii → activări):
  → RDS Aurora Serverless v2 scalează la 0 ACU când nu e trafic (ideal dev/staging)
  → Economie: ~$50/lună vs $150/lună pentru RDS standard

Date simple (events, logs): DynamoDB cu TTL — expiră automat, nu plătești stocare veche
```

**5. CloudFront cache pentru S3 (BIOS files)**
```
Fișierele BIOS sunt statice — o dată uploadate, nu se schimbă.
CloudFront le cachează în edge locations.
S3 GetRequest: $0.0004/1000 requests
CloudFront: $0.0085/10k requests DAR cache hit = 0 cost S3
La 1M downloads BIOS: S3 direct = $400, prin CloudFront = ~$20 (95% cache hit rate)
```

**6. Lambda Graviton2 (ARM)**
```
Schimbi Architecture: x86_64 → arm64 în Lambda settings
20% mai ieftin, 19% mai performant (pentru workload-uri .NET tipice)
Risc: testezi că codul compilat pentru ARM rulează corect (.NET 8 suportă complet)
```

---

## 6. Arhitectura completă — Dell Software Bundles pe AWS

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           INTRARE UTILIZATORI                               │
└─────────────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────┐
│   CloudFront    │  CDN global, HTTPS, WAF opțional
│   Distribution  │
└────────┬────────┘
         │
    ┌────┴──────────────────────────────┐
    │                                   │
    ▼                                   ▼
┌──────────┐                  ┌──────────────────┐
│  S3      │                  │   API Gateway    │
│ Frontend │                  │   HTTP API v2    │
│ (React/  │                  │                  │
│  static) │                  └────────┬─────────┘
└──────────┘                           │
                                       │ Routes:
                          ┌────────────┼────────────┐
                          │            │             │
                          ▼            ▼             ▼
                   ┌──────────┐ ┌──────────┐ ┌──────────────┐
                   │  Lambda  │ │  Lambda  │ │    Lambda    │
                   │  Auth/   │ │  Device  │ │   Benefits   │
                   │  Login   │ │  Register│ │   Catalog    │
                   │  .NET 8  │ │  .NET 8  │ │   .NET 8     │
                   └────┬─────┘ └────┬─────┘ └──────┬───────┘
                        │            │               │
                        ▼            ▼               │
                  ┌──────────────────────┐           │
                  │      Cognito         │           │
                  │  User Pools + JWKS   │           │
                  └──────────────────────┘           │
                                                     │
┌────────────────────────────────────────────────────┘
│
│  EVENT PIPELINE (inima sistemului)
│
▼
┌──────────────────────────────────────────────────────────────────────┐
│                                                                      │
│   Device Registration → SQS "device-registered"                     │
│                            │                                         │
│                            ├──→ Lambda ProcessDevice                 │
│                            │      → scrie în RDS (device + user)     │
│                            │      → publică în SNS "device-ready"    │
│                            │                                         │
│   SNS "device-ready" → fan-out:                                      │
│      │                                                               │
│      ├──→ SQS → Lambda BenefitsEligibility                           │
│      │           → calculează ce beneficii primești după model       │
│      │           → scrie în DynamoDB (benefits-cache)                │
│      │                                                               │
│      ├──→ SQS → Lambda WarrantyRegister                              │
│      │           → înregistrează garanția la Dell                    │
│      │                                                               │
│      └──→ SQS → Lambda TelemetryIngest                               │
│                  → trimite la Kinesis Data Firehose → S3 → Athena    │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│  BENEFIT ACTIVATION (când userul activează un software)              │
│                                                                      │
│  User click "Activate Norton" → API Gateway → Lambda ActivateBenefit │
│    → validare eligibilitate din DynamoDB (fast, ~5ms)                │
│    → SendMessage la SQS "benefit-activations" (async)                │
│    → return 202 Accepted imediat                                     │
│                                                                      │
│  SQS "benefit-activations" → Lambda ProcessActivation (batch=10)     │
│    → call extern la Norton API (sau Dell Partner API)                │
│    → update DynamoDB: status=activated                               │
│    → SNS publish → email confirmare via SES                          │
│    → dacă eșuează de 3 ori → DLQ → alertă CloudWatch                │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│  BIOS FILES (ce vine în S3)                                          │
│                                                                      │
│  S3 bucket "dell-bios-files"                                         │
│    → S3 Event Notification → Lambda ProcessBios                      │
│    → extrage metadata din fișier (model, versiune)                   │
│    → actualizează catalog în DynamoDB                                │
│    → trimite notificare update la device-urile afectate via SNS      │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│  BAZE DE DATE                                                        │
│                                                                      │
│  RDS Aurora Serverless v2 (PostgreSQL)                               │
│    → users, devices, orders, benefits_assignments                    │
│    → relații clare, joins, tranzacții ACID                           │
│    → scalare automată 0.5 → 128 ACU                                  │
│                                                                      │
│  DynamoDB                                                            │
│    → benefits-cache (access pattern: getByDeviceId, TTL 1h)          │
│    → activation-status (getByUserId + shortCode, high read)          │
│    → session-data (TTL auto-expire după 24h)                         │
│                                                                      │
│  ElastiCache Redis (opțional)                                        │
│    → dacă DynamoDB latency nu e suficient pentru catalog lookup      │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│  OBSERVABILITATE                                                     │
│                                                                      │
│  CloudWatch Logs → Log Insights → dashboards                        │
│  X-Ray distributed tracing → vezi unde e latency                     │
│  CloudWatch Alarms → SNS → PagerDuty/Slack                           │
│  Alarms critice:                                                     │
│    - DLQ depth > 0 (ceva pică)                                       │
│    - Lambda error rate > 1% (ceva e broken)                          │
│    - RDS CPU > 80% (trebuie scale up)                                │
│    - SQS age of oldest message > 5 min (consumer e lent)             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 7. De ce SQS și nu Kafka în scenariul Dell

**La Dell ați ales corect SQS pentru:**

| Use case | De ce SQS e suficient |
|---|---|
| Benefit activation | Un singur consumer (ProcessActivation), nu ai nevoie de replay |
| Device registration | Point-to-point, procesezi o dată |
| Email notifications | SES e deja async, SQS e bufferul perfect |
| BIOS file processing | S3 Events → Lambda, nici măcar SQS nu e necesar |

**Kafka (MSK) ar aduce valoare DACĂ:**
- Ai nevoie să reprocessezi toate activările unui model (recall software buggy)
- Ai 5+ servicii care citesc același event de device_registered
- Analytics-ul trebuie să vadă exact ce s-a întâmplat și în ce ordine, la orice moment din trecut

**Costul real al alegerii:**
```
SQS:  ~$5-20/lună pentru 30 servicii cu trafic normal
MSK:  ~$200-600/lună pentru cluster minim (3 brokeri) + storage + transfer

Dacă nu ai USE CASE CLAR pentru Kafka → SQS câștigă mereu.
```

---

## 8. Cum scalezi 30 de servicii .NET pe Lambda

### Pattern recomandat: Shared VPC, separate Lambda functions

```
Nu pune tot codul într-o singură Lambda mare.
Fiecare serviciu = funcție Lambda separată.
Avantaje:
  - Scalare independentă (BenefitsEligibility poate fi la 0, ActivateBenefit la 500)
  - Deploy independent (nu trebuie să redeployezi toate pentru un bugfix)
  - IAM permissions minime per funcție
  - Cost tracking per serviciu (CloudWatch per-function metrics)
```

### Lambda Layers pentru cod comun

```csharp
// În loc să copiezi codul de logging, retry, DB helper în toate cele 30 de servicii:
// Creezi un Lambda Layer cu DLL-urile comune

// Layer: Dell.Common.Layer
// Conține: IDeviceRepository, IBenefitRepository, LoggingExtensions, RetryPolicies
// Toate cele 30 de Lambda-uri referențiază același layer
// Update-ul layer-ului nu necesită redeployul funcțiilor (dacă nu schimbi interfețele)
```

### Cold Start strategy pentru 30 servicii

```
Servicii critice (redirect, auth, catalog) → Provisioned Concurrency 5-10 instanțe
Servicii async (email, telemetry, cleanup) → Default (cold start ok, nu e user-facing)
Servicii rare (BIOS processing) → Default + Native AOT pentru start rapid

Cost estimat pentru servicii critice:
  5 funcții × 5 instanțe × 512MB × 730h/lună × $0.0000046/GB-s
  = 5 × 5 × 0.5 × 730 × 3600 × $0.0000046
  ≈ $150/lună pentru zero cold start pe funcțiile critice
```

---

## 9. Pattern .NET interesant în Lambda — din codul MiniLink

### Static clients — reutilizarea conexiunilor între invocații

```csharp
// DIN MINILINK - pattern CORECT:
public class Function
{
    // Static = creat o singură dată per container Lambda, refolosit între invocații
    private static readonly AmazonDynamoDBClient _dynamo = new();
    private static readonly AmazonSQSClient _sqs = new();
    
    // SSM citit o singură dată per cold start (nu per invocație)
    private static string? _baseUrl;
    
    public async Task<...> FunctionHandler(...)
    {
        if (_baseUrl == null)  // lazy init, thread-safe în Lambda (1 thread per invocație)
        {
            // Citit o singură dată, cached în memorie pentru toate invocațiile viitoare
            var r = await _ssm.GetParameterAsync(...);
            _baseUrl = r.Parameter.Value;
        }
    }
}
```

**De ce contează:** Fără static, fiecare invocație Lambda creează un nou HTTP client, face handshake TCP + TLS cu DynamoDB, citește SSM. Cu static, conexiunile se reutilizează (connection pooling), SSM e citit o singură dată. La 1000 req/sec, diferența e masivă.

### Fire-and-forget async — din RedirectLink

```csharp
// DIN MINILINK - redirect rapid, tracking async:
_ = _sqs.SendMessageAsync(new SendMessageRequest { ... });
// Nu așteptăm SQS să confirme — returnăm 301 imediat
// Dacă SQS pică (extrem rar), pierdem un click — tradeoff acceptabil
// User-ul e redirectat în <50ms în loc de <100ms
```

**Varianta mai sigură pentru producție (Dell):**
```csharp
// Trimite SQS în background task, returnezi răspunsul
// Dar dacă Lambda se termină înainte ca Task-ul să finalizeze → pierzi mesajul
// Soluție: folosești context.RemainingTime să știi dacă mai ai timp
var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromMilliseconds(500));
await _sqs.SendMessageAsync(request, cts.Token);
```

### SQSBatchResponse — partial failure handling

```csharp
// DIN MINILINK ProcessClick - pattern corect pentru batch:
public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ...)
{
    var failures = new List<SQSBatchResponse.BatchItemFailure>();
    
    foreach (var record in sqsEvent.Records)
    {
        try { /* procesare */ }
        catch (Exception ex)
        {
            // DOAR mesajul care a eșuat merge înapoi în SQS
            // Celelalte 9 din batch sunt procesate și șterse
            failures.Add(new() { ItemIdentifier = record.MessageId });
        }
    }
    
    return new SQSBatchResponse { BatchItemFailures = failures };
}
// Fără asta: dacă 1 din 10 mesaje eșuează, toate 10 sunt re-trimise → duplicate procesare
```

---

## 10. Ce ai putea adăuga la arhitectura Dell

### 1. Step Functions pentru fluxuri complexe multi-step

```
Benefit activation e simplu (1 pas).
Dar "onboarding complet al unui device nou" are 8 pași:
  1. Validare device serial number cu Dell API
  2. Fetch user account
  3. Calculate benefits eligibility
  4. Register warranty
  5. Send welcome email
  6. Create telemetry record
  7. Notify partner integrations
  8. Update device status

Cu Lambda chain: dacă pasul 5 pică, pierzi starea. Retry manual, debugging coșmar.
Cu Step Functions: workflow vizual, retry automat per pas, stare persistată, compensating transactions.
Cost: $0.025/1000 state transitions = neglijabil.
```

### 2. EventBridge pentru integrări externe

```
Dell Headquarters trimite un event "new_model_launched" →
EventBridge rule → fan-out la toate serviciile relevante:
  - BenefitsService: adaugă beneficiile noului model în catalog
  - TelemetryService: începe să accepte telemetrie pentru noul hardware
  - SupportService: activează knowledge base pentru model
  
Fiecare serviciu e decuplat — nu știe de celelalte.
```

### 3. Cognito cu Custom Authorizer Lambda

```
Cognito JWT → Lambda Authorizer → API Gateway
Authorizer verifică:
  - JWT valid (semnat de Cognito)
  - User are device înregistrat
  - Device e eligibil pentru benefits (cache în DynamoDB TTL 5 min)
Rezultatul e cached de API Gateway 5 minute → nu mai chemi Lambda Authorizer la fiecare request
```

---

## Rezumat rapid pentru interviu

**Ce ai migrat:**
- Azure Service Bus → SQS (procesare simplă) + SNS (fan-out)
- Azure Functions → Lambda .NET 8 (același model, API diferit)
- Azure SQL → RDS Aurora Serverless v2 sau DynamoDB (după use case)
- Azure Blob → S3 (trivial)

**De ce SQS în loc de Kafka:**
- Ai procesare point-to-point, nu fan-out masiv
- Nu ai nevoie de replay
- $20/lună vs $400/lună

**Cum scalezi:**
- Lambda scalează automat, fără configurare
- SQS ca buffer absorbe spike-urile (device registration la lansare model)
- Provisioned Concurrency pe funcțiile critice ($150/lună pentru zero cold start)
- DynamoDB pentru date cu access pattern clar (benefits cache, session data)
- RDS Aurora Serverless v2 pentru date relaționale (scalează la 0 când nu e trafic)

**Cum tai costurile:**
- Lambda ARM (Graviton2): 20% mai ieftin
- Batch processing SQS (batch=10): 10x mai puține invocații
- CloudFront pentru S3: 95%+ cache hit → 20x mai ieftin pe transferuri
- DynamoDB TTL: date vechi expirate automat, nu plătești stocare
- Log retention 7-30 zile: CloudWatch Logs nu cresc la infinit
