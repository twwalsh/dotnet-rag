# dotnet-rag: one RAG system, built three times

The support assistant on [fruitlessresearch.com](https://fruitlessresearch.com) ("Ask a question" on any page)
is a retrieval-augmented system on Cloudflare: Workers AI embeddings, a Vectorize index, Claude through AI
Gateway. It was [rebuilt on Databricks](https://github.com/twwalsh/databricks-rag) to compare the platforms.
This repo is the **same system a third time, on ASP.NET Core and Postgres**: the same 219 chunks, the same
system prompt, the same 30-question evaluation set, so the three can be compared on the one question that
matters for a support bot, *does it answer correctly from the documents and refuse everything else*.

It runs on .NET 10 with EF Core, pgvector and Postgres full-text search, calls Claude through the official
C# SDK, embeds with Azure OpenAI (or, for a like-for-like retrieval comparison, the same Workers AI model the
site uses), reports traces and metrics over OpenTelemetry, and runs on Azure Container Apps from a Bicep
template. **Try it at [twwalsh.com/ask-dotnet](https://twwalsh.com/ask-dotnet/).** It is a working demo, not the
production assistant; that stays on Cloudflare, where the site runs.

## What runs where

| Step | Cloudflare build | Databricks build | **.NET build (this repo)** |
|---|---|---|---|
| Corpus | chunked from public sources by an allow-listed script | the same chunks, exported | the same chunks, `data/chunks.jsonl` |
| Storage | chunk text inside vector metadata | Unity Catalog volume + Delta table | **Postgres**: one `chunks` table, EF Core migrations |
| Embeddings | Workers AI `bge-base-en-v1.5` (768-d) | Databricks-managed `gte-large-en` (1024-d) | **`text-embedding-3-small`** (1536-d) via Azure OpenAI in Azure or OpenAI directly elsewhere; or Workers AI bge-base, by configuration |
| Index / search | Vectorize, vector only | Vector Search Delta Sync, hybrid | **pgvector cosine + Postgres `tsvector`, fused by reciprocal rank** |
| Licensing pin | top 3 FAQ entries on a licensing-shaped question | identical, by filter | identical, by `source = 'faq'` |
| Generation | Claude Haiku via AI Gateway, streamed; `show_form` tool | Llama 3.3 70B via a Unity Gateway service; tagged last line | **Claude Haiku via the official C# SDK; `show_form` tool, prompt caching** |
| Ingest | script, run by hand | serverless job (DAB) | **a `BackgroundService`, nightly and on an empty table; idempotent by content hash and embedding model** |
| Evaluation | `support-eval.mjs` | MLflow GenAI scorers | **`SupportRag.Eval`, in-process, same rules, exit code gate** |
| Telemetry | AI Gateway logs | inference table + MLflow traces | **OpenTelemetry traces, metrics and logs → OTLP, with the GenAI semantic conventions on the agent, embedding and chat spans; running under [Dynatrace](dynatrace/README.md)** |
| Deployment | `wrangler deploy` | Declarative Automation Bundle | **Dockerfile + `infra/main.bicep` → Azure Container Apps, managed identity to Azure OpenAI** |

## Layout

```
data/chunks.jsonl              the corpus, exported from the site's chunker (make export)
data/eval.jsonl                the 30 evaluation cases, copied verbatim
src/SupportRag.Core/           the pipeline, host-agnostic
  Options/RagOptions.cs          every non-secret setting, validated on start
  Data/                          Chunk + RagDbContext (pgvector column, generated tsvector, GIN index)
  Embeddings/                    IEmbedder; Azure OpenAI through Microsoft.Extensions.AI; Workers AI over REST
  Retrieval/                     Retriever (hybrid + licensing pin), ReciprocalRankFusion
  Generation/                    Prompts (the site's prompt, verbatim), Answerer (one Messages call), Answer.Fold
  Ingest/                        IngestPlan (the diff, pure), CorpusIngestor, NightlyIngestService
  Pipeline/SupportAssistant.cs   retrieve → refuse on empty → answer → sources
  Telemetry/Instrumentation.cs   one ActivitySource, one Meter, the gen_ai.* attribute names
src/SupportRag.Api/            minimal API: POST /ask, POST /admin/ingest, GET /healthz; rate limiting; OpenTelemetry
src/SupportRag.Eval/           the eval runner (and --ingest for a fresh database)
tests/SupportRag.Tests/        xunit: the pure parts
infra/main.bicep               Container Apps + Azure OpenAI + Postgres Flexible Server, declared (+ the OTLP endpoint and token)
dynatrace/README.md            the assistant under Dynatrace: wiring, trace shape, DQL for a dashboard, one alert
Dockerfile · Makefile · .github/workflows/ci.yml
```

## Run it

Needs the .NET 10 SDK, a Postgres with pgvector (Postgres.app bundles it on a Mac), an Anthropic API key, and
one of: a Cloudflare account for Workers AI, an OpenAI API key, or an Azure OpenAI resource with an embedding
deployment. Secrets are read from the environment; `.env.op` shows the 1Password references used here. For
Workers AI on a laptop, wrangler's own login token is enough:
`CLOUDFLARE_API_TOKEN=$(cd ../fruitless-research/site && npx wrangler auth token)`.

```bash
make db                                   # createdb + CREATE EXTENSION vector
op run --env-file=.env.op -- make run     # migrates, ingests on first boot, serves on :5080
make ask Q="Where do I put the .frlicense file on a Mac?"
op run --env-file=.env.op -- make eval    # 30 cases; exit 1 below 80 % strict
dotnet test
```

## Results

Run 2026-10-30, Claude Haiku 5.5 as the generator, Workers AI bge-base as the embedder (the live site's), local
Postgres 18 with pgvector 0.8.6 (`eval/latest.json` holds every answer with its passages and token counts):

| Scorer | .NET build | Databricks build | Cloudflare build |
|---|---|---|---|
| must-say rules met | 30/30 | 29/30 | 29/30 |
| must-not-say rules met | 30/30 | 30/30 | 30/30 |
| right self-service form offered | 30/30 | 30/30 | 30/30 |
| **strict pass (all rules of a case)** | **30/30** | **29/30** | **29/30** |

Mean 2.2 s per question; 55.6k input tokens for the 30 (34.5k of them served from the prompt cache), 8.9k out.

**The first run was 28/30, and the two misses were the same bug.** Both answers stopped at exactly 700
output tokens, the cap the Worker uses. Claude 5 models think adaptively by default, the thinking counts
against `max_tokens`, and on those two questions the model thought for 356 and 550 tokens before writing: one
answer lost its `show_form` call to the cap, the other lost most of its text. The cap is now 2000, the
response's stop reason is a span tag and a `rag.truncations` counter, and a re-run was 30/30. The Worker, at
700 with the same model, is exposed to the same failure on a long think; that finding goes back to it.

Idempotency, measured: the second `--ingest` on the same file reported `219 unchanged, 0 embedded` in 0.1 s.

## Design notes

- **The prompt and the limits are shared, the mechanics are not.** Three builds, one system prompt, one
  eval set, one set of retrieval limits (top 6, 3 pinned, 8 passages). Everything a reader sees differ
  between the columns above is a platform decision, not a wording one.
- **Hybrid in plain Postgres.** Databricks turns hybrid search on with a flag; here it is two queries (cosine
  over pgvector, `websearch_to_tsquery` over a generated `tsvector`) and a reciprocal-rank merge in C#. The
  merge is a pure function with tests, and it is where you can see what each half contributes to a result.
- **Idempotent ingest, as a type.** `IngestPlan.Compute` takes what the file says and what the table holds and
  returns what to embed and what to delete. Same file and same embedding model twice → an empty plan. A model
  change → everything re-embeds, with no migration, because the vector column has no fixed dimension.
- **Count the thinking.** The one real defect the eval caught was not retrieval or prompting but a budget: an
  answer cap chosen for a model that did not think, applied to one that does. The fix is a number, the lesson
  is to watch `stop_reason` in production, which the instrumentation now does.
- **Refuse before you generate.** All three builds learned the same lesson on the same day: against an empty
  index the model answers from general knowledge however firmly the prompt forbids it. No passages, no call.
- **The provider boundary is the embedding, not the model.** `IEmbedder` makes the one asymmetry explicit
  (bge wants a query prefix, OpenAI models do not), and swapping Azure OpenAI for Workers AI is a config
  value, which is how the retrieval comparison with the live site is made like for like.
- **Secrets never touch the repo.** Locally they come from 1Password at run time (`op run`); in Azure the
  Anthropic key is a Container Apps secret and the embedding call uses the app's managed identity.

## Deployed

Live since 2026-10-30 on Azure, behind a front door at [twwalsh.com/ask-dotnet](https://twwalsh.com/ask-dotnet/)
(a Cloudflare Worker route with Turnstile that carries the question to the app and the answer back, the same
shape as the Databricks demo's door). `infra/main.bicep`, deployed twice as its header says:

- **Container Apps** (East US), scale to zero, one replica, the image built *in* the registry with `az acr build`
  because the laptop has no Docker; the app pulls it with a **user-assigned identity** (AcrPull) and calls
  **Azure OpenAI** with the same identity (Cognitive Services OpenAI User). No registry password, no OpenAI key.
- **Azure OpenAI** `text-embedding-3-small`, so the deployed index differs from the local Workers AI one only in
  the embedding model, and the ingest knew: on first boot it embedded all 219 chunks under the new model id.
- **Postgres Flexible Server** B1ms in **Central US**, not East US, because this subscription gets
  "Provisioning is restricted in this region" for East US and `SkuNotAvailable` for East US 2 while the SKU
  listing advertises both. The region is a parameter for that reason. A failed create also keeps the server
  name reserved for a while, hence the unique suffix.
- **Scored over HTTP** with `SupportRag.Eval --api <url>`: **30/30 strict**, 2.2 s a question, 47.9k input tokens
  (34.5k from the prompt cache). The eval runner now waits out the app's own per-IP rate limit, which it tripped
  at question 21, as a client should.

Secrets: the Anthropic key and the admin key are Container Apps secrets; the Postgres password lives in
1Password and in the app's connection-string secret. Nothing is in the repo or the CI configuration.

## Observed

The point of a support assistant in production is not the demo answer but knowing, a month later, whether it
is still answering, what it costs and when it is quietly refusing. The API ships traces, metrics and logs over
OTLP with the [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/):
one trace per question, four spans deep (`invoke_agent` → `rag.retrieve` → `embeddings …` + the two Postgres
halves, then `chat claude-haiku-5-5` with model, finish reason, input/output/cache-read tokens), and an
`rag.outcome` of `answered`, `form`, `handoff` or `refused` on the agent span. Those attributes are what an AI
observability backend keys on, so no vendor SDK is involved; the backend is three environment variables.
**It runs under Dynatrace's AI Observability**, wired, queried and alerted as [dynatrace/README.md](dynatrace/README.md)
describes. First runs, 2026-11-02: the 30-case eval from a laptop through the exporter, 29/30 strict (one
missed `show_form` call, the model's, not the telemetry's), then the Azure instance on the same build, **30/30
strict, 2.18 s a question**, every question a trace in the tenant under `deployment.environment.name = azure`.

## Not built (yet)

- Streaming. The Worker streams; this API returns the whole answer, as the Databricks build does.
- Conversation turns. One question in, one answer out; the Worker carries up to six turns.
