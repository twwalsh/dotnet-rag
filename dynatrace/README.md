# The assistant under Dynatrace

The API exports traces, metrics and logs over OTLP, attributed with the OpenTelemetry GenAI semantic
conventions, so Dynatrace's AI Observability app sees the model call, the embedding call and the
agent-level request without a vendor SDK. Nothing here is Dynatrace-specific except the endpoint, the
header and delta temporality for metrics.

## Wiring

| Variable | Value |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `https://<env>.live.dynatrace.com/api/v2/otlp` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` (Dynatrace does not take OTLP/gRPC) |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=Api-Token <token>` — the whole line is the secret |
| `DEPLOYMENT_ENVIRONMENT` | `azure`, `laptop`, … → `deployment.environment.name` |

The token needs three scopes: `openTelemetryTrace.ingest`, `metrics.ingest`, `logs.ingest`. In Azure the
header line is a Container Apps secret (`otlp-headers`) and the two plain values are env vars; `infra/main.bicep`
takes them as `otlpEndpoint` and `otlpToken`. Locally, with the token in 1Password:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=https://<env>.live.dynatrace.com/api/v2/otlp
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Api-Token $(op read 'op://Private/<item>/token')"
op run --env-file=.env.op -- make run
```

## What a question looks like

One trace per question, four spans deep:

```
POST /ask                                   ASP.NET Core
└─ invoke_agent support-assistant           gen_ai.operation.name=invoke_agent, rag.outcome, rag.passages, rag.sources
   ├─ rag.retrieve                          rag.retrieval.kind=hybrid, rag.top_score, rag.pinned
   │  ├─ embeddings text-embedding-3-small  gen_ai.operation.name=embeddings, gen_ai.provider.name=azure.ai.openai
   │  ├─ (Npgsql) SELECT … cosine           the vector half
   │  └─ (Npgsql) SELECT … tsquery          the keyword half
   └─ chat claude-haiku-5-5                 gen_ai.operation.name=chat, gen_ai.provider.name=anthropic,
                                            gen_ai.usage.input_tokens / output_tokens / cache_read.input_tokens,
                                            gen_ai.response.finish_reasons, rag.truncated
```

`rag.outcome` is the thing to watch: `answered`, `form` (a self-service form was offered), `handoff` (the
answer sent the visitor to a person), `refused` (nothing retrieved, no model call). A support assistant that
is working well has a high `answered` share on questions the docs cover and a *non-zero* `handoff` share on
the rest; a `refused` spike means the index, not the model.

Metrics (delta, one minute): `rag.questions`, `rag.refusals`, `rag.forms_offered{form}`, `rag.truncations`,
`rag.tokens.input|output|cache_read`, `rag.answer.duration{rag.outcome}`, `rag.retrieval.top_score`, and the
two GenAI client histograms `gen_ai.client.token.usage{gen_ai.token.type}` and `gen_ai.client.operation.duration`.

## Queries (DQL) for a dashboard or a notebook

Latency, cost and quality for the assistant, in six tiles. Paste into Notebooks first; the attribute names
are the span attributes above as Grail stores them.

```sql
-- 1. Questions per outcome
fetch spans
| filter gen_ai.agent.name == "support-assistant" and gen_ai.operation.name == "invoke_agent"
| summarize questions = count(), by: {rag.outcome}

-- 2. End-to-end latency, p50 / p95, over time
fetch spans
| filter gen_ai.operation.name == "invoke_agent"
| makeTimeseries p50 = percentile(duration, 50), p95 = percentile(duration, 95)

-- 3. Model latency vs retrieval latency
fetch spans
| filter gen_ai.operation.name in ("chat", "embeddings") or span.name == "rag.retrieve"
| summarize p95 = percentile(duration, 95), calls = count(), by: {span.name}

-- 4. Tokens per answer and the prompt-cache hit share
fetch spans
| filter gen_ai.operation.name == "chat"
| summarize input = sum(gen_ai.usage.input_tokens), output = sum(gen_ai.usage.output_tokens),
            cached = sum(gen_ai.usage.cache_read.input_tokens), answers = count()
| fieldsAdd input_per_answer = input / answers, cache_share = cached / input

-- 5. Retrieval quality: best similarity per question (low = the docs did not cover it)
fetch spans
| filter span.name == "rag.retrieve"
| makeTimeseries top_score = avg(rag.top_score), interval: 5m

-- 6. Truncations and errors
fetch spans
| filter gen_ai.operation.name == "chat"
| summarize truncated = countIf(rag.truncated == true), errors = countIf(isNotNull(error.type)), calls = count()
```

Metric versions of the same, for a tile that must survive span retention:

```sql
timeseries questions = sum(rag.questions), refusals = sum(rag.refusals)
timeseries tokens = sum(gen_ai.client.token.usage), by: {gen_ai.token.type}
timeseries duration = avg(rag.answer.duration), by: {rag.outcome}
```

## The alert

One anomaly detector is enough to show the shape: **refusal share**. In Settings → Anomaly detection →
Metric events, a custom event on `rag.refusals` divided by `rag.questions` over 5 minutes, static threshold
above 0.3, 3 of 5 samples. A refusal is the fail-closed path (nothing retrieved → no model call), so a
jump means the index is empty or stale, which is an operations problem, not a model problem, and the
nightly ingest is the first suspect.

## Verified

- 2026-11-02, laptop → the tenant: the 30-case eval over HTTP against the local API (29/30 strict; the miss was
  a `show_form` call the model skipped). Services showed `support-rag` as an ASP.NET service with `POST /ask`
  at 0 % failures, p50 2.02 s, throughput matching the run.
- 2026-11-02, Azure Container Apps → the tenant: revision 3 on image `8a87f70` with the three variables above;
  the same eval against the public URL, **30/30 strict, 2.18 s a question, 47.9k input tokens (33.3k from the
  prompt cache)**, `deployment.environment.name = azure`.
- The tenant is a 15-day trial. When it lapses the exporter will get 4xx responses and drop batches; the app is
  unaffected. Remove the three `OTEL_EXPORTER_OTLP_*` variables (or point them elsewhere) at that point.
